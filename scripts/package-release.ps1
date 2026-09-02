# Builds a self-contained win-x64 release zip: app + hook + README + roadmap.
# Usage: .\scripts\package-release.ps1 -Version 0.3.0
param([Parameter(Mandatory = $true)][string]$Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "artifacts\ModularBannerlordsCoop-$Version"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out | Out-Null

dotnet publish (Join-Path $root 'src\ModularCoop.App\ModularCoop.App.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:Version=$Version -o $out
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# The hook is loaded into the engine (net6.0); publish copies it via the CopyHook target, but make sure.
$hook = Join-Path $root 'src\ModularCoop.Hook\bin\Release\net6.0\ModularCoop.Hook.dll'
if (-not (Test-Path $hook)) { dotnet build (Join-Path $root 'src\ModularCoop.Hook\ModularCoop.Hook.csproj') -c Release |Copy-Item (Join-Path $root 'srcModularCoop.HookinRelease
et6.0ModularCoop.Hook.dll') $out -Force

Copy-Item (Join-Path $root 'README.md') (Join-Path $out 'README.md') -Force
Copy-Item (Join-Path $root 'README.md') (Join-Path $out 'README.txt') -Force
Copy-Item (Join-Path $root 'docs\ROADMAP.md') $out -Force
Copy-Item (Join-Path $root 'LICENSE') $out -Force -ErrorAction SilentlyContinue

$zip = "$out.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "Release package: $zip"
