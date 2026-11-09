<#
.SYNOPSIS
  Builds a release of Mochi: runs the tests, publishes a self-contained single-file Mochi.exe
  (no .NET install needed) and a Mochi-Setup.exe (same binary; the name switches it to installer mode).
.EXAMPLE
  pwsh -File build/publish.ps1
#>
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
Set-Location $root

if (-not $SkipTests) {
    Write-Host 'Running tests...' -ForegroundColor Cyan
    dotnet test tests/Mochi.Core.Tests -c $Configuration -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

Write-Host 'Publishing self-contained single-file executable...' -ForegroundColor Cyan
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
dotnet publish src/Mochi.App -c $Configuration -r $Runtime --self-contained true -nologo -v q `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $dist
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

Copy-Item (Join-Path $dist 'Mochi.exe') (Join-Path $dist 'Mochi-Setup.exe')
Get-ChildItem $dist -Exclude 'Mochi.exe', 'Mochi-Setup.exe' | Remove-Item -Recurse -Force

Get-ChildItem $dist | ForEach-Object { '{0,-18} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
Write-Host "Done: $dist" -ForegroundColor Green
exit 0
