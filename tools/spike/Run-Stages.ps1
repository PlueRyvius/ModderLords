<#
.SYNOPSIS
    Runs the world-creation spike stages and verifies each one automatically.

.DESCRIPTION
    Each stage launches a real dedicated server, which takes minutes and produces logs far too large to read
    by hand (the 2h15m TAOM run was 175 MB). So no stage is judged by eye: each declares assertions -- a
    regex plus whether it must be Present or Absent -- and the script reports PASS/FAIL with the matching
    line, appending to results.md.

    Stages marked Hard stop the whole run when they fail, so the spike's go/no-go is enforced here rather
    than by whoever is watching.

    Two invariants are checked after every stage, because both have already been broken once this week:
      * SandBox's settlement distance cache is back to its original 2,594,125 bytes unless the stage
        deliberately passed -mod-distance-cache.
      * No server process is left running (a held file handle on that cache broke the known-good stack).

.EXAMPLE
    .\tools\spike\Run-Stages.ps1 -Stage all
    .\tools\spike\Run-Stages.ps1 -Stage 0a
#>
[CmdletBinding()]
param(
    [ValidateSet('all', '0a', '0b', '1', '1n', '2', '3', 'reg')]
    [string] $Stage = 'all',

    # Skip the Release build (it is required before any server run, so only skip when you just built).
    [switch] $NoBuild,

    [string] $Port = '7299'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$cli = Join-Path $repo 'src\ModderLords.Cli\bin\Release\net10.0\ModderLords.Cli.dll'
$logDir = Join-Path $PSScriptRoot 'logs'
$results = Join-Path $PSScriptRoot 'results.md'

# The vanilla file the engine opens, and the size it must be when nothing is overriding it.
$cacheDir = 'D:\Program Files (x86)\Steam\steamapps\workshop\content\261550\3770450698\DedicatedServer\engine\Modules\SandBox\ModuleData\DistanceCaches'
$cacheFile = Join-Path $cacheDir 'settlements_distance_cache_Default.bin'
$vanillaCacheBytes = 2594125

# The module mirrors its phase lines here. The engine writes to the same stdout without newline discipline
# and splices ours in half (a real run produced "worldcreate: phase=sta"), so assertions are checked against
# the stage log AND this file.
$sidecar = Join-Path $env:LOCALAPPDATA 'ModderLords\logs\worldcreate-latest.log' 

$taom = 'TAOM.Dependencies:DependencyOnly,TAOM:Run,TAOM_Map:Run'
$knownGood = 'Bannerlord.Harmony:DependencyOnly,Bannerlord.ButterLib:DependencyOnly,Bannerlord.UIExtenderEx:DependencyOnly,Bannerlord.MBOptionScreen:DependencyOnly,CoopModPatch:Run,ImprovedGarrisons:Run,ModularSmithing2:Run'

# --- stage definitions -------------------------------------------------------------------------------
# Assert: @{ Pattern; Expect = 'Present'|'Absent'; Why; Hard = $true|$false }
$stages = [ordered]@{
    '0a' = @{
        Why  = 'Is TAOM_Map required for the crash? If this reaches SERVING the whole premise is wrong.'
        Args = @('launch', '--mods', 'TAOM.Dependencies:DependencyOnly,TAOM:Run', '--compat', '--port', $Port, '--stop-after', '300')
        TimeoutSec = 600
        Assert = @(
            @{ Pattern = 'SERVING'; Expect = 'Absent'; Hard = $true;  Why = 'TAOM alone reaches SERVING -> the map mod is the entire story; stop and re-plan' }
        )
    }
    '0b' = @{
        Why  = 'With no save named, what does the host load? Decides whether the module must win a race.'
        Args = @('launch', '--mods', $taom, '--compat', '--port', $Port, '--stop-after', '150')
        TimeoutSec = 400
        Assert = @(
            @{ Pattern = 'coopsave'; Expect = 'Absent'; Hard = $false; Why = 'no /coopsave should be on the command line when no save is named' }
        )
        # Informational: which save the host picked, if any.
        Report = @('"save":"[^"]*"', 'settlement distance cache: .*')
    }
    '1' = @{
        Why  = 'Unknown 1: does StartNewGame begin a campaign headlessly?'
        Args = @('launch', '--mods', $taom, '--compat', '--mod-distance-cache', '--create-world', 'spike1', '--port', $Port, '--stop-after', '420')
        TimeoutSec = 700
        KeepsCache = $true
        Assert = @(
            @{ Pattern = 'worldcreate: phase=armed';            Expect = 'Present'; Hard = $true;  Why = 'the module never armed -- env var not reaching it' }
            @{ Pattern = 'worldcreate: phase=campaign-created'; Expect = 'Present'; Hard = $false; Why = 'CP1: campaign did not start (compare with stage 1n before concluding)' }
        )
    }
    '1n' = @{
        Why  = 'Same as stage 1 with our own guards OFF -- isolates whether Guards.PushScreen stalls it.'
        Args = @('launch', '--mods', $taom, '--mod-distance-cache', '--create-world', 'spike1n', '--port', $Port, '--stop-after', '420')
        TimeoutSec = 700
        KeepsCache = $true
        Assert = @(
            @{ Pattern = 'worldcreate: phase=campaign-created'; Expect = 'Present'; Hard = $false; Why = 'CP1 without guards; if this passes and stage 1 failed, our screen guards are the cause' }
        )
    }
    '2' = @{
        Why  = 'Unknown 2: does loading complete to MapState?'
        Args = @('launch', '--mods', $taom, '--compat', '--mod-distance-cache', '--create-world', 'spike2', '--port', $Port, '--stop-after', '600')
        TimeoutSec = 900
        KeepsCache = $true
        Assert = @(
            @{ Pattern = 'worldcreate: phase=map-ready'; Expect = 'Present'; Hard = $true;  Why = 'CP2: loading never reached MapState' }
            @{ Pattern = 'NavigationCacheElement`1.get_StringId'; Expect = 'Absent'; Hard = $true; Why = 'CP2: the same NavigationCache NRE as a loaded save -- the hypothesis is dead' }
        )
    }
    '3' = @{
        Why  = 'Unknown 3: does the save land, with the server''s modules in its header?'
        Args = @('launch', '--mods', $taom, '--compat', '--mod-distance-cache', '--create-world', 'spike3', '--port', $Port, '--stop-after', '900')
        TimeoutSec = 1200
        KeepsCache = $true
        Assert = @(
            @{ Pattern = 'worldcreate: ok name=spike3'; Expect = 'Present'; Hard = $true; Why = 'CP3: no save was produced' }
        )
        VerifySave = 'spike3'
    }
    'reg' = @{
        Why  = 'Regression, non-negotiable: the known-good stack must still reach SERVING.'
        Args = @('launch', '--mods', $knownGood, '--compat', '--port', $Port, '--stop-after', '200')
        TimeoutSec = 500
        Assert = @(
            @{ Pattern = 'SERVING'; Expect = 'Present'; Hard = $true; Why = 'the known-good stack no longer serves -- revert before doing anything else' }
        )
        ExpectExit = 0
    }
}

# --- helpers -----------------------------------------------------------------------------------------

function Invoke-Stage {
    param([string] $Name, [hashtable] $Def)

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $log = Join-Path $logDir "$Name-$stamp.log"
    $sidecar = "$log.worldcreate.log"
    # The invariant that matters is "this stage did not change the cache behind the user's back", not that it
    # holds any particular value -- the user may legitimately have the map mod's cache installed already.
    $cacheBefore = if (Test-Path $cacheFile) { (Get-Item $cacheFile).Length } else { -1 }

    Write-Host ""
    Write-Host "=== stage $Name ===" -ForegroundColor Cyan
    Write-Host $Def.Why -ForegroundColor DarkGray

    # Start-Process joins ArgumentList with spaces and does NOT quote, so anything containing a space (the
    # repo path certainly does) has to be quoted here or dotnet sees it as several arguments.
    $stageArgs = $Def.Args
    if ($stageArgs -contains '--create-world') { $stageArgs += @('--world-log', $sidecar) }
    $argv = @($cli) + $stageArgs | ForEach-Object {
        if ($_ -match '\s') { '"' + $_ + '"' } else { $_ }
    }

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath 'dotnet' -ArgumentList $argv -WorkingDirectory $repo `
                       -WindowStyle Hidden -PassThru -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    # Touching Handle caches it in the returned object. Without this, Start-Process -PassThru loses the
    # handle when the process exits and ExitCode silently reads back as empty.
    $null = $p.Handle
    $timedOut = $false
    if (-not $p.WaitForExit($Def.TimeoutSec * 1000)) {
        $timedOut = $true
        try { $p.Kill($true) } catch { } # Only this stage's launcher and its descendants.
        Start-Sleep -Seconds 2
    }
    # The timed WaitForExit(ms) overload leaves ExitCode unpopulated; the parameterless one flushes the
    # process state so it can actually be read.
    try { $p.WaitForExit() } catch { }
    $sw.Stop()
    $exitCode = $null
    try { $exitCode = $p.ExitCode } catch { }
    $exit = if ($timedOut) { 'timeout' } else { "$exitCode" }

    if (Test-Path "$log.err") {
        $err = Get-Content "$log.err" -Raw -ErrorAction SilentlyContinue
        if ($err) { Add-Content -Path $log -Value $err -Encoding utf8 }
        Remove-Item "$log.err" -Force -ErrorAction SilentlyContinue
    }

    # --- evaluate -------------------------------------------------------------------------------------
    $rows = @()
    $hardFail = $false
    $searchIn = @($log)
    if ((Test-Path $sidecar) -and (Get-Item $sidecar).LastWriteTime -ge $p.StartTime) { $searchIn += $sidecar }
    foreach ($a in $Def.Assert) {
        $hit = Select-String -Path $searchIn -Pattern $a.Pattern -SimpleMatch:$false -ErrorAction SilentlyContinue | Select-Object -First 1
        $present = $null -ne $hit
        $ok = if ($a.Expect -eq 'Present') { $present } else { -not $present }
        if (-not $ok -and $a.Hard) { $hardFail = $true }
        $evidence = if ($hit) { $hit.Line.Trim() } else { '(not found)' }
        if ($evidence.Length -gt 200) { $evidence = $evidence.Substring(0, 200) + '...' }
        $rows += [pscustomobject]@{
            Ok = $ok; Expect = $a.Expect; Pattern = $a.Pattern; Hard = [bool]$a.Hard; Why = $a.Why; Evidence = $evidence
        }
    }

    if ($null -ne $Def.ExpectExit -and -not $timedOut) {
        $exitOk = ($exitCode -eq $Def.ExpectExit)
        $rows += [pscustomobject]@{ Ok = $exitOk; Expect = "exit $($Def.ExpectExit)"; Pattern = 'exit code'; Hard = $true
                                    Why = 'wrong exit code'; Evidence = "exit $exitCode" }
        if (-not $exitOk) { $hardFail = $true }
    }

    # --- invariants -----------------------------------------------------------------------------------
    $inv = @()
    $inv += [pscustomobject]@{ Ok = $p.HasExited; Name = 'owned launcher exited (engine uses kill-on-close job)'; Evidence = "launcher PID $($p.Id)" }

    $cacheAfter = if (Test-Path $cacheFile) { (Get-Item $cacheFile).Length } else { -1 }
    if (-not $Def.KeepsCache) {
        # A stage that did not pass -mod-distance-cache must leave SandBox's own cache in place. Note that
        # ending vanilla having started modded is the override RESTORING it, which is the designed behaviour.
        $isVanilla = ($cacheAfter -eq $vanillaCacheBytes)
        $inv += [pscustomobject]@{ Ok = $isVanilla; Name = 'distance cache is SandBox''s own'
                                   Evidence = "$cacheBefore -> $cacheAfter bytes (vanilla is $vanillaCacheBytes)" }
        if (-not $isVanilla) { $hardFail = $true }
    }
    else {
        $inv += [pscustomobject]@{ Ok = $true; Name = 'distance cache (stage opted in to the override)'
                                   Evidence = "$cacheBefore -> $cacheAfter bytes" }
    }

    # --- report ---------------------------------------------------------------------------------------
    $verdict = if ($rows.Where({ -not $_.Ok }).Count -eq 0 -and $inv.Where({ -not $_.Ok }).Count -eq 0) { 'PASS' } else { 'FAIL' }
    $colour = if ($verdict -eq 'PASS') { 'Green' } else { 'Red' }
    Write-Host "  $verdict  (exit $exit, $([int]$sw.Elapsed.TotalSeconds)s)" -ForegroundColor $colour
    foreach ($r in $rows) {
        $mark = if ($r.Ok) { 'ok  ' } else { if ($r.Hard) { 'HARD' } else { 'soft' } }
        # Windows PowerShell 5.1 cannot use `if` as an argument expression, so the colour is hoisted.
        $rowColour = if ($r.Ok) { 'DarkGray' } else { 'Yellow' }
        Write-Host "    [$mark] $($r.Expect) /$($r.Pattern)/ -> $($r.Evidence)" -ForegroundColor $rowColour
        if (-not $r.Ok) { Write-Host "           $($r.Why)" -ForegroundColor Yellow }
    }
    foreach ($i in $inv) {
        if (-not $i.Ok) { Write-Host "    [HARD] invariant: $($i.Name) -> $($i.Evidence)" -ForegroundColor Red }
    }

    $md = @()
    $md += ""
    $md += "## stage ``$Name`` - $verdict"
    $md += ""
    $md += "$($Def.Why)"
    $md += ""
    $md += "- run: ``$stamp``, exit ``$exit``, $([int]$sw.Elapsed.TotalSeconds)s"
    $md += "- log: ``$log``"
    $md += ""
    $md += "| | expect | pattern | evidence |"
    $md += "|---|---|---|---|"
    foreach ($r in $rows) {
        $m = if ($r.Ok) { 'ok' } else { if ($r.Hard) { '**HARD FAIL**' } else { 'soft fail' } }
        $md += "| $m | $($r.Expect) | ``$($r.Pattern)`` | ``$($r.Evidence)`` |"
    }
    foreach ($i in $inv) {
        $m = if ($i.Ok) { 'ok' } else { '**HARD FAIL**' }
        $md += "| $m | invariant | $($i.Name) | $($i.Evidence) |"
    }
    if ($Def.Report) {
        $md += ""
        $md += "Observed:"
        foreach ($pat in $Def.Report) {
            $hits = Select-String -Path $log -Pattern $pat -ErrorAction SilentlyContinue | Select-Object -First 3
            foreach ($h in $hits) { $md += "- ``$($h.Matches[0].Value)``" }
        }
    }
    if (Test-Path $sidecar) {
        $md += ""
        $md += "World-creation phases (sidecar):"
        $md += '```'
        $md += (Get-Content $sidecar -Tail 20)
        $md += '```'
    }
    if ($Def.VerifySave) {
        $md += ""
        $md += "Save verification (``saves``):"
        $savesOut = & dotnet $cli saves 2>&1 | Select-String -Pattern $Def.VerifySave
        if ($savesOut) { foreach ($s in $savesOut) { $md += "- ``$($s.Line.Trim())``" } }
        else { $md += "- **save '$($Def.VerifySave)' not found**"; $hardFail = $true }
    }
    Add-Content -Path $results -Value ($md -join "`r`n") -Encoding utf8

    return [pscustomobject]@{ Verdict = $verdict; HardFail = $hardFail }
}

# --- main --------------------------------------------------------------------------------------------

New-Item -ItemType Directory -Force -Path $logDir | Out-Null

if (-not $NoBuild) {
    # Build the CLI project, not the solution. The spike only drives the CLI, and building everything fails
    # whenever the WPF app is open -- it holds its own output DLLs open, which is a completely normal thing
    # for the user to be doing while this runs. The CLI project pulls in Core, Coop and the compat modules.
    Write-Host "building Release (CLI only)..." -ForegroundColor Cyan
    & dotnet build (Join-Path $repo 'src\ModderLords.Cli\ModderLords.Cli.csproj') -c Release -v q --nologo
    if ($LASTEXITCODE -ne 0) { Write-Host "build FAILED" -ForegroundColor Red; exit 1 }
}
if (-not (Test-Path $cli)) { Write-Host "CLI not built at $cli" -ForegroundColor Red; exit 1 }

Set-Content -Path $results -Value "# Spike results`r`n`r`nRun $(Get-Date -Format 'yyyy-MM-dd HH:mm'), stage selection ``$Stage``." -Encoding utf8

$toRun = if ($Stage -eq 'all') { $stages.Keys } else { @($Stage) }
$anyFail = $false
foreach ($name in $toRun) {
    $r = Invoke-Stage -Name $name -Def $stages[$name]
    if ($r.Verdict -ne 'PASS') { $anyFail = $true }
    if ($r.HardFail) {
        Write-Host ""
        Write-Host "HARD STOP at stage $name -- not running the remaining stages." -ForegroundColor Red
        Add-Content -Path $results -Value "`r`n> **HARD STOP at stage ``$name``** - remaining stages skipped." -Encoding utf8
        break
    }
}

Write-Host ""
Write-Host "results -> $results" -ForegroundColor Cyan
if ($anyFail) { exit 1 } else { exit 0 }
