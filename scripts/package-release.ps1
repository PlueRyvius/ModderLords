# Builds a self-contained win-x64 release zip: app + hook + README + roadmap.
# Usage: .\scripts\package-release.ps1 -Version 0.3.0
param([Parameter(Mandatory = $true)][string]$Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
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

# The Compat module (net472, loaded by the engine) ships under compat\ next to the exe.
dotnet build (Join-Path $root 'src\ModularCoop.Compat\ModularCoop.Compat.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw "compat build failed" }
$compatOut = Join-Path $out 'compat\DedicatedServer.ModularCoopCompat'
New-Item -ItemType Directory -Path $compatOut -Force | Out-Null
Copy-Item (Join-Path $root 'src\ModularCoop.Compat\_Module\*') $compatOut -Recurse -Force
Get-ChildItem $compatOut -Recurse -Filter *.pdb | Remove-Item -Force
Copy-Item (Join-Path $root 'README.md') (Join-Path $out 'README.md') -Force
Copy-Item (Join-Path $root 'README.md') (Join-Path $out 'README.txt') -Force
Copy-Item (Join-Path $root 'docs\ROADMAP.md') $out -Force
if (Test-Path (Join-Path $root 'LICENSE')) { Copy-Item (Join-Path $root 'LICENSE') $out -Force }

$zip = "$out.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "Release package: $zip"
