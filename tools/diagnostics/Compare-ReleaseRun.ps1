#requires -Version 7.0
<#
.SYNOPSIS
  Reproduce a packaged release with the user's installed inputs without touching the live installation.

.DESCRIPTION
  Capture is read-only. Run/Create/Reload clone the server, the selected client modules, the profile's current
  server data, and the release's compatibility payload into a new session directory. Every engine process is owned by
  the script and is the only process the timeout cleanup can stop. The script never copies the full client
  AssetPackages tree.

.EXAMPLE
  pwsh -File tools/diagnostics/Compare-ReleaseRun.ps1 -Stage Capture
  pwsh -File tools/diagnostics/Compare-ReleaseRun.ps1 -Stage All -Case ExactUser -SaveName release094_exact
  pwsh -File tools/diagnostics/Compare-ReleaseRun.ps1 -Stage All -Case ComputedOrder -SaveName release094_computed
  pwsh -File tools/diagnostics/Compare-ReleaseRun.ps1 -Stage Matrix -SaveName release094_matrix
#>
[CmdletBinding()]
param(
    [ValidateSet('Capture','Create','Reload','All','Matrix')][string]$Stage = 'All',
    [ValidateSet('ExactUser','ComputedOrder','CleanState','Baseline','ExistingSave','Version','Package','Vanilla')][string]$Case = 'ExactUser',
    [string]$ReleaseDir = 'C:\Users\A\Downloads\ModderLords-0.9.4',
    [string]$Workspace = 'D:\Design\Bannerlord Mods\_release-taom-comparison',
    [string]$ProfilePath = "$env:LOCALAPPDATA\ModderLords\profiles\profile2.json",
    [string]$InstalledServer = 'D:\Program Files (x86)\Steam\steamapps\workshop\content\261550\3770450698\DedicatedServer',
    [string]$InstalledGame = 'D:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord',
    [string]$InstalledData = "$env:USERPROFILE\Documents\Mount and Blade II Bannerlord\CoopData\DedicatedServer",
    [string]$BaselineWorkspace = 'D:\Design\Bannerlord Mods\_taom-world-proof',
    [Alias('Name')][string]$SaveName = ('release_taom_' + (Get-Date -Format 'yyyyMMdd_HHmmss')),
    [ValidateSet('Manual','Computed')][string]$OrderPolicy = 'Manual',
    [ValidateSet('Release','Source')][string]$Payload = 'Release',
    [string]$ExistingSaveName = 'TAOM',
    # Zero selects an unused private port in the ranges below. Supplying a value keeps that exact port and fails
    # before cloning if it is already occupied, which makes an explicit test reproducible.
    [int]$EnginePort = 0,
    [int]$JoinPort = 0,
    [int]$TimeoutSeconds = 900,
    [int]$ReloadStopSeconds = 120,
    [bool]$PreserveLiveState = $true,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function Normalize([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'Path cannot be empty.' }
    return [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Path))
}

function Assert-Exists([string]$Path, [string]$Description) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "$Description was not found: $Path" }
}

function Assert-Outside([string]$Candidate, [string]$Protected, [string]$Description) {
    $c = (Normalize $Candidate).TrimEnd('\')
    $p = (Normalize $Protected).TrimEnd('\')
    if ($c.Equals($p, [StringComparison]::OrdinalIgnoreCase) -or $c.StartsWith($p + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "${Description} must be outside ${p}: $c"
    }
}

function Copy-Tree([string]$From, [string]$To) {
    Assert-Exists $From 'Copy source'
    New-Item -ItemType Directory -Force -Path $To | Out-Null
    & robocopy $From $To /E /XJ /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copy failed: $From -> $To (robocopy $LASTEXITCODE)" }
}

function Copy-IfPresent([string]$From, [string]$To) {
    if (Test-Path -LiteralPath $From) { Copy-Tree $From $To; return $true }
    return $false
}

function Get-FileEvidence([string]$Root, [switch]$Recurse) {
    if (-not (Test-Path -LiteralPath $Root)) { return @() }
    $items = if ($Recurse) { Get-ChildItem -LiteralPath $Root -File -Recurse -ErrorAction SilentlyContinue } else { Get-ChildItem -LiteralPath $Root -File -ErrorAction SilentlyContinue }
    $items |
        Sort-Object FullName |
        ForEach-Object {
            try {
                $h = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
                [ordered]@{ path = $_.FullName; length = $_.Length; lastWriteUtc = $_.LastWriteTimeUtc; sha256 = $h.Hash }
            } catch {
                [ordered]@{ path = $_.FullName; error = $_.Exception.Message }
            }
        }
}

function Get-FirstExisting([string[]]$Candidates) {
    foreach ($candidate in $Candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    return $null
}

function Get-PayloadComparison([string]$ReleaseRoot, [string]$BaselineRoot) {
    $artifacts = [ordered]@{
        'hook' = @('bin\ModderLords.Hook.dll')
        'serverCompat' = @('compat\DedicatedServer.ModderLordsCompat\bin\Win64_Shipping_Server\DedicatedServer.ModderLordsCompat.dll')
        'recipes' = @('compat\ModderLords.Compat\recipes.json')
        'compatDb' = @('data\compat-db.json')
    }
    $comparison = [ordered]@{}
    foreach ($name in $artifacts.Keys) {
        $releasePath = Get-FirstExisting @($artifacts[$name] | ForEach-Object { Join-Path $ReleaseRoot $_ })
        $baselineCandidates = switch ($name) {
            'hook' { @(
                (Join-Path $BaselineRoot 'bin\ModderLords.Hook.dll'),
                (Join-Path $BaselineRoot 'source\src\ModderLords.Cli\bin\Diagnostic\net10.0\ModderLords.Hook.dll'),
                (Join-Path $BaselineRoot 'source\src\ModderLords.Hook\bin\Diagnostic\net6.0\ModderLords.Hook.dll')) }
            'serverCompat' { @(
                (Join-Path $BaselineRoot 'compat\DedicatedServer.ModderLordsCompat\bin\Win64_Shipping_Server\DedicatedServer.ModderLordsCompat.dll'),
                (Join-Path $BaselineRoot 'source\src\ModderLords.Cli\bin\Diagnostic\net10.0\compat\DedicatedServer.ModderLordsCompat\bin\Win64_Shipping_Server\DedicatedServer.ModderLordsCompat.dll'),
                (Join-Path $BaselineRoot 'source\src\ModderLords.Compat\_Module\bin\Win64_Shipping_Server\DedicatedServer.ModderLordsCompat.dll')) }
            'recipes' { @(
                (Join-Path $BaselineRoot 'compat\ModderLords.Compat\recipes.json'),
                (Join-Path $BaselineRoot 'source\src\ModderLords.Cli\bin\Diagnostic\net10.0\compat\ModderLords.Compat\recipes.json')) }
            'compatDb' { @(
                (Join-Path $BaselineRoot 'data\compat-db.json'),
                (Join-Path $BaselineRoot 'source\src\ModderLords.Cli\bin\Diagnostic\net10.0\compat-db.json'),
                (Join-Path $BaselineRoot 'source\src\ModderLords.Core\bin\Diagnostic\net10.0\compat-db.json')) }
        }
        $baselinePath = Get-FirstExisting $baselineCandidates
        $releaseHash = if ($releasePath) { (Get-FileHash -LiteralPath $releasePath -Algorithm SHA256).Hash } else { $null }
        $baselineHash = if ($baselinePath) { (Get-FileHash -LiteralPath $baselinePath -Algorithm SHA256).Hash } else { $null }
        $comparison[$name] = [ordered]@{
            releasePath = $releasePath; releaseSha256 = $releaseHash
            baselinePath = $baselinePath; baselineSha256 = $baselineHash
            equal = ($null -ne $releaseHash -and $releaseHash -eq $baselineHash)
        }
    }
    return $comparison
}

function Get-ModuleManifestEvidence([object]$Profile) {
    $result = @()
    if ($Profile) {
        foreach ($mod in @($Profile.Mods)) {
            $source = [string]$mod.SourcePath
            $manifest = if ($source) { Join-Path $source 'SubModule.xml' } else { '' }
            $manifestVersion = $null
            if ($manifest -and (Test-Path -LiteralPath $manifest)) {
                try { $manifestVersion = ([xml](Get-Content -LiteralPath $manifest -Raw)).Module.Version.value } catch { }
            }
            $result += [ordered]@{
                id = [string]$mod.Id; role = [string]$mod.Role; enabled = [bool]$mod.Enabled
                profileVersion = [string]$mod.LastVersion; manifestVersion = [string]$manifestVersion; sourcePath = $source
                manifest = if ($manifest -and (Test-Path -LiteralPath $manifest)) { @(Get-FileEvidence $manifest) } else { @() }
            }
        }
    }
    return $result
}

function Get-ProcessEvidence {
    try {
        Get-CimInstance Win32_Process -Filter "Name = 'ModderLords.exe' OR Name = 'dotnet.exe'" |
            Sort-Object ProcessId |
            Select-Object @{n='processId';e={$_.ProcessId}}, @{n='parentProcessId';e={$_.ParentProcessId}},
                @{n='name';e={$_.Name}}, @{n='executablePath';e={$_.ExecutablePath}}, @{n='commandLine';e={$_.CommandLine}},
                @{n='creationDate';e={$_.CreationDate}}
    } catch {
        # Non-elevated PowerShell often cannot query CIM command lines. Keep a useful read-only inventory and make the
        # limitation explicit instead of returning an empty process list.
        $fallback = @(Get-Process -Name ModderLords,dotnet -ErrorAction SilentlyContinue |
            Sort-Object Id |
            ForEach-Object {
                [ordered]@{ processId = $_.Id; parentProcessId = $null; name = $_.ProcessName; executablePath = $_.Path
                    commandLine = $null; creationDate = $_.StartTime; commandLineUnavailable = $true }
            })
        if ($fallback.Count -gt 0) { $fallback } else { @([ordered]@{ error = $_.Exception.Message }) }
    }
}

function Assert-NoLiveLauncher {
    # Bannerlord's native crash watchdog uses a Global named event. Two server engines from different folders still
    # collide on that event, and the second one can die with 0xC0000409 before the managed hook reaches worldcreate.
    # Capture remains available while the user's launcher is open, but an engine comparison must wait until it is
    # stopped; never stop it here.
    $live = @(Get-Process -Name ModderLords -ErrorAction SilentlyContinue)
    if ($live.Count -gt 0) {
        $ids = $live | ForEach-Object { "pid $($_.Id)" }
        throw "A ModderLords launcher is already running ($($ids -join ', ')). Capture is safe, but an engine comparison would collide with Bannerlord's global watchdog. Close it and rerun the isolated stage; this tool will not stop it."
    }
}

function Write-Json([string]$Path, $Value) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Redact-Profile([object]$Profile) {
    if ($null -eq $Profile) { return $null }
    $copy = $Profile | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    if ($copy.Server -and $copy.Server.PSObject.Properties['Password']) { $copy.Server.Password = '****' }
    return $copy
}

function Redact-ServerConfig([object]$Config) {
    if ($null -eq $Config) { return $null }
    $copy = $Config | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    if ($copy.PSObject.Properties['password']) { $copy.password = '****' }
    return $copy
}

function Test-UdpPortFree([int]$Port) {
    $socket = [Net.Sockets.UdpClient]::new()
    try {
        $socket.ExclusiveAddressUse = $true
        $socket.Client.Bind([Net.IPEndPoint]::new([Net.IPAddress]::Any, $Port))
    } catch { throw "UDP port $Port is already in use; choose -EnginePort/-JoinPort without stopping the existing server." }
    finally { $socket.Dispose() }
}

function Select-FreeUdpPort([int]$Requested, [int]$First) {
    if ($Requested -gt 0) { Test-UdpPortFree $Requested; return $Requested }
    for ($port = $First; $port -lt ($First + 200); $port++) {
        try {
            Test-UdpPortFree $port
            return $port
        } catch { }
    }
    throw "Could not find an unused UDP port in $First-$($First + 199). Supply -EnginePort/-JoinPort explicitly."
}

function Get-UdpPortEvidence([int]$Port) {
    if ($Port -le 0) { return [ordered]@{ port = $Port; free = $null; error = 'not configured' } }
    $socket = [Net.Sockets.UdpClient]::new()
    try {
        $socket.ExclusiveAddressUse = $true
        $socket.Client.Bind([Net.IPEndPoint]::new([Net.IPAddress]::Any, $Port))
        return [ordered]@{ port = $Port; free = $true }
    } catch {
        return [ordered]@{ port = $Port; free = $false; error = $_.Exception.Message }
    } finally { $socket.Dispose() }
}

function Get-LiveLogSummary([string]$LogDirectory) {
    $launch = Get-ChildItem -LiteralPath $LogDirectory -Filter 'launch-*.log' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    $world = Get-ChildItem -LiteralPath $LogDirectory -Filter 'worldcreate-*.log' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    $summaries = @()
    foreach ($file in @($launch, $world)) {
        if (-not $file) { continue }
        $lines = @(Get-Content -LiteralPath $file.FullName -ErrorAction SilentlyContinue)
        $summaries += [ordered]@{
            path = $file.FullName; lastWriteUtc = $file.LastWriteTimeUtc; lineCount = $lines.Count
            expectedMissingAnimationWarnings = @($lines | Where-Object { $_ -match 'Could not find animation' }).Count
            otherWarnings = @($lines | Where-Object { $_ -match '(?i)warning' -and $_ -notmatch 'Could not find animation' }).Count
            messageboxPrompts = @($lines | Where-Object { $_ -match 'Messagebox \[Always Ignore\?\]' }).Count
            worldCreateLines = @($lines | Where-Object { $_ -match 'worldcreate:' }).Count
            campaignCreated = @($lines | Where-Object { $_ -match 'campaign-created' }).Count
            mapReady = @($lines | Where-Object { $_ -match 'map-ready' }).Count
            saved = @($lines | Where-Object { $_ -match 'worldcreate: phase=saved' }).Count
            serving = @($lines | Where-Object { $_ -match 'SERVING' }).Count
            dedicatedMapSceneLoads = @($lines | Where-Object { $_ -match 'CreateMapScene -> DedicatedServerMapScene|DedicatedServerMapScene\.Load' }).Count
            firstLines = @($lines | Select-Object -First 3)
            lastLines = @($lines | Select-Object -Last 8)
        }
    }
    return $summaries
}

function Quote-Argument([string]$Value) {
    if ($Value -match '[\s"]') { return '"' + $Value.Replace('"', '\"') + '"' }
    return $Value
}

function Read-SaveHeader([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try {
        $bytes = [IO.File]::ReadAllBytes($Path)
        if ($bytes.Length -lt 4) { return [ordered]@{ error = 'file too short' } }
        $length = [BitConverter]::ToInt32($bytes, 0)
        if ($length -le 2 -or $length -gt (4 * 1024 * 1024) -or $length -gt ($bytes.Length - 4)) {
            return [ordered]@{ error = "implausible header length $length" }
        }
        $json = [Text.Encoding]::UTF8.GetString($bytes, 4, $length) | ConvertFrom-Json
        $list = if ($null -ne $json.List) { $json.List } else { $json }
        $modules = @([string]$list.Modules -split ';' | Where-Object { $_ })
        $versions = [ordered]@{}
        foreach ($property in $list.PSObject.Properties) {
            if ($property.Name.StartsWith('Module_', [StringComparison]::Ordinal)) {
                $versions[$property.Name.Substring(7)] = [string]$property.Value
            }
        }
        $file = Get-Item -LiteralPath $Path
        return [ordered]@{
            path = $Path; length = $file.Length; lastWriteUtc = $file.LastWriteTimeUtc
            applicationVersion = [string]$list.ApplicationVersion; characterName = [string]$list.CharacterName
            mainHeroLevel = [string]$list.MainHeroLevel; dayLong = [double]$list.DayLong
            modules = $modules; moduleVersions = $versions
        }
    } catch {
        return [ordered]@{ error = $_.Exception.Message }
    }
}

function Invoke-EngineRun([string]$Kind, [string]$SaveName, [hashtable]$Context) {
    $creating = $Kind -eq 'Create'
    $savePath = Join-Path $Context.data "Game Saves\$SaveName.sav"
    if ($creating -and (Test-Path -LiteralPath $savePath)) { throw "Save already exists in isolated data: $SaveName" }
    if (-not $creating -and -not (Test-Path -LiteralPath $savePath)) { throw "Cannot reload absent save: $saveName" }
    Test-UdpPortFree $Context.enginePort
    Test-UdpPortFree $Context.joinPort

    $run = Join-Path $Context.sessionRoot ("runs\{0:yyyyMMdd-HHmmss-fff}-{1}-{2}" -f (Get-Date), $Kind, $SaveName)
    New-Item -ItemType Directory -Force -Path $run | Out-Null
    $stdout = Join-Path $run 'stdout.log'
    $stderr = Join-Path $run 'stderr.log'
    $phase = Join-Path $run 'worldcreate.log'
    $argv = @($Context.cli, 'launch', '--root', $Context.server, '--data-dir', $Context.data,
        '--coop-data-dir', $Context.coopData, '--game', $Context.game, '--source', $Context.mods,
        '--port', "$($Context.enginePort)", '--join-port', "$($Context.joinPort)",
        '--stop-after', "$($creating ? $Context.timeout : $Context.reloadStop)")
    if (-not [string]::IsNullOrWhiteSpace($Context.modSpec)) { $argv += @('--mods', $Context.modSpec) }
    if ($Context.bundleRoot) { $argv += @('--bundle-root', $Context.bundleRoot) }
    if ($Context.manualOrder) { $argv += '--manual-order' }
    if ($Context.distanceCache) { $argv += '--mod-distance-cache' }
    if ($creating) {
        $argv += @('--create-world', $SaveName, '--create-world-timeout', "$($Context.timeout)", '--world-log', $phase)
    } else {
        $argv += @('--save', $SaveName, '--compat', '--stall-seconds', '300')
    }
    Write-Json (Join-Path $run 'arguments.json') $argv
    $started = [DateTimeOffset]::UtcNow
    $argText = ($argv | ForEach-Object { Quote-Argument $_ }) -join ' '
    $process = $null
    $timedOut = $false
    $exitCode = $null
    $budgetSeconds = if ($creating) { $Context.timeout } else { $Context.reloadStop }
    try {
        $process = Start-Process -FilePath $Context.dotnet -ArgumentList $argText -WorkingDirectory $repo -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        $processId = $process.Id
        Write-Json (Join-Path $run 'process.json') ([ordered]@{ processId = $processId; startedUtc = $started; command = $argText })
        while (-not $process.HasExited) {
            if (([DateTimeOffset]::UtcNow - $started).TotalSeconds -gt ($budgetSeconds + 45)) {
                $timedOut = $true
                try { $process.Kill($true) } catch { }
                break
            }
            Start-Sleep -Seconds 1
        }
        $process.WaitForExit()
        $exitCode = $process.ExitCode
    } finally {
        if ($process -and -not $process.HasExited) {
            try { $process.Kill($true); $process.WaitForExit() } catch { }
        }
        if ($process) { $process.Dispose() }
    }

    $text = ((Test-Path $stdout) ? [IO.File]::ReadAllText($stdout) : '') + "`n" + ((Test-Path $stderr) ? [IO.File]::ReadAllText($stderr) : '')
    # Match the manifest to this run by its exact world-log path and save name. Timestamp is only a secondary guard;
    # a previous process may have flushed a stale manifest while this process was starting.
    $diag = Get-ChildItem -LiteralPath (Join-Path $Context.data 'logs') -Filter 'creation-*.json' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTimeUtc -ge $started.UtcDateTime.AddSeconds(-2) } |
        ForEach-Object {
            try {
                $candidate = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
                $candidateLog = [string]$candidate.plan.environment.MODDERLORDS_CREATE_WORLD_LOG
                if ([string]$candidate.saveName -eq $SaveName -and $candidateLog -eq $phase) { $_ }
            } catch { }
        } |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($diag) { Copy-Item -LiteralPath $diag.FullName -Destination (Join-Path $run 'creation-manifest.json') -Force }
    if (Test-Path -LiteralPath $phase) { Copy-Item -LiteralPath $phase -Destination (Join-Path $run 'worldcreate-sidecar.log') -Force }
    $hookLogs = Get-ChildItem -LiteralPath (Join-Path $Context.data 'logs') -Filter 'hook-*.log' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTimeUtc -ge $started.UtcDateTime.AddSeconds(-2) }
    if ($hookLogs) {
        $hookDest = Join-Path $run 'hook-logs'; New-Item -ItemType Directory -Force -Path $hookDest | Out-Null
        $hookLogs | Copy-Item -Destination $hookDest -Force
    }
    $engineLogs = Join-Path $Context.server 'engine\bin\Win64_Shipping_Server\Logs'
    if (Test-Path $engineLogs) {
        $dest = Join-Path $run 'engine-logs'; New-Item -ItemType Directory -Force -Path $dest | Out-Null
        Get-ChildItem -LiteralPath $engineLogs -File -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTimeUtc -ge $started.UtcDateTime.AddSeconds(-2) } |
            Copy-Item -Destination $dest -Force
    }
    $manifest = if (Test-Path (Join-Path $run 'creation-manifest.json')) { Get-Content (Join-Path $run 'creation-manifest.json') -Raw | ConvertFrom-Json } else { $null }
    $phases = @()
    if ($manifest -and $manifest.phases -is [Array]) { $phases = @($manifest.phases | ForEach-Object { [string]$_ }) }
    $actualMapSceneType = if ($manifest -and $manifest.worldCreation) { [string]$manifest.worldCreation.actualMapSceneType } else { $null }
    $saveExists = Test-Path -LiteralPath $savePath
    $saveHeader = if ($saveExists) { Read-SaveHeader $savePath } else { $null }
    if ($saveExists) {
        $hash = (Get-FileHash -LiteralPath $savePath -Algorithm SHA256).Hash
        Copy-Item -LiteralPath $savePath -Destination (Join-Path $run "$SaveName.sav") -Force
    } else { $hash = $null }
    $passed = if ($creating) {
        -not $timedOut -and $exitCode -eq 11 -and $saveExists -and ($phases -contains 'saved') -and -not $text.Contains('SERVING')
    } else {
        -not $timedOut -and $exitCode -eq 0 -and $text.Contains('SERVING') -and $text.Contains("HostSaveGame('$SaveName')")
    }
    $finished = [DateTimeOffset]::UtcNow
    $result = [ordered]@{
        stage = $Kind; case = $Context.case; recipe = $Context.recipe; name = $SaveName; passed = $passed
        processId = $processId; exitCode = $exitCode; timedOut = $timedOut; seconds = [int]($finished - $started).TotalSeconds
        startedUtc = $started; finishedUtc = $finished; enginePort = $Context.enginePort; joinPort = $Context.joinPort
        orderPolicy = if ($Context.manualOrder) { 'Manual' } else { 'Computed' }; modules = @($Context.modSpec -split ',' | Where-Object { $_ })
        payload = $Context.payload; sessionRoot = $Context.sessionRoot; stdout = $stdout; stderr = $stderr; phaseLog = $phase
        creationManifest = if ($diag) { $diag.FullName } else { $null }; phases = $phases; actualMapSceneType = $actualMapSceneType
        warnings = if ($manifest) { $manifest.warnings } else { $null }
        save = $savePath; saveExists = $saveExists; saveSha256 = $hash; saveHeader = $saveHeader
        foundServingDuringCreate = $text.Contains('SERVING')
    }
    Write-Json (Join-Path $run 'result.json') $result
    Write-Host ("[{0}/{1}] passed={2} exit={3} seconds={4} evidence={5}" -f $Kind, $Context.case, $passed, $exitCode, $result.seconds, $run)
    if (-not $passed) { Write-Warning "$Kind failed; evidence: $run" }
    return $result
}

function Capture-LiveState([string]$OutputRoot) {
    New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
    $profile = if (Test-Path -LiteralPath $ProfilePath) { Get-Content $ProfilePath -Raw | ConvertFrom-Json } else { $null }
    $profileCopy = if ($profile) { Redact-Profile $profile } else { [ordered]@{ error = "profile not found: $ProfilePath" } }
    $serverConfigPath = Join-Path $InstalledData 'server-config.json'
    $serverConfig = if (Test-Path -LiteralPath $serverConfigPath) { Redact-ServerConfig (Get-Content $serverConfigPath -Raw | ConvertFrom-Json) } else { $null }
    $releaseFiles = @(Get-FileEvidence $ReleaseDir -Recurse)
    $serverFiles = @(Get-FileEvidence $InstalledServer)
    foreach ($stock in @('Native','SandBoxCore','SandBox','Coop','DedicatedServer.Windows')) {
        $serverFiles += @(Get-FileEvidence (Join-Path $InstalledServer "engine\Modules\$stock") -Recurse)
    }
    $dataFiles = @(Get-FileEvidence $InstalledData -Recurse)
    $overlayRoot = if ($profile) { Join-Path $env:LOCALAPPDATA "ModderLords\overlay\$($profile.Name)" } else { $null }
    $overlayFiles = if ($overlayRoot) { @(Get-FileEvidence $overlayRoot -Recurse) } else { @() }
    $launcherLogFiles = @(Get-FileEvidence (Join-Path $env:LOCALAPPDATA 'ModderLords\logs') -Recurse)
    $moduleManifests = @(Get-ModuleManifestEvidence $profile)
    $officialManifests = @()
    foreach ($official in @('Native','SandBoxCore','Sandbox','SandBox','StoryMode','CustomBattle','BirthAndDeath','Coop','DedicatedServer.Windows')) {
        $candidates = @(
            (Join-Path $InstalledGame "Modules\$official\SubModule.xml"),
            (Join-Path $InstalledServer "engine\Modules\$official\SubModule.xml"))
        $manifest = Get-FirstExisting $candidates
        if ($manifest) {
            $version = $null
            try { $version = ([xml](Get-Content -LiteralPath $manifest -Raw)).Module.Version.value } catch { }
            $officialManifests += [ordered]@{ id = $official; version = [string]$version; evidence = @(Get-FileEvidence $manifest) }
        }
    }
    $portValues = @()
    if ($profile) { $portValues += @([int]$profile.Server.EnginePort, [int]$profile.Server.JoinPort) }
    if ($serverConfig) { $portValues += @([int]$serverConfig.port) }
    $ports = @($portValues | Where-Object { $_ -gt 0 } | Sort-Object -Unique | ForEach-Object { Get-UdpPortEvidence $_ })
    $liveLogSummary = @(Get-LiveLogSummary (Join-Path $env:LOCALAPPDATA 'ModderLords\logs'))
    $capture = [ordered]@{
        schema = 1; capturedUtc = [DateTimeOffset]::UtcNow; releaseDir = $ReleaseDir; profilePath = $ProfilePath
        installedServer = $InstalledServer; installedGame = $InstalledGame; installedData = $InstalledData
        profile = $profileCopy; serverConfig = $serverConfig; ports = $ports
        releaseFiles = $releaseFiles; serverFiles = $serverFiles; dataFiles = $dataFiles; overlayRoot = $overlayRoot
        moduleManifests = $moduleManifests; officialManifests = $officialManifests
        overlayFiles = $overlayFiles; launcherLogFiles = $launcherLogFiles; liveLogSummary = $liveLogSummary; processes = @(Get-ProcessEvidence)
        note = 'Read-only capture. No process was stopped and no source file was changed. Data, overlay and launcher logs are hashed in place; no client AssetPackages are copied.'
    }
    Write-Json (Join-Path $OutputRoot 'live-manifest.json') $capture
    # Keep the exported copy consistent with live-manifest.json; diagnostic artifacts must not duplicate credentials.
    if ($profile) { Write-Json (Join-Path $OutputRoot 'profile.json') $profileCopy }
    Write-Output (Join-Path $OutputRoot 'live-manifest.json')
}

function New-Context([string]$SelectedCase, [string]$SelectedName) {
    if (-not $PreserveLiveState) { throw '-PreserveLiveState must remain enabled; this tool never runs against the live installation.' }
    $release = Normalize $ReleaseDir; $workspace = Normalize $Workspace
    Assert-Exists $release 'Release directory'; Assert-Exists $ProfilePath 'Profile'; Assert-Exists $InstalledServer 'Installed dedicated server'; Assert-Exists $InstalledGame 'Installed game'
    Assert-Outside $workspace $release 'Comparison workspace'; Assert-Outside $workspace $InstalledServer 'Comparison workspace'; Assert-Outside $workspace $InstalledGame 'Comparison workspace'; Assert-Outside $workspace $InstalledData 'Comparison workspace'
    Assert-NoLiveLauncher
    # Pick ports only after the live-process guard. This keeps the currently running server untouched while still
    # making the normal no-argument path work on machines where the suggested diagnostic ports are busy.
    $enginePort = Select-FreeUdpPort $EnginePort 7368
    $joinPort = Select-FreeUdpPort $JoinPort 4368
    if ($enginePort -eq $joinPort) { $joinPort = Select-FreeUdpPort 0 ($joinPort + 1) }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
    $session = Join-Path $workspace "sessions\$stamp-$SelectedCase-$SelectedName"
    $server = Join-Path $session 'DedicatedServer'; $game = Join-Path $session 'Game'; $mods = Join-Path $session 'Mods'; $data = Join-Path $session 'Data'; $coopData = Join-Path $session 'CoopData'; $inputDir = Join-Path $session 'input'
    New-Item -ItemType Directory -Force -Path $session,$mods,$data,$coopData,$inputDir | Out-Null
    $profile = Get-Content $ProfilePath -Raw | ConvertFrom-Json
    $sourceServer = $InstalledServer; $sourceGame = $InstalledGame; $baselineAvailable = $false
    if ($SelectedCase -in @('Baseline','Version') -and (Test-Path (Join-Path $BaselineWorkspace 'DedicatedServer')) -and (Test-Path (Join-Path $BaselineWorkspace 'Game'))) {
        $sourceServer = Join-Path $BaselineWorkspace 'DedicatedServer'; $sourceGame = Join-Path $BaselineWorkspace 'Game'; $baselineAvailable = $true
    }
    Copy-Tree $sourceServer $server
    New-Item -ItemType Directory -Force -Path (Join-Path $game 'Modules'), (Join-Path $game 'bin') | Out-Null
    Copy-IfPresent (Join-Path $sourceGame 'bin\Win64_Shipping_Client') (Join-Path $game 'bin\Win64_Shipping_Client') | Out-Null
    foreach ($official in @('Native','SandBoxCore','Sandbox','SandBox','StoryMode','CustomBattle','BirthAndDeath')) {
        $from = Join-Path $sourceGame "Modules\$official"
        if (Test-Path $from) { Copy-Tree $from (Join-Path $game "Modules\$official") }
    }
    $enabled = if ($SelectedCase -eq 'Vanilla') { @() } else { @($profile.Mods | Where-Object { $_.Enabled -eq $true }) }
    $modSpec = [Collections.Generic.List[string]]::new()
    foreach ($mod in $enabled) {
        $source = [string]$mod.SourcePath
        if (-not (Test-Path -LiteralPath $source)) { throw "Selected mod source is missing: $($mod.Id) -> $source" }
        $destination = Join-Path $mods ([IO.Path]::GetFileName(([IO.Path]::GetFullPath($source))))
        Copy-Tree $source $destination
        $modSpec.Add("$($mod.Id):$($mod.Role)")
    }
    # ExactUser preserves the existing profile data and generated overlay as evidence. Other cases start clean so a
    # stale marker, save, or generated projection cannot silently affect the comparison.
    $copyExistingData = $SelectedCase -in @('ExactUser','ExistingSave')
    if ($copyExistingData -and (Test-Path $InstalledData) -and (Test-Path (Join-Path $InstalledData 'Game Saves'))) {
        Copy-Tree (Join-Path $InstalledData 'Game Saves') (Join-Path $data 'Game Saves')
        if (Test-Path (Join-Path $InstalledData 'server-config.json')) {
            $originalConfig = Get-Content (Join-Path $InstalledData 'server-config.json') -Raw | ConvertFrom-Json
            Write-Json (Join-Path $data 'server-config.original.json') (Redact-ServerConfig $originalConfig)
        }
        $overlay = Join-Path $env:LOCALAPPDATA "ModderLords\overlay\$($profile.Name)"
        if (Test-Path $overlay) { Copy-Tree $overlay (Join-Path $data "overlay\$($profile.Name)") }
    }
    $config = [ordered]@{ saveName = ''; port = $joinPort; password = ([Guid]::NewGuid().ToString('N')); autosaveMinutes = 0; steam = $false; logFile = $true }
    Write-Json (Join-Path $data 'server-config.json') $config
    Write-Json (Join-Path $inputDir 'profile.json') (Redact-Profile $profile)
    if (Test-Path (Join-Path $InstalledData 'server-config.json')) {
        $originalConfig = Get-Content (Join-Path $InstalledData 'server-config.json') -Raw | ConvertFrom-Json
        Write-Json (Join-Path $inputDir 'server-config.json') (Redact-ServerConfig $originalConfig)
    }
    Write-Json (Join-Path $inputDir 'release-files.json') (Get-FileEvidence $release -Recurse)
    Write-Json (Join-Path $inputDir 'server-files.json') (Get-FileEvidence $sourceServer)
    Write-Json (Join-Path $inputDir 'game-files.json') (Get-FileEvidence $sourceGame)
    # ExactUser and Version start from the profile's real ordering. The comparison cases that are intended to be
    # known-good deliberately use the computed dependency order; -OrderPolicy still controls the generic/Vanilla
    # path and can force a computed ExactUser/Version run when requested.
    $manual = switch ($SelectedCase) {
        'ExactUser' { if ($OrderPolicy -eq 'Computed') { $false } else { [bool]$profile.ManualLoadOrder } }
        'ComputedOrder' { $false }
        'CleanState' { $false }
        'Baseline' { $false }
        'ExistingSave' { $false }
        'Version' { if ($OrderPolicy -eq 'Computed') { $false } else { [bool]$profile.ManualLoadOrder } }
        default { $OrderPolicy -eq 'Manual' }
    }
    $distanceCache = [bool]$profile.UseModDistanceCache
    $bundleRoot = $Payload -eq 'Release' ? $release : $null
    $recipe = if ($SelectedCase -eq 'Vanilla') { 'Vanilla' } else { 'TaomFull' }
    $context = @{
        case = $SelectedCase; sessionRoot = $session; releaseDir = $release; bundleRoot = $bundleRoot; payload = $Payload; server = $server; game = $game; mods = $mods; data = $data; coopData = $coopData
        modSpec = ($modSpec -join ','); recipe = $recipe; manualOrder = $manual; distanceCache = $distanceCache; enginePort = $enginePort; joinPort = $joinPort
        timeout = $TimeoutSeconds; reloadStop = $ReloadStopSeconds; dotnet = (Get-Command dotnet -ErrorAction Stop).Source
        cli = Join-Path $repo 'src\ModderLords.Cli\bin\Diagnostic\net10.0\ModderLords.Cli.dll'; baselineAvailable = $baselineAvailable
    }
    Write-Json (Join-Path $session 'session.json') ([ordered]@{ schema = 1; createdUtc = [DateTimeOffset]::UtcNow; case = $SelectedCase; name = $SelectedName; recipe = $recipe; manualOrder = $manual; orderPolicy = if ($manual) { 'Manual' } else { 'Computed' }; enginePort = $enginePort; joinPort = $joinPort; payload = $Payload; sourceServer = $sourceServer; sourceGame = $sourceGame; baselineAvailable = $baselineAvailable; modules = @($modSpec); releaseDir = $release; data = $data; noFullClientAssetPackages = $true })
    return $context
}

function Ensure-Cli([hashtable]$Context) {
    if ($NoBuild) { Assert-Exists $Context.cli 'Diagnostic CLI' ; return }
    $buildLog = Join-Path $Context.sessionRoot 'build.log'
    & dotnet build (Join-Path $repo 'src\ModderLords.Cli\ModderLords.Cli.csproj') -c Diagnostic --nologo -v minimal *> $buildLog
    if ($LASTEXITCODE -ne 0) { throw "Diagnostic build failed; see $buildLog" }
}

function Write-Comparison([string]$Root, $CurrentResults, [string]$SelectedCase) {
    $prior = @()
    $priorRuns = Join-Path $BaselineWorkspace 'runs'
    if (Test-Path $priorRuns) {
        $prior = @(Get-ChildItem -LiteralPath $priorRuns -Filter result.json -File -Recurse -ErrorAction SilentlyContinue |
            ForEach-Object { [ordered]@{ path = $_.FullName; result = try { Get-Content $_.FullName -Raw | ConvertFrom-Json } catch { $null } } })
    }
    Write-Json (Join-Path $Root 'comparison.json') ([ordered]@{ schema = 1; generatedUtc = [DateTimeOffset]::UtcNow; case = $SelectedCase; current = @($CurrentResults); previousEvidence = $prior; releaseDir = $ReleaseDir; note = 'Current runs are isolated clones; previous evidence is referenced read-only.' })
}

$ReleaseDir = Normalize $ReleaseDir; $Workspace = Normalize $Workspace; $ProfilePath = Normalize $ProfilePath; $InstalledServer = Normalize $InstalledServer; $InstalledGame = Normalize $InstalledGame; $InstalledData = Normalize $InstalledData; $BaselineWorkspace = Normalize $BaselineWorkspace
$captureRoot = Join-Path $Workspace 'capture'
if ($Stage -in @('Capture','All','Matrix')) { Capture-LiveState $captureRoot | Out-Host }
if ($Stage -eq 'Capture') { exit 0 }

$cases = if ($Stage -eq 'Matrix') { @('ExactUser','ComputedOrder','CleanState','Baseline','ExistingSave','Version','Package') } else { @($Case) }
$allResults = @()
foreach ($selectedCase in $cases) {
    $selectedName = if ($cases.Count -gt 1) { "$SaveName`_$($selectedCase.ToLowerInvariant())" } else { $SaveName }
    if ($selectedCase -eq 'Package') {
        $packageRoot = Join-Path $Workspace ("package-$((Get-Date).ToString('yyyyMMdd-HHmmss-fff'))")
        New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
        $packageProfile = Redact-Profile (Get-Content $ProfilePath -Raw | ConvertFrom-Json)
        $payloadComparison = Get-PayloadComparison $ReleaseDir $BaselineWorkspace
        Write-Json (Join-Path $packageRoot 'package-manifest.json') ([ordered]@{ schema = 1; releaseDir = $ReleaseDir; files = Get-FileEvidence $ReleaseDir -Recurse; compatibilityComparison = $payloadComparison; profile = $packageProfile; processes = @(Get-ProcessEvidence) })
        $allResults += [ordered]@{ stage = 'Package'; case = $selectedCase; passed = $true; manifest = Join-Path $packageRoot 'package-manifest.json'; compatibilityComparison = $payloadComparison }
        continue
    }
    if ($selectedCase -eq 'Version' -and
        (-not (Test-Path -LiteralPath (Join-Path $BaselineWorkspace 'DedicatedServer')) -or
         -not (Test-Path -LiteralPath (Join-Path $BaselineWorkspace 'Game')))) {
        $unsupported = [ordered]@{
            stage = 'Version'; case = $selectedCase; recipe = 'TaomFull'; name = $selectedName; passed = $true; unsupported = $true
            reason = 'No pinned server/game pair was found under -BaselineWorkspace; the installed pair was not substituted.'
            baselineWorkspace = $BaselineWorkspace; timestampUtc = [DateTimeOffset]::UtcNow
        }
        $allResults += $unsupported
        Write-Json (Join-Path $Workspace "version-unsupported-$selectedName.json") $unsupported
        Write-Host "[Version/$selectedCase] unsupported: $($unsupported.reason)"
        continue
    }
    $context = New-Context $selectedCase $selectedName
    Ensure-Cli $context
    if ($Stage -in @('Create','All','Matrix')) {
        if ($selectedCase -eq 'ExistingSave') {
            $allResults += Invoke-EngineRun 'Reload' $ExistingSaveName $context
        } else {
            $create = Invoke-EngineRun 'Create' $selectedName $context
            $allResults += $create
            if ($create.passed -and $Stage -in @('All','Matrix')) { $allResults += Invoke-EngineRun 'Reload' $selectedName $context }
        }
    } elseif ($Stage -eq 'Reload') {
        $allResults += Invoke-EngineRun 'Reload' ($selectedCase -eq 'ExistingSave' ? $ExistingSaveName : $SaveName) $context
    }
    Write-Comparison $context.sessionRoot $allResults $selectedCase
}
if ($cases.Count -gt 1) { Write-Comparison $Workspace $allResults 'Matrix' }
$allResults | ConvertTo-Json -Depth 10 | Write-Output
if (@($allResults | Where-Object { $_.passed -ne $true }).Count -gt 0) { exit 1 }
