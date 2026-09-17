[CmdletBinding()]
param([Parameter(Mandatory)][string]$Workspace,
    [ValidateSet('Preflight','ClientStartup','PrepareCharacter','CommandSnapshot')][string]$Stage='Preflight',
    [int]$StartupSeconds=120, [int]$JoinSeconds=120, [int]$CharacterSeconds=600)
# This entry point also works from Windows PowerShell 5.1; implementation runs in PowerShell 7.
if ($PSVersionTable.PSVersion.Major -lt 7) {
    $pwsh=Get-Command pwsh -ErrorAction SilentlyContinue
    $executable=if ($pwsh) { $pwsh.Source } else { Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe' }
    if (-not (Test-Path -LiteralPath $executable)) { throw 'PowerShell 7 is required; run this script with pwsh.exe.' }
    & $executable -NoProfile -File $PSCommandPath @PSBoundParameters
    exit $LASTEXITCODE
}
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\PeerHarness\PeerHarness.psm1') -Force
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Workspace=[IO.Path]::GetFullPath($Workspace).TrimEnd('\')
$setup=Get-Content -LiteralPath (Join-Path $Workspace 'preparation.json') -Raw | ConvertFrom-Json
foreach ($path in @($setup.Client,$setup.Server,$setup.Data)) {
    $absolute=[IO.Path]::GetFullPath($path)
    if (-not $absolute.StartsWith($Workspace+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Peer paths must remain inside the prepared workspace.' }
    $part=Get-Item -LiteralPath $absolute
    while ($part -and $part.FullName.Length -ge $Workspace.Length) {
        if ($part.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'An isolated peer path resolves through a junction or symbolic link.' }
        $part=$part.Parent
    }
}
if ($StartupSeconds -lt 1 -or $StartupSeconds -gt 180 -or $JoinSeconds -lt 1 -or $JoinSeconds -gt 180) { throw 'Timeouts must be between 1 and 180 seconds.' }
if ($CharacterSeconds -lt 1 -or $CharacterSeconds -gt 900) { throw 'Character timeout must be between 1 and 900 seconds.' }
if ($setup.EnginePort -eq $setup.JoinPort -or $setup.EnginePort -lt 1024 -or $setup.JoinPort -lt 1024 -or $setup.EnginePort -gt 65535 -or $setup.JoinPort -gt 65535) { throw 'Use distinct nonprivileged UDP ports.' }
$clientBin=Join-Path $setup.Client 'bin\Win64_Shipping_Client'
$cli=Join-Path $repo 'src\ModderLords.Cli\bin\Debug\net10.0\ModderLords.Cli.dll'
foreach ($path in @($cli,(Join-Path $clientBin 'Bannerlord.exe'),(Join-Path $Workspace 'fixture-plan.json'))) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing prerequisite: $path" }
}
# Validate the empty fixture envelope with the same trusted, engine-independent encoder as runtime.
Add-Type -Path (Join-Path $repo 'src\ModderLords.Operations\bin\Debug\netstandard2.0\ModderLords.Operations.dll')
$fixturePlan=[ModderLords.Operations.PlanIntegrity]::Parse([IO.File]::ReadAllText((Join-Path $Workspace 'fixture-plan.json')))
$planReason=''
if (-not [ModderLords.Operations.PlanIntegrity]::Verify($fixturePlan,[ref]$planReason) -or $fixturePlan['Contracts'].Count -ne 0) { throw 'Expected an intact empty diagnostic fixture plan.' }
$expectedCoop='90121C59FF8B6D379933CE01D9A4C4BB842FCFE730CD5131C1072D4332A47CF1'
foreach ($path in @((Join-Path $setup.Client 'Modules\CoopNightly\bin\Win64_Shipping_Client\Coop.Core.dll'),(Join-Path $setup.Server 'engine\Modules\Coop\bin\Win64_Shipping_Server\Coop.Core.dll'))) {
    if ((Get-FileHash -LiteralPath $path).Hash -ne $expectedCoop) { throw 'Staged Coop.Core differs from the audited join-adapter target.' }
}
# Validate every staged fixture binary, not just the launcher exe timestamp.
foreach ($modules in @((Join-Path $setup.Client 'Modules'),(Join-Path $setup.Server 'engine\Modules'))) {
    $manifestPath=Join-Path $modules 'ModderLords.OperationFixture\SubModule.xml'
    [xml]$manifest=Get-Content -LiteralPath $manifestPath -Raw
    if ($manifest.Module.SubModules.SubModule.DLLName.value -ne 'ModderLords.OperationFixture.Bootstrap.dll' -or $manifest.SelectNodes('//Tags/Tag').Count -ne 0) {
        throw 'Fixture manifest does not match the audited shared bootstrap.'
    }
    foreach ($side in @('Client','Server')) {
        foreach ($file in @('ModderLords.OperationFixture.dll','ModderLords.OperationFixture.Bootstrap.dll')) {
            $source=Join-Path $repo "tests\ModderLords.OperationFixture\bin\Debug\net472\$file"
            $copy=Join-Path $modules "ModderLords.OperationFixture\bin\Win64_Shipping_$side\$file"
            if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $copy).Hash) { throw "Stale staged fixture: $copy" }
        }
        foreach ($file in @('ModderLords.CompatSync.dll','ModderLords.CompatSync.Coop.dll','ModderLords.Operations.dll')) {
            $source=Join-Path $repo "src\ModderLords.CompatSync\_Module\bin\Win64_Shipping_$side\$file"
            $copy=Join-Path $modules "ModderLords.Compat\bin\Win64_Shipping_$side\$file"
            if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $copy).Hash) { throw "Stale staged runtime: $copy" }
        }
    }
}
$config=Get-Content -LiteralPath (Join-Path $setup.Data 'server-config.json') -Raw | ConvertFrom-Json
if ($config.port -ne $setup.JoinPort -or $config.steam -ne $false -or [string]::IsNullOrEmpty($config.password)) { throw 'Expected a private fixture server on the prepared join port.' }
$playerPrerequisite=Test-RegisteredFixturePlayer (Join-Path $setup.Data ("Game Saves\"+$config.saveName+'.json'))
if ($Stage -eq 'Preflight') {
    @{StartupFilesReady=$true;CommandPrerequisite=$playerPrerequisite;NativeLaunched=$false} | ConvertTo-Json
    return
}
if ($Stage -eq 'CommandSnapshot' -and -not $playerPrerequisite.Ready) { throw $playerPrerequisite.Reason }
if (-not (Get-Process -Name steam -ErrorAction SilentlyContinue)) { throw 'Steam process was not found; verify Steam before a native test. This is not an authentication check.' }
foreach ($port in @($setup.EnginePort,$setup.JoinPort)) {
    $socket=[Net.Sockets.UdpClient]::new()
    try { $socket.ExclusiveAddressUse=$true; $socket.Client.Bind([Net.IPEndPoint]::new([Net.IPAddress]::Any,$port)) } finally { $socket.Dispose() }
}
$runId=[Guid]::NewGuid().ToString('N')
$run=Join-Path $Workspace ('runs\'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+$runId)
New-Item -ItemType Directory -Path $run,(Join-Path $Workspace 'client-data\CoopData') -Force | Out-Null
@([string]$config.port,[string]$config.password) | Set-Content -LiteralPath (Join-Path $run 'connection.txt')
function Start-Peer([string]$Exe,[string[]]$Arguments,[string]$Cwd,[string]$Side) {
    $info=[Diagnostics.ProcessStartInfo]::new($Exe); $info.WorkingDirectory=$Cwd
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    foreach ($key in @('MODDERLORDS_OPERATION_INPUTS','MODDERLORDS_FIXTURE_CONNECTION','MODDERLORDS_FIXTURE_OBSERVE_ONLY')) { $info.Environment.Remove($key) | Out-Null }
    $info.Environment['MODDERLORDS_OPERATION_PLAN']=Join-Path $Workspace 'fixture-plan.json'
    $info.Environment['MODDERLORDS_OPERATION_FIXTURE']='1'
    $info.Environment['MODDERLORDS_FIXTURE_RUN']=$runId
    $info.Environment['MODDERLORDS_FIXTURE_EVENTS']=Join-Path $run "$Side-events.jsonl"
    $info.Environment['MODDERLORDS_FIXTURE_LOG']=Join-Path $run "$Side-fixture.log"
    if ($Side -eq 'client') {
        if ($Stage -in @('CommandSnapshot','PrepareCharacter')) { $info.Environment['MODDERLORDS_FIXTURE_CONNECTION']=Join-Path $run 'connection.txt' }
        if ($Stage -eq 'PrepareCharacter') { $info.Environment['MODDERLORDS_FIXTURE_OBSERVE_ONLY']='1' }
        $info.Environment['BANNERLORD_USER_DIR']=Join-Path $Workspace 'client-data'
        $info.Environment['COOP_DATA_DIR']=Join-Path $Workspace 'client-data\CoopData'
    }
    return Start-OwnedSpec $info $run $Side ($Side -eq 'client' -and $Stage -eq 'PrepareCharacter')
}
$owned=@(); $status='Failed'; $failure=''; $phase='server startup'; $cleanup=@()
try {
    if ($Stage -in @('CommandSnapshot','PrepareCharacter')) {
        # --stop-after is deliberately absent: the CLI treats serving as an early success/stop.
        $server=Start-Peer 'dotnet' @($cli,'launch','--root',$setup.Server,'--data-dir',$setup.Data,'--game',$setup.Client,'--port',"$($setup.EnginePort)",'--compat') $repo 'server'
        $owned+=$server
        $null=Wait-PeerStage $owned {
            $events=@(Read-FixtureEvents (Join-Path $run 'server-events.jsonl') $runId)
            $verdict=Get-PeerVerdict $events @() (Test-ServingCheckpoint (Join-Path $run 'server-stdout.log'))
            if ($verdict.Stage -eq 'client bootstrap') { return @{Status='Passed';Stage='server ready'} }
            return $verdict
        } $StartupSeconds
    }
    $phase='client bootstrap'
    $token='_MODULES_*Native*SandBoxCore*Sandbox*CustomBattle*StoryMode*CoopNightly*ModderLords.Compat*ModderLords.OperationFixture*_MODULES_'
    $client=Start-Peer (Join-Path $clientBin 'Bannerlord.exe') @($token) $clientBin 'client'; $owned+=$client
    Write-Output "Run evidence: $run"
    $clientTimeout=if ($Stage -eq 'PrepareCharacter') { $CharacterSeconds } else { $JoinSeconds }
    $verdict=Wait-PeerStage $owned {
        $events=@(Read-FixtureEvents (Join-Path $run 'client-events.jsonl') $runId)
        if ($Stage -eq 'ClientStartup') {
            if (@($events | Where-Object kind -eq 'menu.ready').Count) { return @{Status='Passed';Stage='client startup'} }
            return @{Status='Pending';Stage='client main menu'}
        }
        if ($Stage -eq 'PrepareCharacter') {
            return Get-CharacterPreparationVerdict $events
        }
        return Get-PeerVerdict @(Read-FixtureEvents (Join-Path $run 'server-events.jsonl') $runId) $events $true
    } $clientTimeout
    if ($Stage -eq 'PrepareCharacter' -and $verdict.Status -eq 'Passed') {
        $phase='persisting character'
        $saveStarted=[DateTime]::UtcNow
        $server.Child.Send('save')
        $verdict=Wait-PeerStage $owned {
            $sidecar=Join-Path $setup.Data ('Game Saves\'+$config.saveName+'.json')
            $save=Join-Path $setup.Data ('Game Saves\'+$config.saveName+'.sav')
            if ((Get-Item -LiteralPath $sidecar).LastWriteTimeUtc -gt $saveStarted -and (Get-Item -LiteralPath $save).LastWriteTimeUtc -gt $saveStarted) {
                try { $registration=Test-RegisteredFixturePlayer $sidecar } catch { return @{Status='Pending';Stage='save writing'} }
                if ($registration.Ready) { return @{Status='Passed';Stage='character registration saved'} }
            }
            return @{Status='Pending';Stage='persisting character'}
        } $StartupSeconds
    }
    $phase=$verdict.Stage; $status=$verdict.Status; $failure=$verdict.Reason
} catch { $failure=$_.Exception.Message; if ($_.Exception.Data.Contains('PeerCheckpoint')) { $phase=[string]$_.Exception.Data['PeerCheckpoint'] } }
finally {
    $cleanup=@(Stop-OwnedPeers $owned)
    if (@($cleanup | Where-Object CleanupError).Count) { $status='Failed'; $failure+=' Cleanup failed; inspect cleanup records.' }
    @{Run=$runId;Stage=$Stage;Status=$status;LastCheckpoint=$phase;Failure=$failure;Cleanup=$cleanup;Reconnect='Pending';ResourceReplication='Pending';ProviderCoexistence='Pending'} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $run 'result.json')
}
Write-Output "Run evidence: $run"
if ($status -ne 'Passed') { throw "$status at ${phase}: $failure" }
