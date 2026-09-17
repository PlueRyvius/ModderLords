#requires -Version 7.0
param([string]$OutputRoot=(Join-Path $PSScriptRoot '..\..\artifacts\peer-harness'))
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'PeerHarness.psm1') -Force
$run=Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$results=[Collections.Generic.List[object]]::new()
function Check([string]$Name,[scriptblock]$Body) {
    try { & $Body; $results.Add(@{Name=$Name;Passed=$true}); Write-Output "PASS $Name" }
    catch { $results.Add(@{Name=$Name;Passed=$false;Error=$_.Exception.Message}); Write-Output "FAIL $Name : $($_.Exception.Message)" }
}
function Assert([bool]$Condition,[string]$Message='Assertion failed') { if (-not $Condition) { throw $Message } }
function Throws([scriptblock]$Body,[string]$Contains) {
    $caught=$null
    try { & $Body | Out-Null } catch { $caught=$_.Exception.Message }
    Assert ($null -ne $caught -and $caught.Contains($Contains)) "Expected failure containing '$Contains', got '$caught'"
}
function New-Peer([string]$Mode,[string]$Name=$Mode,[string]$PidFile='') {
    $info=[Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    foreach($arg in @('-NoProfile','-File',(Join-Path $PSScriptRoot 'FakePeer.ps1'),'-Mode',$Mode,'-PidFile',$PidFile)) { $info.ArgumentList.Add($arg) }
    return Start-OwnedSpec $info $run $Name
}
$id='0123456789abcdef0123456789abcdef'
$server=@(@{kind='bootstrap.loaded'},@{kind='driver.loaded'},@{kind='server.registered'},@{kind='counter.executed';value='1'})
$client=@(@{kind='bootstrap.loaded'},@{kind='driver.loaded'},@{kind='connection.started'},@{kind='campaign.ready'},
    @{kind='command.pending';request=$id},@{kind='result.completed';request=$id;value='1';revision='1'},@{kind='snapshot.applied';value='1'})
Check 'Exact correlated round trip passes' { Assert ((Get-PeerVerdict $server $client $true).Status -eq 'Passed') }
Check 'Listening alone cannot start a client' { Assert ((Get-PeerVerdict @() @() $true).Stage -eq 'server bootstrap') }
Check 'Registration without serving is pending' { Assert ((Get-PeerVerdict $server @() $false).Stage -eq 'server serving') }
Check 'Missing client bootstrap is explicit' { Assert ((Get-PeerVerdict $server @() $true).Stage -eq 'client bootstrap') }
Check 'Character creation needs input' { Assert ((Get-PeerVerdict $server ($client[0..2]+@{kind='character.required'}) $true).Status -eq 'NeedsInput') }
Check 'Character state alone does not prove preparation' { Assert ((Get-CharacterPreparationVerdict @(@{kind='character.required'})).Status -eq 'Pending') }
Check 'Character preparation permits human input until campaign admission' { Assert ((Get-CharacterPreparationVerdict @(@{kind='character.required'},@{kind='campaign.ready'})).Status -eq 'Passed') }
Check 'Disconnected character preparation fails before deadline' { Assert ((Get-CharacterPreparationVerdict @(@{kind='character.required'},@{kind='connection.ended'})).Status -eq 'Failed') }
Check 'Observation cannot submit a command' { Assert ((Get-CharacterPreparationVerdict @(@{kind='campaign.ready'},@{kind='command.pending'})).Status -eq 'Failed') }
Check 'Command run reports disconnect after character entry' { Assert ((Get-PeerVerdict $server ($client[0..2]+@{kind='character.required'}+@{kind='connection.ended'}) $true).Status -eq 'Failed') }
Check 'Deadline exception preserves its actual checkpoint' {
    $caught=$null
    try { Wait-PeerStage @() { @{Status='Pending';Stage='campaign admission'} } 0.01 } catch { $caught=$_.Exception }
    Assert ($null -ne $caught -and $caught.Data['PeerCheckpoint'] -eq 'campaign admission')
}
Check 'Rejected command fails' { Assert ((Get-PeerVerdict $server ($client[0..4]+@{kind='result.rejected';request=$id;value='';revision='0'}) $true).Status -eq 'Failed') }
Check 'Wrong request cannot pass' { Assert ((Get-PeerVerdict $server ($client[0..4]+@{kind='result.completed';request=('f'*32);value='1';revision='1'}) $true).Status -eq 'Pending') }
Check 'Snapshot 10 cannot match snapshot 1' { Assert ((Get-PeerVerdict $server ($client[0..5]+@{kind='snapshot.applied';value='10'}) $true).Stage -eq 'snapshot application') }
Check 'Duplicate mutation fails' { Assert ((Get-PeerVerdict ($server+@{kind='counter.executed';value='2'}) $client $true).Status -eq 'Failed') }
Check 'Duplicate result fails' { Assert ((Get-PeerVerdict $server ($client+$client[5]) $true).Status -eq 'Failed') }
Check 'Result before request cannot pass' { Assert ((Get-PeerVerdict $server ($client[0..3]+$client[5]+$client[4]+$client[6]) $true).Status -eq 'Failed') }
Check 'Old snapshot cannot satisfy a new command' { Assert ((Get-PeerVerdict $server ($client[0..3]+$client[6]+$client[4..5]) $true).Status -eq 'Pending') }
Check 'No registered player blocks unattended command testing' {
    $path=Join-Path $run 'players.json'; [IO.File]::WriteAllText($path,'{"Players":[]}')
    Assert (-not (Test-RegisteredFixturePlayer $path).Ready)
}
Check 'Truncated event waits for newline' {
    $path=Join-Path $run 'partial.jsonl'; [IO.File]::WriteAllText($path,'{"run":"test","kind":"bootstrap.loaded"}')
    Assert (@(Read-FixtureEvents $path 'test').Count -eq 0)
    [IO.File]::AppendAllText($path,"`n"); Assert (@(Read-FixtureEvents $path 'test').Count -eq 1)
}
Check 'Stale run evidence is refused' { Throws { Read-FixtureEvents (Join-Path $run 'partial.jsonl') 'another' } 'another run' }
Check 'Malformed event is refused' {
    $path=Join-Path $run 'bad.jsonl'; [IO.File]::WriteAllText($path,"{broken`n")
    Throws { Read-FixtureEvents $path 'test' } 'JSON'
}
Check 'Startup failure leaves a recorded exception' {
    $info=[Diagnostics.ProcessStartInfo]::new((Join-Path $run 'absent.exe'))
    Throws { [ModderLords.TestHarness.OwnedPeer]::new($info,(Join-Path $run 'bad-out.log'),(Join-Path $run 'bad-err.log')) } 'cannot find'
}
Check 'Live stdout and graceful shutdown' {
    $peer=New-Peer 'Ready' 'server'
    try {
        $null=Wait-PeerStage @($peer) { if (Test-ServingCheckpoint (Join-Path $run 'server-stdout.log')) { @{Status='Passed';Stage='serving'} } else { @{Status='Pending';Stage='serving'} } } 5
        Assert (-not $peer.Child.Process.HasExited)
        $records=@(Stop-OwnedPeers @($peer)); Assert (-not $records[0].Forced); Assert (-not $records[0].CleanupError)
    } finally { $peer.Child.Dispose() }
}
Check 'Natural exit is not reported as harness termination' {
    $peer=New-Peer 'Exit'
    try { Throws { Wait-PeerStage @($peer) { @{Status='Pending';Stage='bootstrap'} } 5 } 'exit 23'; $peer.Child.Stop($false); Assert ($peer.Child.ExitBeforeCleanup -eq 23) }
    finally { $peer.Child.Dispose() }
}
Check 'Hung peer respects deadline and is killed' {
    $peer=New-Peer 'Hang'; $clock=[Diagnostics.Stopwatch]::StartNew()
    try { Throws { Wait-PeerStage @($peer) { @{Status='Pending';Stage='bootstrap'} } 0.2 } 'Timeout at bootstrap'; $peer.Child.Stop($false); Assert $peer.Child.Forced; Assert ($clock.Elapsed.TotalSeconds -lt 5) }
    finally { $peer.Child.Dispose() }
}
Check 'Both output pipes drain under pressure' {
    $peer=New-Peer 'Flood'
    try {
        $null=Wait-PeerStage @($peer) { if (Test-ServingCheckpoint (Join-Path $run 'Flood-stdout.log')) { @{Status='Passed';Stage='serving'} } else { @{Status='Pending';Stage='serving'} } } 10
        Assert ((Get-Item (Join-Path $run 'Flood-stderr.log')).Length -gt 2000000)
    } finally { $peer.Child.Dispose() }
}
Check 'Output overrun fails without unbounded buffering' {
    $peer=New-Peer 'Overrun'
    try {
        Throws { Wait-PeerStage @($peer) { @{Status='Pending';Stage='output limit'} } 10 } 'exceeded 16 MiB'
        Assert ((Get-Item (Join-Path $run 'Overrun-stdout.log')).Length -le 16777216)
    } finally { $peer.Child.Dispose() }
}
Check 'Progress cannot extend the deadline indefinitely' {
    $clock=[Diagnostics.Stopwatch]::StartNew()
    Throws { Wait-PeerStage @() { @{Status='Pending';Stage=[Guid]::NewGuid().ToString()} } 0.2 } 'Timeout at'
    Assert ($clock.Elapsed.TotalSeconds -lt 2)
}
Check 'Exited parent cannot orphan its descendant' {
    $pidFile=Join-Path $run 'child.pid'; $peer=New-Peer 'Spawn' 'spawn-parent' $pidFile; $childId=0
    try {
        $peer.Child.Send('go'); Assert ($peer.Child.Process.WaitForExit(10000))
        $childId=[int][IO.File]::ReadAllText($pidFile); Assert ($null -ne (Get-Process -Id $childId -ErrorAction SilentlyContinue))
        $peer.Child.Stop($false)
        Assert ($null -eq (Get-Process -Id $childId -ErrorAction SilentlyContinue)) 'Descendant survived job closure'
    } finally { $peer.Child.Dispose() }
}
Check 'Cleanup cannot kill an unrelated peer' {
    $one=New-Peer 'Hang' 'one'; $two=New-Peer 'Hang' 'two'
    try { $null=Stop-OwnedPeers @($one); Assert (-not $two.Child.Process.HasExited) }
    finally { $one.Child.Dispose(); $two.Child.Dispose() }
}
@{Tests=$results;GameLaunched=$false} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $run 'results.json')
Write-Output "Evidence: $run"
if (@($results | Where-Object { -not $_.Passed }).Count) { exit 1 }
