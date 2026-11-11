# Builds everything into one place:
#   Nebo.Client.exe          NEBO client: one file in the project root (framework-dependent,
#                           its libraries bundled inside)
#   app\tools\DiagramIndexer  airport-diagram indexer
#   server\dll\build\Release\nebo.dll  server DLL (installed by server\deploy.ps1)
# and runs the Core tests. One build type: developer aids (--snapshot, render timing) are
# included and stay off unless asked for on the command line.
# A running client is closed for the build and started again afterwards.
# -NoDcs skips the tests that need a DCS install (data cartridge tables): GitHub's release build.
param([switch]$SkipTests, [switch]$SkipDll, [switch]$NoDcs)
$ErrorActionPreference = 'Stop'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$root = $PSScriptRoot
$app = Join-Path $root 'app'
$client = Join-Path $root 'Nebo.Client.exe'

# A running client locks its file: close it (gracefully, so it saves its layout), build,
# then start it again. Force-closed only if it does not exit in time.
$running = @(Get-Process Nebo.Client -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$root\*" })
foreach ($p in $running) {
    Write-Host "Closing the NEBO client (pid $($p.Id)) for the build"
    [void]$p.CloseMainWindow()
    if (-not $p.WaitForExit(15000)) {
        Write-Host "  did not exit in 15 s: force-closing"
        Stop-Process -Id $p.Id -Force
        $p.WaitForExit(5000) | Out-Null
    }
}

function Build([string]$project, [string]$out) {
    Write-Host "== $project"
    dotnet build $project -c Release -o $out -v q -nologo
    if ($LASTEXITCODE) { throw "build failed: $project" }
}

# The client is published as a single file into build\client, then copied to the root.
Write-Host "== client\Nebo.Client"
$staging = Join-Path $root 'build\client'
dotnet publish (Join-Path $root 'client\Nebo.Client\Nebo.Client.csproj') -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded `
    -o $staging -v q -nologo
if ($LASTEXITCODE) { throw 'build failed: client' }
Copy-Item (Join-Path $staging 'Nebo.Client.exe') $client -Force

Build (Join-Path $root 'tools\DiagramIndexer\DiagramIndexer.csproj') (Join-Path $app 'tools\DiagramIndexer')

if (-not $SkipTests) {
    Write-Host "== tests"
    $filter = if ($NoDcs) { @('--filter', 'Needs!=DCS') } else { @() }
    dotnet test (Join-Path $root 'client\Nebo.Client.Core.Tests') -c Release -v q -nologo @filter
    if ($LASTEXITCODE) { throw 'tests failed' }
}

if (-not $SkipDll) {
    Write-Host "== nebo.dll"
    & (Join-Path $root 'server\dll\build.ps1') | Select-Object -Last 1
}

Write-Host "Client: $client"
if ($running.Count -gt 0) {
    Write-Host "Restarting the NEBO client"
    # Through Windows' process service, not Start-Process: started from a tool that runs in a
    # Windows job (Claude Code), the client would end when that tool restarts.
    [void](Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "`"$client`""; CurrentDirectory = $root })
}
