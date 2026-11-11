# Builds server\dll\build\Release\nebo.dll with Visual Studio 2022's MSVC (any edition, or the
# Build Tools) and CMake: the one on PATH, else the one bundled with Visual Studio.
param([string]$Config = 'Release')
$ErrorActionPreference = 'Stop'

$cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source
if (-not $cmake) {
    $vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -property installationPath
    $cmake = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
}
if (-not (Test-Path $cmake)) { throw 'CMake not found: install Visual Studio 2022 (C++ workload) or the Build Tools' }
$src   = $PSScriptRoot
$build = Join-Path $src 'build'

& $cmake -S $src -B $build -G 'Visual Studio 17 2022' -A x64 | Out-Null
if ($LASTEXITCODE) { throw 'cmake configure failed' }
& $cmake --build $build --config $Config -- /nologo /v:minimal
if ($LASTEXITCODE) { throw 'cmake build failed' }
Join-Path $build "$Config\nebo.dll"
