#requires -Version 7.0
Set-StrictMode -Version Latest
if (-not ('ModderLords.TestHarness.OwnedPeer' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'OwnedPeer.cs') }
function Read-SharedText([string]$Path) {
    $stream=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
    $reader=[IO.StreamReader]::new($stream)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}
function Start-OwnedSpec([Diagnostics.ProcessStartInfo]$Info,[string]$Directory,[string]$Side,[bool]$Visible=$false) {
    $environment=@{}
    $baseline=[Environment]::GetEnvironmentVariables()
    foreach ($pair in $Info.Environment.GetEnumerator()) {
        if (-not $baseline.Contains($pair.Key) -or [string]$baseline[$pair.Key] -ne $pair.Value) { $environment[$pair.Key]=$pair.Value }
    }
    $removed=@($baseline.Keys | Where-Object { -not $Info.Environment.ContainsKey([string]$_) })
    # The spec contains process environment data: it is local control material, not a diagnostic export.
    $spec=Join-Path $Directory "$Side-control.json"
    @{Visible=$Visible;Executable=$Info.FileName;WorkingDirectory=$Info.WorkingDirectory;Arguments=@($Info.ArgumentList);Environment=$environment;Removed=$removed} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $spec
    $worker=[Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    foreach ($arg in @('-NoProfile','-File',(Join-Path $PSScriptRoot 'PeerWorker.ps1'),'-SpecFile',$spec)) { $worker.ArgumentList.Add($arg) }
    $child=$null
    try {
        $child=[ModderLords.TestHarness.OwnedPeer]::new($worker,(Join-Path $Directory "$Side-stdout.log"),(Join-Path $Directory "$Side-stderr.log"))
        $child.Send('go')
        return @{Side=$Side;Child=$child}
    } catch { if ($child) { $child.Dispose() }; throw }
}

function Read-FixtureEvents([string]$Path, [string]$RunId) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    if ((Get-Item -LiteralPath $Path).Length -gt 524288) { throw 'Fixture event stream exceeds 512 KiB.' }
    $text=Read-SharedText $Path
    # An append may be in flight. Only parse complete records.
    $end=$text.LastIndexOf("`n")
    if ($end -lt 0) { return }
    foreach ($line in $text.Substring(0,$end).Split("`n")) {
        if (-not $line.Trim()) { continue }
        $event=$line | ConvertFrom-Json -AsHashtable -ErrorAction Stop
        if ($event.run -ne $RunId) { throw 'Fixture evidence belongs to another run.' }
        if (-not $event.ContainsKey('kind')) { throw 'Fixture event is missing its kind.' }
        $event
    }
}
function Get-PeerVerdict([object[]]$ServerEvents, [object[]]$ClientEvents, [bool]$Serving) {
    $serverKinds=@($ServerEvents | ForEach-Object { $_.kind })
    $clientKinds=@($ClientEvents | ForEach-Object { $_.kind })
    foreach ($pair in @(@('bootstrap.loaded','server bootstrap'),@('driver.loaded','server driver'),@('server.registered','server registration'))) {
        if ($serverKinds -notcontains $pair[0]) { return @{Status='Pending';Stage=$pair[1]} }
    }
    if (-not $Serving) { return @{Status='Pending';Stage='server serving'} }
    foreach ($pair in @(@('bootstrap.loaded','client bootstrap'),@('driver.loaded','client driver'),@('connection.started','client connection'))) {
        if ($clientKinds -notcontains $pair[0]) { return @{Status='Pending';Stage=$pair[1]} }
    }
    if ($clientKinds -contains 'connection.ended') { return @{Status='Failed';Stage='client connection';Reason='Client returned to the main menu before completion.'} }
    if ($clientKinds -contains 'character.required') { return @{Status='NeedsInput';Stage='character creation';Reason='This player needs a disposable character before command testing.'} }
    if ($clientKinds -contains 'connection.failed') { return @{Status='Failed';Stage='client connection';Reason='Coop refused to start the fixture connection.'} }
    if ($clientKinds -notcontains 'campaign.ready') { return @{Status='Pending';Stage='campaign admission'} }
    $pending=@($ClientEvents | Where-Object kind -eq 'command.pending')
    if ($pending.Count -eq 0) { return @{Status='Pending';Stage='command submission'} }
    if ($pending.Count -ne 1 -or $pending[0].request -notmatch '^[a-fA-F0-9]{32}$') { return @{Status='Failed';Stage='command submission';Reason='Expected one valid fixture request.'} }
    $id=$pending[0].request
    $pendingIndex=[Array]::IndexOf($ClientEvents,$pending[0])
    $campaignIndex=[Array]::IndexOf($clientKinds,'campaign.ready')
    if ($campaignIndex -ge $pendingIndex) { return @{Status='Failed';Stage='event order';Reason='Command preceded campaign readiness.'} }
    $results=@($ClientEvents | Where-Object { $_.kind -in @('result.completed','result.rejected') -and $_.request -eq $id })
    if ($results.Count -eq 0) { return @{Status='Pending';Stage='correlated result'} }
    if ($results.Count -ne 1 -or $results[0].kind -ne 'result.completed' -or $results[0].value -ne '1' -or $results[0].revision -ne '1') {
        return @{Status='Failed';Stage='correlated result';Reason='Expected exactly one completed revision-1 result with value 1.'}
    }
    if ([Array]::IndexOf($ClientEvents,$results[0]) -le $pendingIndex) { return @{Status='Failed';Stage='event order';Reason='Result preceded request submission.'} }
    $executions=@($ServerEvents | Where-Object kind -eq 'counter.executed')
    if ($executions.Count -ne 1 -or $executions[0].value -ne '1') { return @{Status='Failed';Stage='server mutation';Reason='Expected exactly one server counter mutation.'} }
    if (@($ClientEvents | Where-Object { $_.kind -eq 'snapshot.applied' -and $_.value -eq '1' -and [Array]::IndexOf($ClientEvents,$_) -gt $pendingIndex }).Count -eq 0) { return @{Status='Pending';Stage='snapshot application'} }
    return @{Status='Passed';Stage='command and snapshot'}
}
function Get-CharacterPreparationVerdict([object[]]$Events) {
    $kinds=@($Events | ForEach-Object { $_.kind })
    if ($kinds -contains 'connection.failed' -or $kinds -contains 'connection.ended') {
        return @{Status='Failed';Stage='client connection';Reason='Connection ended before character preparation completed.'}
    }
    if ($kinds -contains 'command.pending') { return @{Status='Failed';Stage='character preparation';Reason='Unexpected command during observation.'} }
    if ($kinds -contains 'campaign.ready') { return @{Status='Passed';Stage='character admitted'} }
    return @{Status='Pending';Stage='interactive character creation'}
}
function Test-RegisteredFixturePlayer([string]$Sidecar) {
    if (-not (Test-Path -LiteralPath $Sidecar)) { return @{Ready=$false;Reason='Disposable save has no Coop player sidecar.'} }
    $data=Get-Content -LiteralPath $Sidecar -Raw | ConvertFrom-Json -AsHashtable
    if (-not $data.ContainsKey('Players') -or @($data.Players).Count -eq 0) { return @{Ready=$false;Reason='Disposable save has no registered players; character preparation is required.'} }
    return @{Ready=$true;Reason='Registered players exist; the joining account still must match a valid registration.'}
}
function Test-ServingCheckpoint([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    foreach ($line in (Read-SharedText $Path).Split("`n")) {
        $index=$line.IndexOf('@DS@')
        if ($index -lt 0) { continue }
        try { $event=$line.Substring($index+4) | ConvertFrom-Json -AsHashtable -ErrorAction Stop }
        catch { continue } # stdout may end in a partial write
        if ($event.ev -eq 'state' -and $event.phase -eq 'serving') { return $true }
    }
    return $false
}
function Stop-OwnedPeers([object[]]$Peers) {
    foreach ($entry in @($Peers | Sort-Object { if ($_.Side -eq 'client') {0} else {1} })) {
        $errorText=$null
        try { $entry.Child.Stop($entry.Side -eq 'server') } catch { $errorText=$_.Exception.Message }
        $record=@{Side=$entry.Side;NaturalExit=$entry.Child.ExitBeforeCleanup;Forced=$entry.Child.Forced;CleanupError=$errorText}
        try { $entry.Child.Dispose() } catch { $record.CleanupError=$_.Exception.Message }
        $record
    }
}
function Throw-PeerFailure([string]$Message,[string]$Checkpoint) {
    $exception=[InvalidOperationException]::new($Message)
    $exception.Data['PeerCheckpoint']=$Checkpoint
    throw $exception
}
function Wait-PeerStage([object[]]$Peers, [scriptblock]$Observe, [double]$TimeoutSeconds) {
    $clock=[Diagnostics.Stopwatch]::StartNew()
    $phase='initial evidence'
    while ($true) {
        foreach ($entry in $Peers) {
            if ($entry.Child.Process.HasExited) { Throw-PeerFailure "$($entry.Side) exited during $phase (exit $($entry.Child.Process.ExitCode))." $phase }
            if ($entry.Child.OutputFailure) { Throw-PeerFailure $entry.Child.OutputFailure $phase }
        }
        $verdict=& $Observe
        $phase=$verdict.Stage
        if ($verdict.Status -ne 'Pending') { return $verdict }
        if ($clock.Elapsed.TotalSeconds -ge $TimeoutSeconds) { Throw-PeerFailure "Timeout at $phase. No automatic relaunch." $phase }
        Start-Sleep -Milliseconds 50
    }
}
Export-ModuleMember -Function Get-CharacterPreparationVerdict,Read-FixtureEvents,Get-PeerVerdict,Test-ServingCheckpoint,Stop-OwnedPeers,Wait-PeerStage,Test-RegisteredFixturePlayer,Start-OwnedSpec
