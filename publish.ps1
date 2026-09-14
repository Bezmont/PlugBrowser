<#
.SYNOPSIS
    Builds the downloadable release: a self-contained PlugBrowser folder, zipped.

.DESCRIPTION
    The same script runs locally and in the GitHub build (.github/workflows/build.yml), so a release
    can always be reproduced by hand.

      1. build-native.ps1 - NetPlugHost's native DLL and the native worker, freshly built
      2. dotnet publish   - the app, self-contained for win-x64, so users need no .NET install
      3. LICENSE and README beside the exe, then zipped as PlugBrowser-<version>-win-x64.zip

.PARAMETER Version
    Release version, e.g. 1.2.0 or v1.2.0-beta.1. A leading "v" (as in a git tag) is dropped.

.PARAMETER SkipNative
    Use the native binaries already built rather than rebuilding them - for when build-native.ps1 has
    just run, as it has in the GitHub build.

.EXAMPLE
    .\publish.ps1 -Version 1.0.0
#>
[CmdletBinding()]
param(
    [string]$Version = '0.0.0-local',
    [string]$OutDir = (Join-Path $PSScriptRoot 'artifacts'),
    [switch]$SkipNative
)

$ErrorActionPreference = 'Stop'

$Version = $Version.TrimStart('v')
# The SDK derives the file and assembly versions from the prefix alone, so the two halves are passed
# separately; a Version property with a suffix would leave those at their 1.0.0 default.
$prefix, $suffix = $Version -split '-', 2
if ($prefix -notmatch '^\d+\.\d+\.\d+$') { throw "Version '$Version' is not in the form 1.2.3 or 1.2.3-suffix." }

if (-not $SkipNative) {
    & (Join-Path $PSScriptRoot 'build-native.ps1') -Configuration Release -RebuildNetPlugHost
}

$publishDir = Join-Path $OutDir 'PlugBrowser'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

$publishArgs = @(
    'publish', (Join-Path $PSScriptRoot 'src\PlugBrowser.App\PlugBrowser.App.csproj'),
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-o', $publishDir,
    # Precompiled code: noticeably faster start-up, the splash screen's whole reason for being.
    '-p:PublishReadyToRun=true',
    "-p:VersionPrefix=$prefix",
    '-nologo'
)
if ($suffix) { $publishArgs += "-p:VersionSuffix=$suffix" }

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

# GPLv3 requires the licence to travel with the binaries.
Copy-Item (Join-Path $PSScriptRoot 'LICENSE') (Join-Path $publishDir 'LICENSE.txt')
Copy-Item (Join-Path $PSScriptRoot 'README.md') $publishDir

New-Item -ItemType Directory -Force $OutDir | Out-Null
$zip = Join-Path $OutDir "PlugBrowser-$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $publishDir -DestinationPath $zip -CompressionLevel Optimal

$sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ""
Write-Host "Built $zip ($sizeMb MB)" -ForegroundColor Green

# The path, for the GitHub build to pick up.
$zip
