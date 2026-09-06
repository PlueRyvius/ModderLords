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
$out = Join-Path $root "artifacts\ModderLords-$Version"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out | Out-Null

# The hook is loaded into the engine (net6.0); build it first so the app's CopyHook target finds it.
dotnet build (Join-Path $root 'src\ModderLords.Hook\ModderLords.Hook.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw "hook build failed" }

dotnet publish (Join-Path $root 'src\ModderLords.App\ModderLords.App.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
    -p:Version=$Version -o $out
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# Anything the exe cannot swallow goes in a folder rather than beside it. The hook must stay a real file:
# the ENGINE loads it through DOTNET_STARTUP_HOOKS, in its own process, so it can never live inside our exe.
$binOut = Join-Path $out 'bin'
New-Item -ItemType Directory -Path $binOut -Force | Out-Null
Copy-Item (Join-Path $root 'src\ModderLords.Hook\bin\Release\net6.0\ModderLords.Hook.dll') $binOut -Force
# The curated compat database is a Content item of Core; publish carries it next to the exe. Copy as a belt-and-braces.
$dataOut = Join-Path $out 'data'
New-Item -ItemType Directory -Path $dataOut -Force | Out-Null
Copy-Item (Join-Path $root 'src\ModderLords.Core\compat-db.json') $dataOut -Force
# Publish also drops it beside the exe as a Core content item; one copy is enough, and data\ is the one we read.
Remove-Item (Join-Path $out 'compat-db.json') -Force -ErrorAction SilentlyContinue

# The Compat module (net472, loaded by the engine) ships under compat\ next to the exe.
dotnet build (Join-Path $root 'src\ModderLords.Compat\ModderLords.Compat.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw "compat build failed" }
$compatOut = Join-Path $out 'compat\DedicatedServer.ModderLordsCompat'
New-Item -ItemType Directory -Path $compatOut -Force | Out-Null
Copy-Item (Join-Path $root 'src\ModderLords.Compat\_Module\*') $compatOut -Recurse -Force
Get-ChildItem $compatOut -Recurse -Filter *.pdb | Remove-Item -Force

# The shared client+server sync module (players copy this one into their game's Modules folder).
dotnet build (Join-Path $root 'src\ModderLords.CompatSync\ModderLords.CompatSync.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw "compat sync build failed" }
dotnet build (Join-Path $root 'src\ModderLords.CompatSync.Coop\ModderLords.CompatSync.Coop.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw "compat sync adapter build failed" }
$syncOut = Join-Path $out 'compat\ModderLords.Compat'
New-Item -ItemType Directory -Path $syncOut -Force | Out-Null
Copy-Item (Join-Path $root 'src\ModderLords.CompatSync\_Module\*') $syncOut -Recurse -Force
Get-ChildItem $syncOut -Recurse -Filter *.pdb | Remove-Item -Force

# The _Module output dirs are source-tree build outputs that accumulate leftovers (e.g. a win-x64 RID subfolder
# from a self-contained build). A Bannerlord module only wants the flat Win64_Shipping_* bins, so strip the rest.
foreach ($m in @($compatOut, $syncOut)) {
    Get-ChildItem $m -Recurse -Directory | Where-Object { $_.Name -eq 'win-x64' } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Get-ChildItem $m -Recurse -Filter *.json | Where-Object { $_.Name -match 'deps|runtimeconfig' } | Remove-Item -Force -ErrorAction SilentlyContinue
    Get-ChildItem $m -Recurse -Filter recipes.json | Remove-Item -Force -ErrorAction SilentlyContinue
}
Copy-Item (Join-Path $root 'README.md') (Join-Path $out 'README.txt') -Force
$docsOut = Join-Path $out 'docs'
New-Item -ItemType Directory -Path $docsOut -Force | Out-Null
Copy-Item (Join-Path $root 'README.md') $docsOut -Force
Copy-Item (Join-Path $root 'docs\ROADMAP.md') $docsOut -Force
if (Test-Path (Join-Path $root 'LICENSE')) { Copy-Item (Join-Path $root 'LICENSE') $out -Force }
if (Test-Path (Join-Path $root 'THIRD-PARTY-NOTICES.md')) { Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $docsOut -Force }

# Leftovers a self-contained publish drops beside the exe that no player needs.
Get-ChildItem $out -File | Where-Object { $_.Extension -eq '.pdb' -or $_.Name -eq 'createdump.exe' } | Remove-Item -Force

# The point of all the above: if the top level ever fills back up with loose files, fail loudly.
$loose = @(Get-ChildItem $out -File)
if ($loose.Count -gt 4) {
    throw "Release root has $($loose.Count) loose files; it should be the exe plus a couple of documents. Put new files in a folder. ($($loose.Name -join ', '))"
}

$zip = "$out.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "Release package: $zip"
