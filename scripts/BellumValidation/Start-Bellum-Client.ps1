[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$cli = Join-Path $repository 'src\ModderLords.Cli\bin\Release\net10.0\ModderLords.Cli.dll'
$profile = Join-Path $PSScriptRoot 'client-profile.json'
$bundledBin = Join-Path $repository 'artifacts\ModderLords-1.1.1\compat\ModderLords.Compat\bin\Win64_Shipping_Client'
$installedBin = 'D:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\Modules\ModderLords.Compat\bin\Win64_Shipping_Client'

foreach ($required in @($cli, $profile, $bundledBin, $installedBin)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required validation input is missing: $required" }
}

foreach ($name in @('ModderLords.CompatSync.Coop.dll', 'ModderLords.Operations.dll')) {
    $bundled = Join-Path $bundledBin $name
    $installed = Join-Path $installedBin $name
    if (-not (Test-Path -LiteralPath $installed)) { throw "The validation client module is missing $installed" }
    if ((Get-FileHash -LiteralPath $bundled).Hash -ne (Get-FileHash -LiteralPath $installed).Hash) {
        throw "The installed client $name is not the validation build. Stop and ask Codex to restage it."
    }
}

$env:MODDERLORDS_BELLUM_VALIDATION = 'bellum-civile-1.3.1-isolated'

Write-Host ''
Write-Host 'Launching the Bellum validation client.' -ForegroundColor Cyan
Write-Host 'In Coop, direct-connect to 127.0.0.1 on UDP port 4200 with no password.' -ForegroundColor Yellow
Write-Host ''

& dotnet $cli play --profile-file $profile
exit $LASTEXITCODE
