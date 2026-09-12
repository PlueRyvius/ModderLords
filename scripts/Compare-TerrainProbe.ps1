# Diffs two terrain-probe CSVs written by ModderLords.CompatSync.TerrainProbe — normally a dedicated server's
# against a single-player client's on the same map — and says whether the map scenes agree.
#
# Usage: .\scripts\Compare-TerrainProbe.ps1 -Server terrain-probe-server.csv -Client terrain-probe-client.csv
#
# Read the header verdict first. If the scene CRCs or borders differ the two runs are not on the same map, and the
# per-column rates below mean nothing; fix that before reading further.
param(
    [Parameter(Mandatory = $true)][string]$Server,
    [Parameter(Mandatory = $true)][string]$Client,
    [int]$Examples = 12
)

$ErrorActionPreference = 'Stop'

function Read-Probe([string]$path) {
    if (-not (Test-Path $path)) { throw "no such probe file: $path" }
    $lines = Get-Content -LiteralPath $path
    $header = $lines | Where-Object { $_.StartsWith('#') }
    $body = $lines | Where-Object { -not $_.StartsWith('#') }
    if ($body.Count -lt 2) { throw "$path has no samples" }
    [pscustomobject]@{
        Path    = $path
        Header  = $header
        Columns = $body[0].Split(',')
        Rows    = $body | Select-Object -Skip 1
    }
}

$a = Read-Probe $Server
$b = Read-Probe $Client

Write-Host "server header:" -ForegroundColor Cyan
$a.Header | ForEach-Object { "  $_" }
Write-Host "client header:" -ForegroundColor Cyan
$b.Header | ForEach-Object { "  $_" }

# Identity first. A CRC or border disagreement makes every row difference below meaningless.
function Get-HeaderField([string[]]$header, [string]$name) {
    foreach ($line in $header) {
        foreach ($token in $line.TrimStart('#').Trim().Split(' ')) {
            if ($token.StartsWith("$name=")) { return $token.Substring($name.Length + 1) }
        }
    }
    return $null
}
$identity = @('borders', 'terrainSize', 'sceneXmlCrc', 'navMeshCrc', 'navMeshFaces')
$identityDiffs = @()
$notReported = @()
foreach ($field in $identity) {
    $x = Get-HeaderField $a.Header $field
    $y = Get-HeaderField $b.Header $field
    if ($x -eq $y) { continue }
    # A CRC of 0 is "this scene does not carry one", not "a different map". The stripped headless scene reports 0 for
    # both, and treating that as a map mismatch hides the very comparison this script exists to make.
    if (($field -eq 'sceneXmlCrc' -or $field -eq 'navMeshCrc') -and ($x -eq '0' -or $y -eq '0')) {
        $notReported += "  $field : server=$x client=$y  (a 0 means the scene does not report one)"
        continue
    }
    $identityDiffs += "  $field : server=$x client=$y"
}
if ($notReported.Count -gt 0) {
    Write-Host "`nnot reported by one side (not a map difference):" -ForegroundColor DarkGray
    $notReported | ForEach-Object { $_ }
}
if ($identityDiffs.Count -gt 0) {
    Write-Host "`nSTOP: the two runs are not on the same map." -ForegroundColor Red
    $identityDiffs | ForEach-Object { $_ }
    Write-Host "Row comparisons below are meaningless until this matches." -ForegroundColor Red
} else {
    Write-Host "`nmap identity matches (borders, terrain size, navmesh face count)" -ForegroundColor Green
}

if (($a.Columns -join ',') -ne ($b.Columns -join ',')) { throw "probe files have different columns; they came from different builds" }
if ($a.Rows.Count -ne $b.Rows.Count) { throw "probe files have different sample counts ($($a.Rows.Count) vs $($b.Rows.Count)); the grid or the extra points differ" }

$columns = $a.Columns
$differing = @{}
foreach ($c in $columns) { $differing[$c] = 0 }
$shown = New-Object System.Collections.ArrayList

for ($i = 0; $i -lt $a.Rows.Count; $i++) {
    $left = $a.Rows[$i].Split(',')
    $right = $b.Rows[$i].Split(',')
    # Same grid arithmetic on both sides, but the two runs get their borders from different places, so the last bit of
    # a float can differ and print as 1034.562 against 1034.563. Compare with a tolerance; a real misalignment is
    # orders of magnitude bigger than one grid cell's rounding.
    if ([math]::Abs([double]$left[0] - [double]$right[0]) -gt 0.01 -or [math]::Abs([double]$left[1] - [double]$right[1]) -gt 0.01) {
        throw "row $i samples different positions ($($left[0]),$($left[1])) vs ($($right[0]),$($right[1])); the grids are not aligned"
    }
    $rowDiffs = @()
    for ($c = 2; $c -lt $columns.Count; $c++) {
        if ($left[$c] -ne $right[$c]) {
            $differing[$columns[$c]]++
            $rowDiffs += "$($columns[$c]): server=$($left[$c]) client=$($right[$c])"
        }
    }
    if ($rowDiffs.Count -gt 0 -and $shown.Count -lt $Examples) {
        [void]$shown.Add("  ($($left[0]), $($left[1]))  " + ($rowDiffs -join '; '))
    }
}

$total = $a.Rows.Count
Write-Host "`n$total samples compared" -ForegroundColor Cyan
$any = $false
foreach ($c in $columns[2..($columns.Count - 1)]) {
    $n = $differing[$c]
    if ($n -eq 0) { continue }
    $any = $true
    $pct = [math]::Round(100.0 * $n / $total, 1)
    Write-Host ("  {0,-12} {1,7} of {2} disagree ({3}%)" -f $c, $n, $total, $pct) -ForegroundColor Yellow
}
if (-not $any) {
    Write-Host "  every column agrees on every sample" -ForegroundColor Green
    Write-Host "`nVerdict: the stripped server scene answers terrain queries the same as a full client scene." -ForegroundColor Green
    Write-Host "That refutes the standing hypothesis for the field-battle crash. Look elsewhere." -ForegroundColor Green
} else {
    Write-Host "`nfirst $($shown.Count) differing samples:" -ForegroundColor Yellow
    $shown | ForEach-Object { $_ }
    Write-Host "`nVerdict: the server and client map scenes disagree. The columns above name what to fix." -ForegroundColor Yellow
}
