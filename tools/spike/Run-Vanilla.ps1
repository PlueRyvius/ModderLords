#requires -Version 7.0
<#!
.SYNOPSIS
  Isolated vanilla generation/reload proof. Never launches against the installed package.
.EXAMPLE
  pwsh -File tools/spike/Run-Vanilla.ps1 -Stage All
!#>
[CmdletBinding()]
param(
    [ValidateSet('Setup','Control','Create','Reload','Timeout','All')][string]$Stage = 'All',
    [ValidateSet('Vanilla','Taom','TaomFull')][string]$Recipe = 'Vanilla',
    [string]$Name = ('vanilla_' + (Get-Date -Format 'yyyyMMdd_HHmmss')),
    [string]$Workspace = 'D:\Design\Bannerlord Mods\_vanilla-world-proof',
    [string]$InstalledServer = 'D:\Program Files (x86)\Steam\steamapps\workshop\content\261550\3770450698\DedicatedServer',
    [string]$InstalledGame = 'D:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord',
    [int]$EnginePort = 7299,
    [int]$JoinPort = 4299,
    [int]$TimeoutSeconds = 900,
    [string]$HeadlessScene,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Workspace = [IO.Path]::GetFullPath($Workspace)
$package = Join-Path $Workspace 'DedicatedServer'
$source = Join-Path $Workspace 'source'
$data = Join-Path $Workspace 'data'
$diagnosticGame = Join-Path $Workspace 'Game'
if ($Recipe -ne 'Vanilla' -and $Workspace -eq 'D:\Design\Bannerlord Mods\_vanilla-world-proof') {
    throw 'TAOM requires a separate -Workspace to preserve the vanilla proof.'
}
$cli = Join-Path $source 'src\ModderLords.Cli\bin\Diagnostic\net10.0\ModderLords.Cli.dll'
if ($Workspace -eq [IO.Path]::GetFullPath($InstalledServer) -or $Workspace.StartsWith([IO.Path]::GetFullPath($InstalledServer) + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Diagnostic workspace must be outside the installed server.'
}
New-Item -ItemType Directory -Force -Path $Workspace,$source,$data | Out-Null
function Copy-Tree([string]$From, [string]$To) {
    & robocopy $From $To /E /XJ /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copy failed: $From -> $To ($LASTEXITCODE)" }
}
if (-not (Test-Path (Join-Path $package '.isolated-copy-complete'))) {
    New-Item -ItemType Directory -Force -Path $package | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $InstalledServer -File) { Copy-Item -LiteralPath $file.FullName -Destination $package }
    foreach ($part in @('bin','dotnet','Parameters','XmlSchemas')) {
        Copy-Tree (Join-Path $InstalledServer "engine\$part") (Join-Path $package "engine\$part")
    }
    foreach ($module in @('Native','SandBoxCore','SandBox','Coop','DedicatedServer.Windows')) {
        Copy-Tree (Join-Path $InstalledServer "engine\Modules\$module") (Join-Path $package "engine\Modules\$module")
    }
    Copy-Tree (Join-Path $InstalledServer 'server-data') (Join-Path $package 'server-data')
    Set-Content -LiteralPath (Join-Path $package '.isolated-copy-complete') -Value $InstalledServer
}
if (-not $NoBuild) {
    # Build a source copy: the original module output is linked into the user's installed server.
    $files = & git -C $repo ls-files --cached --others --exclude-standard
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate source files' }
    foreach ($rel in $files) {
        $from = Join-Path $repo $rel
        if (-not (Test-Path -LiteralPath $from -PathType Leaf)) { continue }
        $to = Join-Path $source $rel
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $to) | Out-Null
        Copy-Item -LiteralPath $from -Destination $to -Force
    }
    & dotnet build (Join-Path $source 'src\ModderLords.Cli\ModderLords.Cli.csproj') -c Diagnostic -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Diagnostic build failed' }
}
if ($Recipe -ne 'Vanilla' -and -not (Test-Path (Join-Path $diagnosticGame '.isolated-copy-complete'))) {
    # Real copies keep mod-side logs/config writes away from the installed client modules.
    foreach ($module in @('TAOM.Dependencies','TAOM','TAOM_Map','LOTRLOME_Armory')) {
        Copy-Tree (Join-Path $InstalledGame "Modules\$module") (Join-Path $diagnosticGame "Modules\$module")
    }
    Copy-Tree (Join-Path $InstalledGame 'bin\Win64_Shipping_Client') (Join-Path $diagnosticGame 'bin\Win64_Shipping_Client')
    foreach ($official in @('StoryMode','CustomBattle','SandBox','SandBoxCore','Native','BirthAndDeath')) {
        $relative = "Modules\$official\bin\Win64_Shipping_Client"
        if (Test-Path (Join-Path $InstalledGame $relative)) {
            Copy-Tree (Join-Path $InstalledGame $relative) (Join-Path $diagnosticGame $relative)
        }
    }
    Set-Content -LiteralPath (Join-Path $diagnosticGame '.isolated-copy-complete') -Value $InstalledGame
}
if ($Stage -eq 'Setup') { Write-Output "Ready: $Workspace"; exit 0 }
function Invoke-Vanilla([string]$Kind, [string]$SaveName) {
    $creating = $Kind -in @('Create','Timeout')
    if ($creating -and (Test-Path -LiteralPath (Join-Path $data "Game Saves\$SaveName.sav"))) { throw "Save already exists: $SaveName" }
    if ($Kind -eq 'Reload' -and -not (Test-Path -LiteralPath (Join-Path $data "Game Saves\$SaveName.sav"))) { throw "Cannot reload absent generated save: $SaveName" }
    # Bind briefly to check both UDP ports; never stop a process occupying either port.
    foreach ($port in @($EnginePort,$JoinPort)) {
        $socket = [Net.Sockets.UdpClient]::new()
        try { $socket.ExclusiveAddressUse=$true; $socket.Client.Bind([Net.IPEndPoint]::new([Net.IPAddress]::Any,$port)) }
        finally { $socket.Dispose() }
    }
    $run = Join-Path $Workspace ('runs\' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + $Kind + '-' + $SaveName)
    New-Item -ItemType Directory -Force -Path $run | Out-Null
    $phase = Join-Path $run 'worldcreate.log'
    if ($Kind -eq 'Reload') {
        Get-FileHash -LiteralPath (Join-Path $data "Game Saves\$SaveName.sav") | Select-Object Hash,Path |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'reload-input.json')
    }
    $log = Join-Path $run 'stdout.log'
    $err = Join-Path $run 'stderr.log'
    # The official host prepares its configured bootstrap file before LoadGame is intercepted.
    # Keep that unused file distinct from the genuinely generated output.
    $configuredSave = if ($creating) { '__unused_worldgen_bootstrap' } else { $SaveName }
    $config = @{ saveName=$configuredSave; port=$JoinPort; password=[Guid]::NewGuid().ToString('N'); autosaveMinutes=0; steam=$false; logFile=$true }
    $config | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $data 'server-config.json')
    $argv = @($cli,'launch','--root',$package,'--data-dir',$data,'--port',"$EnginePort",'--stop-after',"$TimeoutSeconds")
    $taomRun = $Recipe -ne 'Vanilla' -and $Kind -ne 'Control'
    if ($taomRun) {
        $mods = 'TAOM.Dependencies:DependencyOnly,TAOM:Run,TAOM_Map:Run'
        if ($Recipe -eq 'TaomFull') { $mods = 'TAOM.Dependencies:DependencyOnly,LOTRLOME_Armory:Run,TAOM:Run,TAOM_Map:Run' }
        $argv += @('--game',$diagnosticGame,'--mods',$mods,'--mod-distance-cache')
    }
    if ($creating) {
        $creationTimeout = if ($Kind -eq 'Timeout') { 1 } else { $TimeoutSeconds }
        $argv += @('--create-world',$SaveName,'--create-world-timeout',"$creationTimeout",'--world-log',$phase)
    }
    # Load via the isolated config. The official host's /coopsave mode forces UDP 4200 and ignores
    # the configured password/port, so it is unsuitable for an isolated diagnostic run.
    else { $argv += @('--compat') }
    $argv | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'arguments.json')
    $quoted = $argv | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }
    $started = [DateTime]::UtcNow
    $mapFiles = @()
    $mapEnvironment = @{ MODDERLORDS_HEADLESS_MAP = '' }
    $mapTarget = Join-Path $package 'engine\Modules\DedicatedServer.Windows\SceneObj\Main_map'
    $mapBackup = Join-Path $run 'original-map'
    function Restore-DiagnosticMap {
        foreach ($file in $mapFiles) {
            $target = Join-Path $mapTarget $file.Name
            $backup = Join-Path $mapBackup $file.Name
            if (Test-Path -LiteralPath $backup) { Copy-Item -LiteralPath $backup -Destination $target -Force }
            elseif (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target }
        }
    }
    if ($HeadlessScene -and $taomRun) {
        if (-not (Test-Path (Join-Path $HeadlessScene 'modderlords-map.xml'))) { throw 'Prepare the headless scene with Build-HeadlessMap.py first' }
        New-Item -ItemType Directory $mapBackup | Out-Null
        try {
            foreach ($file in Get-ChildItem -LiteralPath $HeadlessScene -File) {
                $target = Join-Path $mapTarget $file.Name
                if (Test-Path -LiteralPath $target) { Copy-Item -LiteralPath $target -Destination $mapBackup }
                # Track only files whose originals have already been secured, including a partial copy.
                $mapFiles += $file
                Copy-Item -LiteralPath $file.FullName -Destination $target
            }
        } catch { Restore-DiagnosticMap; throw }
        $mapEnvironment.MODDERLORDS_HEADLESS_MAP = Join-Path $mapTarget 'modderlords-map.xml'
    }
    try {
        $p = Start-Process -FilePath 'dotnet' -ArgumentList $quoted -WorkingDirectory $source -WindowStyle Hidden -PassThru -RedirectStandardOutput $log -RedirectStandardError $err -Environment $mapEnvironment
    } catch { Restore-DiagnosticMap; throw }
    $null = $p.Handle
    $timeout = $false
    try {
        while (-not $p.WaitForExit(1000)) {
            if (([DateTime]::UtcNow - $started).TotalSeconds -gt $TimeoutSeconds + 45) {
                $timeout = $true
                $p.Kill($true) # Only this CLI and its descendants. Engine also has a kill-on-close job.
                break
            }
        }
        $p.WaitForExit()
        $code = $p.ExitCode
    } finally { if (-not $p.HasExited) { $p.Kill($true); $p.WaitForExit() }; $p.Dispose(); Restore-DiagnosticMap }
    $text = [IO.File]::ReadAllText($log) + [IO.File]::ReadAllText($err)
    $timeout = $timeout -or $text.Contains('auto-stop: timeout reached')
    if ($Recipe -ne 'Vanilla') {
        $engineLogs = Join-Path $package 'engine\bin\Win64_Shipping_Server\Logs'
        if (Test-Path $engineLogs) {
            foreach ($file in Get-ChildItem -LiteralPath $engineLogs -File | Where-Object LastWriteTimeUtc -GE $started) {
                Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $run $file.Name)
            }
        }
    }
    $phases = if (Test-Path -LiteralPath $phase) { [IO.File]::ReadAllText($phase) } else { '' }
    $save = Join-Path $data "Game Saves\$SaveName.sav"
    $ok = -not $timeout
    if ($Kind -eq 'Timeout') {
        $ok = $ok -and $code -eq 12 -and $phases.Contains('worldcreate: fail phase=') -and $phases.Contains('timed out') -and -not (Test-Path -LiteralPath $save)
    } elseif ($Kind -eq 'Create') {
        $ok = $ok -and $code -eq 11 -and $phases.Contains('worldcreate: phase=saved') -and (Test-Path -LiteralPath $save)
        if ($ok) { $ok = (Get-Item -LiteralPath $save).LastWriteTimeUtc -ge $started }
        if ($ok) {
            try {
                $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($save))
                try {
                    $length = $reader.ReadInt32()
                    if ($length -le 2 -or $length -gt 1MB -or $length -ge $reader.BaseStream.Length-4) { throw 'Invalid save header size' }
                    $header = [Text.Encoding]::UTF8.GetString($reader.ReadBytes($length)) | ConvertFrom-Json
                } finally { $reader.Dispose() }
                $header | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $run 'save-header.json')
                $ids = $header.List.Modules -split ';'
                foreach ($required in @('Native','SandBoxCore','Sandbox')) { if ($ids -notcontains $required) { throw "Missing module in save: $required" } }
                if (-not ($ids -contains 'Coop' -or $ids -contains 'CoopNightly')) { throw 'Save is missing Coop' }
                if ($taomRun) {
                    foreach ($required in @('TAOM.Dependencies','TAOM','TAOM_Map')) {
                        if ($ids -notcontains $required) { throw "Missing TAOM module in save: $required" }
                    }
                    if ($Recipe -eq 'TaomFull' -and $ids -notcontains 'LOTRLOME_Armory') { throw 'Save is missing LOTRLOME_Armory' }
                }
                $hash = (Get-FileHash -LiteralPath $save).Hash
                $template = Join-Path $package 'server-data\Game Saves\default_new_game.sav'
                if ($hash -eq (Get-FileHash -LiteralPath $template).Hash) { throw 'Generated save is a copy of the template' }
                Set-Content -LiteralPath (Join-Path $run 'save-sha256.txt') -Value $hash
                # Preserve the generated bytes: a normal server may save again when it shuts down.
                Copy-Item -LiteralPath $save -Destination (Join-Path $run ($SaveName + '.sav'))
            } catch { $ok=$false; Set-Content -LiteralPath (Join-Path $run 'save-validation-error.txt') -Value $_ }
        }
        if ($text.Contains('SERVING')) { $ok=$false }
    } else {
        $ok = $ok -and $code -eq 0 -and $text.Contains('SERVING') -and $text.Contains("HostSaveGame('$SaveName')")
        if ($text.Contains('worldcreate: installed')) { $ok=$false }
    }
    # An exited launcher closes its kill-on-close engine job. Verify both owned ports are released.
    foreach ($port in @($EnginePort,$JoinPort)) {
        $socket = [Net.Sockets.UdpClient]::new()
        try { $socket.ExclusiveAddressUse=$true; $socket.Client.Bind([Net.IPEndPoint]::new([Net.IPAddress]::Any,$port)) }
        catch { $ok=$false }
        finally { $socket.Dispose() }
    }
    $result = [ordered]@{ stage=$Kind; recipe=$Recipe; name=$SaveName; passed=$ok; exitCode=$code; timedOut=$timeout; seconds=[int]([DateTime]::UtcNow-$started).TotalSeconds; log=$log; phaseLog=$phase; save=$save }
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'result.json')
    $result | ConvertTo-Json | Write-Output
    if (-not $ok) { throw "Stage failed; evidence: $run" }
}
if ($Stage -eq 'All') {
    Invoke-Vanilla 'Control' ($Name + '_control')
    Invoke-Vanilla 'Create' $Name
    Invoke-Vanilla 'Reload' $Name
    Invoke-Vanilla 'Create' ($Name + '_repeat')
    Invoke-Vanilla 'Reload' ($Name + '_repeat')
    Invoke-Vanilla 'Control' ($Name + '_regression')
    Invoke-Vanilla 'Timeout' ($Name + '_timeout')
} else { Invoke-Vanilla $Stage $Name }
