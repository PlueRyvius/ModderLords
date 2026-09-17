#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Workspace = ('D:\Design\Bannerlord Mods\_operation-peers-' + (Get-Date -Format 'yyyyMMdd-HHmmss')),
    [string]$Game = 'D:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord',
    [string]$Coop = 'D:\Program Files (x86)\Steam\steamapps\workshop\content\261550\3770450698',
    [string]$Baseline = 'D:\Design\Bannerlord Mods\_operations-reconciled-proof',
    [int]$EnginePort = 7397,
    [int]$JoinPort = 4397
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Workspace = [IO.Path]::GetFullPath($Workspace)
if (Test-Path -LiteralPath $Workspace) { throw 'Use a new workspace; existing tests are never overwritten.' }
foreach ($root in @($Game,$Coop,$Baseline,$repo)) {
    $absolute = [IO.Path]::GetFullPath($root).TrimEnd('\')
    if ($Workspace.Equals($absolute,[StringComparison]::OrdinalIgnoreCase) -or $Workspace.StartsWith($absolute+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Test workspace must be outside source installations and prior evidence.' }
}
if ($EnginePort -eq $JoinPort -or $EnginePort -lt 1024 -or $JoinPort -lt 1024 -or $EnginePort -gt 65535 -or $JoinPort -gt 65535) { throw 'Use distinct nonprivileged UDP ports.' }
foreach ($port in @($EnginePort,$JoinPort)) {
    $socket = [Net.Sockets.UdpClient]::new()
    try { $socket.ExclusiveAddressUse=$true; $socket.Client.Bind([Net.IPEndPoint]::new([Net.IPAddress]::Any,$port)) }
    finally { $socket.Dispose() }
}
$fixture = Join-Path $repo 'tests\ModderLords.OperationFixture\bin\Debug\net472\ModderLords.OperationFixture.dll'
if (-not (Test-Path -LiteralPath $fixture)) { throw 'Build the original operation fixture first.' }
$sourceSave = Join-Path $Baseline 'data\Game Saves\operation_reconciled_baseline.sav'
if (-not (Test-Path -LiteralPath $sourceSave)) { throw 'The disposable baseline save is missing.' }
New-Item -ItemType Directory -Path $Workspace | Out-Null
function Copy-Tree([string]$From,[string]$To,[string[]]$Exclude=@()) {
    $options = @('/E','/XJ','/R:1','/W:1','/NFL','/NDL','/NJH','/NJS','/NP')
    if ($Exclude.Count) { $options += '/XD'; $options += $Exclude }
    & robocopy $From $To @options | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copy failed: $From" }
}
$client = Join-Path $Workspace 'Client'
foreach ($part in @('bin','Data','GUI','Icons','Shaders','XmlSchemas','Modules\Native','Modules\SandBoxCore','Modules\SandBox','Modules\CustomBattle','Modules\StoryMode')) {
    Write-Output "Preparing client $part"
    Copy-Tree (Join-Path $Game $part) (Join-Path $client $part)
}
foreach ($file in Get-ChildItem -LiteralPath $Game -File) { Copy-Item -LiteralPath $file.FullName -Destination $client }
Copy-Tree $Coop (Join-Path $client 'Modules\CoopNightly') @('DedicatedServer','Logs')
$server = Join-Path $Workspace 'DedicatedServer'
Copy-Tree (Join-Path $Baseline 'DedicatedServer') $server @('Logs')
foreach ($modules in @((Join-Path $client 'Modules'),(Join-Path $server 'engine\Modules'))) {
    Copy-Tree (Join-Path $repo 'src\ModderLords.CompatSync\_Module') (Join-Path $modules 'ModderLords.Compat')
    $destination = Join-Path $modules 'ModderLords.OperationFixture'
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo 'tests\ModderLords.OperationFixture\SubModule.xml') -Destination $destination
    foreach ($side in @('Client','Server')) {
        $bin = Join-Path $destination "bin\Win64_Shipping_$side"
        New-Item -ItemType Directory -Force -Path $bin | Out-Null
        Copy-Item -LiteralPath $fixture -Destination $bin
        Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $fixture) 'ModderLords.OperationFixture.Bootstrap.dll') -Destination $bin
    }
}
$data = Join-Path $Workspace 'data'
New-Item -ItemType Directory -Path (Join-Path $data 'Game Saves') -Force | Out-Null
Copy-Item -LiteralPath $sourceSave -Destination (Join-Path $data 'Game Saves\operation_fixture.sav')
$sidecar=[IO.Path]::ChangeExtension($sourceSave,'.json')
if (Test-Path -LiteralPath $sidecar) { Copy-Item -LiteralPath $sidecar -Destination (Join-Path $data 'Game Saves\operation_fixture.json') }
@{saveName='operation_fixture';port=$JoinPort;password=[Guid]::NewGuid().ToString('N');autosaveMinutes=0;steam=$false;logFile=$true} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $data 'server-config.json')
& dotnet run --project (Join-Path $repo 'tools\OperationFixturePlan\OperationFixturePlan.csproj') -- (Join-Path $Workspace 'fixture-plan.json')
if ($LASTEXITCODE -ne 0) { throw 'Fixture plan generation failed.' }
@{Status='PreparedNotLaunched';Client=$client;Server=$server;Data=$data;EnginePort=$EnginePort;JoinPort=$JoinPort;
    TimeoutSeconds=120;NativeAcceptance='Pending';Prerequisites=@('Client join driver and private token delivery','Verify client save/config isolation','Confirm fixture registered before admission');
    Checkpoints=@('admission-pending','plan-agreed','admission-resumed','client-pending','result-completed','snapshot-1','reconnect-snapshot-1')} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Workspace 'preparation.json')
Write-Output "Prepared isolated peers: $Workspace. No game process started; native acceptance remains pending."
