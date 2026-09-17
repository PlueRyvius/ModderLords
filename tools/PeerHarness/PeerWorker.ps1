#requires -Version 7.0
param([Parameter(Mandatory)][string]$SpecFile)
$ErrorActionPreference='Stop'
Add-Type -Path (Join-Path $PSScriptRoot 'OwnedPeer.cs')
# No target can start until the supervisor confirms successful job assignment.
if ([Console]::ReadLine() -ne 'go') { exit 90 }
$spec=Get-Content -LiteralPath $SpecFile -Raw | ConvertFrom-Json -AsHashtable
$info=[Diagnostics.ProcessStartInfo]::new($spec.Executable)
$info.WorkingDirectory=$spec.WorkingDirectory
foreach ($argument in $spec.Arguments) { $info.ArgumentList.Add([string]$argument) }
foreach ($key in $spec.Removed) { $info.Environment.Remove([string]$key) | Out-Null }
foreach ($key in $spec.Environment.Keys) { $info.Environment[$key]=[string]$spec.Environment[$key] }
exit [ModderLords.TestHarness.OwnedPeer]::RunTarget($info,[bool]$spec.Visible)
