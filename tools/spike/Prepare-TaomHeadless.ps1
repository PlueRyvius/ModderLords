#requires -Version 7.0
<# Creates only simulation asset subsets and a headless map in an existing diagnostic workspace. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Workspace,
    [Parameter(Mandatory)][string]$Python
)
$ErrorActionPreference = 'Stop'
$Workspace = [IO.Path]::GetFullPath($Workspace)
foreach ($part in @('Game','DedicatedServer')) {
    $root = Join-Path $Workspace $part
    if (-not (Test-Path (Join-Path $root '.isolated-copy-complete')) -or
        (Get-Item -LiteralPath $root).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
        throw "Expected a real isolated $part copy; run Run-Vanilla.ps1 -Stage Setup -Recipe TaomFull first"
    }
}
$prepared = Join-Path $Workspace ('headless-prepared-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory $prepared | Out-Null
foreach ($module in @('LOTRLOME_Armory','TAOM','TAOM_Map')) {
    $root = Join-Path $Workspace "Game\Modules\$module"
    $assets = Join-Path $root 'AssetPackages'
    $target = Join-Path $root 'DsAssetPackages'
    foreach ($directory in @($root,$assets,$target)) {
        if ((Test-Path -LiteralPath $directory) -and
            (Get-Item -LiteralPath $directory).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing to write through a module link: $directory"
        }
    }
    $subset = Join-Path $prepared $module
    & $Python (Join-Path $PSScriptRoot 'Build-HeadlessAssets.py') --source $assets --output $subset
    if ($LASTEXITCODE -ne 0) { throw "Asset subset failed: $module" }
    New-Item -ItemType Directory -Force $target | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $subset -Filter '*.tpac') {
        $destination = Join-Path $target $file.Name
        if (Test-Path -LiteralPath $destination) {
            if ((Get-FileHash -LiteralPath $destination).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
                throw "Refusing to overwrite a different existing server asset: $destination"
            }
        } else { Copy-Item -LiteralPath $file.FullName -Destination $destination }
    }
}
$scene = Join-Path $prepared 'Main_map'
& $Python (Join-Path $PSScriptRoot 'Build-HeadlessMap.py') --source (Join-Path $Workspace 'Game\Modules\TAOM_Map\SceneObj\Main_map') --output $scene
if ($LASTEXITCODE -ne 0) { throw 'Headless map preparation failed' }
Write-Output "Prepared. Pass -HeadlessScene '$scene' to Create and Reload runs."
