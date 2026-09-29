[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$cli = Join-Path $repository 'src\ModderLords.Cli\bin\Release\net10.0\ModderLords.Cli.dll'
$bundle = Join-Path $repository 'artifacts\ModderLords-1.1.1'
$server = 'D:\Program Files (x86)\Steam\steamapps\workshop\content\261550\3770450698\DedicatedServer'
$game = 'D:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord'
$data = 'D:\Design\Bannerlord Mods\_bellum-civile-analysis-data\live'

foreach ($required in @($cli, $bundle, $server, $game, (Join-Path $data 'Game Saves\BellumCompatSmoke.sav'))) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required validation input is missing: $required" }
}

Remove-Item Env:MODDERLORDS_BELLUM_SNAPSHOT_PROBE -ErrorAction SilentlyContinue
$env:MODDERLORDS_BELLUM_VALIDATION = 'bellum-civile-1.3.1-isolated'

Write-Host ''
Write-Host 'Bellum validation server is starting.' -ForegroundColor Cyan
Write-Host 'Wait for the green SERVING line before starting the client.' -ForegroundColor Yellow
Write-Host "When testing is finished, return here, type stop, and press Enter." -ForegroundColor Yellow
Write-Host ''

& dotnet $cli launch `
    --root $server `
    --data-dir $data `
    --game $game `
    --bundle-root $bundle `
    --mods 'Bannerlord.Harmony:DependencyOnly,Bannerlord.ButterLib:DependencyOnly,Bannerlord.UIExtenderEx:DependencyOnly,Bannerlord.MBOptionScreen:DependencyOnly,BellumCivile:Run' `
    --settings-sync `
    --compat `
    --save BellumCompatSmoke `
    --port 7210 `
    --join-port 4200 `
    --quiet-engine

exit $LASTEXITCODE
