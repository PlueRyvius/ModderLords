[CmdletBinding()]
param(
    [string]$SteamRoot = $(if ($env:MODDERLORDS_STEAM_ROOT) { $env:MODDERLORDS_STEAM_ROOT } else { 'C:\Program Files (x86)\Steam' })
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$cli = Join-Path $repository 'src\ModderLords.Cli\bin\Release\net10.0\ModderLords.Cli.dll'
$template = Join-Path $PSScriptRoot 'client-profile.json'
$game = Join-Path $SteamRoot 'steamapps\common\Mount & Blade II Bannerlord'
$workshop = Join-Path $SteamRoot 'steamapps\workshop\content\261550'
$version = ([xml](Get-Content (Join-Path $repository 'Directory.Build.props'))).SelectSingleNode('//VersionPrefix').InnerText
$bundledBin = Join-Path $repository "artifacts\ModderLords-$version\compat\ModderLords.Compat\bin\Win64_Shipping_Client"
$installedBin = Join-Path $game 'Modules\ModderLords.Compat\bin\Win64_Shipping_Client'

foreach ($required in @($cli, $template, $bundledBin, $installedBin)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required validation input is missing: $required" }
}

foreach ($name in @('ModderLords.CompatSync.Coop.dll', 'ModderLords.Operations.dll')) {
    $bundled = Join-Path $bundledBin $name
    $installed = Join-Path $installedBin $name
    if (-not (Test-Path -LiteralPath $installed)) { throw "The validation client module is missing $installed" }
    if ((Get-FileHash -LiteralPath $bundled).Hash -ne (Get-FileHash -LiteralPath $installed).Hash) {
        throw "The installed client $name is not the validation build. Restage the validation build before launching."
    }
}

$profile = Join-Path ([IO.Path]::GetTempPath()) 'modderlords-bellum-client-profile.json'
$escape = { param($value) ($value | ConvertTo-Json).Trim('"') }
(Get-Content -LiteralPath $template -Raw).Replace('{GameRoot}', (& $escape $game)).Replace('{WorkshopRoot}', (& $escape $workshop)) |
    Set-Content -LiteralPath $profile -Encoding UTF8

$env:MODDERLORDS_BELLUM_VALIDATION = 'bellum-civile-1.3.1-isolated'

Write-Host ''
Write-Host 'Launching the Bellum validation client.' -ForegroundColor Cyan
Write-Host 'In Coop, direct-connect to 127.0.0.1 on UDP port 4200 with no password.' -ForegroundColor Yellow
Write-Host ''

& dotnet $cli play --profile-file $profile
exit $LASTEXITCODE
