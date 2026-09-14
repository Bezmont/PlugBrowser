<#
.SYNOPSIS
    Builds the native worker, which is what lets PlugBrowser load copy-protected plugins.

.DESCRIPTION
    The managed worker cannot load PACE/iLok-protected plugins at all - their anti-tamper treats a CLR
    process as an instrumented one and spins forever. The native worker has no runtime in its process,
    so those plugins load normally.

    Everything still works without this: the app falls back to the managed worker and simply reports
    those plugins as failures.

    Both native binaries - NetPlugHost's Vst3HostNative.dll and the worker - are built with the C++
    runtime linked in statically. Linked dynamically they would need the Visual C++ Redistributable,
    which many machines do not have, and on those every plugin would silently fail to load.

    Requires Visual Studio 2022 (or Build Tools) with the C++ workload.

.PARAMETER NetPlugHost
    NetPlugHost checkout to build against. Defaults to the submodule at external/NetPlugHost, or a
    checkout beside this repository when the submodule is absent.

.PARAMETER RebuildNetPlugHost
    Reconfigure and rebuild NetPlugHost's native DLL even if one is already built. Used for releases,
    so the shipped DLL is always a fresh, statically linked build.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$NetPlugHost = $(
        $submodule = Join-Path $PSScriptRoot 'external\NetPlugHost'
        if (Test-Path (Join-Path $submodule 'native\include\vst3_host_c.h')) { $submodule }
        else { Join-Path (Split-Path $PSScriptRoot -Parent) 'NetPlugHost' }),
    [switch]$RebuildNetPlugHost
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

if (-not (Test-Path (Join-Path $NetPlugHost 'native\include\vst3_host_c.h'))) {
    throw "NetPlugHost not found at $NetPlugHost. Run: git submodule update --init --recursive"
}
if (-not (Test-Path (Join-Path $NetPlugHost 'external\vst3sdk\pluginterfaces'))) {
    throw "The VST3 SDK is missing under $NetPlugHost\external\vst3sdk. Run: git submodule update --init --recursive"
}

$cmake = Find-CMake
Write-Host "cmake:       $cmake"
Write-Host "NetPlugHost: $NetPlugHost"

# Static C++ runtime (/MT, or /MTd for Debug); see the description above.
$staticRuntime = '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded$<$<CONFIG:Debug>:Debug>'

# The worker links NetPlugHost's flat C ABI, so that has to exist first.
$npBuild = Join-Path $NetPlugHost 'native/build'
$lib = Join-Path $NetPlugHost 'native\build\Release\Vst3HostNative.lib'
if ($RebuildNetPlugHost -or -not (Test-Path $lib)) {
    Write-Host "Building NetPlugHost's native DLL..."
    & $cmake -S (Join-Path $NetPlugHost 'native') -B $npBuild -A x64 $staticRuntime
    if ($LASTEXITCODE -ne 0) { throw 'NetPlugHost configure failed.' }
    & $cmake --build $npBuild --config Release
    if ($LASTEXITCODE -ne 0) { throw 'NetPlugHost build failed.' }
}

& $cmake -S "$PSScriptRoot\native" -B "$PSScriptRoot\native\build" -A x64 "-DNETPLUGHOST_DIR=$NetPlugHost" $staticRuntime
if ($LASTEXITCODE -ne 0) { throw 'Configure failed.' }

& $cmake --build "$PSScriptRoot\native\build" --config $Configuration
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$exe = Join-Path $PSScriptRoot 'native\build\bin\PlugBrowser.NativeWorker.exe'
Write-Host ""
Write-Host "Built $exe" -ForegroundColor Green
Write-Host "Run 'dotnet build PlugBrowser.sln' to copy it next to the app."
