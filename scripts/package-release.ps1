# Builds a self-contained win-x64 release zip: app + hook + README + roadmap.
# Usage: .\scripts\package-release.ps1 -Version 0.3.0
param([Parameter(Mandatory = $true)][string]$Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

# The version lives in Directory.Build.props so every build reports it, dev builds included. Refuse to package a
# different number rather than shipping a zip whose title bar disagrees with its release tag.
$propsPath = Join-Path $root 'Directory.Build.props'
$declared = ([xml](Get-Content $propsPath)).Project.PropertyGroup.VersionPrefix
if ($declared -ne $Version) {
    throw "Directory.Build.props says $declared but you asked to package $Version. Bump VersionPrefix first (one line, then commit it)."
}
$out = Join-Path $root "artifacts\ModularBannerlordsCoop-$Version"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out | Out-Null

# The hook is loaded into the engine (net6.0); build it first so the app's CopyHook target finds it.
dotnet build (Join-Path $root 'src\ModularCoop.Hook\ModularCoop.Hook.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw "hook build failed" }

dotnet publish (Join-Path $root 'src\ModularCoop.App\ModularCoop.App.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:Version=$Version -o $out
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Copy-Item (Join-Path $root 'src\ModularCoop.Hook\bin\Release\net6.0\ModularCoop.Hook.dll') $out -Force
# The curated compat database is a Content item of Core; publish carries it next to the exe. Copy as a belt-and-braces.
Copy-Item (Join-Path $root 'src\ModularCoop.Core\compat-db.json') $out -Force

# The Compat module (net472, loaded by the engine) ships under compat\ next to the exe.
dotnet build (Join-Path $root 'src\ModularCoop.Compat\ModularCoop.Compat.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw "compat build failed" }
$compatOut = Join-Path $out 'compat\DedicatedServer.ModularCoopCompat'
New-Item -ItemType Directory -Path $compatOut -Force | Out-Null
Copy-Item (Join-Path $root 'src\ModularCoop.Compat\_Module\*') $compatOut -Recurse -Force
Get-ChildItem $compatOut -Recurse -Filter *.pdb | Remove-Item -Force

# The shared client+server sync module (players copy this one into their game's Modules folder).
dotnet build (Join-Path $root 'src\ModularCoop.CompatSync\ModularCoop.CompatSync.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw "compat sync build failed" }
dotnet build (Join-Path $root 'src\ModularCoop.CompatSync.Coop\ModularCoop.CompatSync.Coop.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw "compat sync adapter build failed" }
$syncOut = Join-Path $out 'compat\ModularCoop.Compat'
New-Item -ItemType Directory -Path $syncOut -Force | Out-Null
Copy-Item (Join-Path $root 'src\ModularCoop.CompatSync\_Module\*') $syncOut -Recurse -Force
Get-ChildItem $syncOut -Recurse -Filter *.pdb | Remove-Item -Force

# The _Module output dirs are source-tree build outputs that accumulate leftovers (e.g. a win-x64 RID subfolder
# from a self-contained build). A Bannerlord module only wants the flat Win64_Shipping_* bins, so strip the rest.
foreach ($m in @($compatOut, $syncOut)) {
    Get-ChildItem $m -Recurse -Directory | Where-Object { $_.Name -eq 'win-x64' } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Get-ChildItem $m -Recurse -Filter *.json | Where-Object { $_.Name -match 'deps|runtimeconfig' } | Remove-Item -Force -ErrorAction SilentlyContinue
    Get-ChildItem $m -Recurse -Filter recipes.json | Remove-Item -Force -ErrorAction SilentlyContinue
}
Copy-Item (Join-Path $root 'README.md') (Join-Path $out 'README.md') -Force
Copy-Item (Join-Path $root 'README.md') (Join-Path $out 'README.txt') -Force
Copy-Item (Join-Path $root 'docs\ROADMAP.md') $out -Force
if (Test-Path (Join-Path $root 'LICENSE')) { Copy-Item (Join-Path $root 'LICENSE') $out -Force }
if (Test-Path (Join-Path $root 'THIRD-PARTY-NOTICES.md')) { Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $out -Force }

$zip = "$out.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "Release package: $zip"
