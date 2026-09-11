<#
.SYNOPSIS
    Builds the native worker, which is what lets PlugBrowser load copy-protected plugins.

.DESCRIPTION
    The managed worker cannot load PACE/iLok-protected plugins at all - their anti-tamper treats a CLR
    process as an instrumented one and spins forever. The native worker has no runtime in its process,
    so those plugins load normally.

    Everything still works without this: the app falls back to the managed worker and simply reports
    those plugins as failures.

    Requires Visual Studio 2022 (or Build Tools) with the C++ workload, and a built NetPlugHost.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$NetPlugHost = (Join-Path (Split-Path $PSScriptRoot -Parent) 'NetPlugHost')
)

$ErrorActionPreference = 'Stop'

function Find-CMake {
    $onPath = Get-Command cmake -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        foreach ($vs in & $vswhere -products * -format value -property installationPath) {
            $candidate = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
            if (Test-Path $candidate) { return $candidate }
        }
    }
    throw 'CMake not found. Install Visual Studio 2022 with the "Desktop development with C++" workload.'
}

$cmake = Find-CMake
Write-Host "cmake: $cmake"

# The worker links NetPlugHost's flat C ABI, so that has to exist first.
$lib = Join-Path $NetPlugHost 'native\build\Release\Vst3HostNative.lib'
if (-not (Test-Path $lib)) {
    Write-Host "Building NetPlugHost first ($NetPlugHost)..."
    & $cmake -S (Join-Path $NetPlugHost 'native') -B (Join-Path $NetPlugHost 'native/build') -A x64
    if ($LASTEXITCODE -ne 0) { throw 'NetPlugHost configure failed.' }
    & $cmake --build (Join-Path $NetPlugHost 'native/build') --config Release
    if ($LASTEXITCODE -ne 0) { throw 'NetPlugHost build failed.' }
}

& $cmake -S "$PSScriptRoot\native" -B "$PSScriptRoot\native\build" -A x64 "-DNETPLUGHOST_DIR=$NetPlugHost"
if ($LASTEXITCODE -ne 0) { throw 'Configure failed.' }

& $cmake --build "$PSScriptRoot\native\build" --config $Configuration
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$exe = Join-Path $PSScriptRoot 'native\build\bin\PlugBrowser.NativeWorker.exe'
Write-Host ""
Write-Host "Built $exe" -ForegroundColor Green
Write-Host "Run 'dotnet build PlugBrowser.sln' to copy it next to the app."
