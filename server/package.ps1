# Builds the release packages into release\:
#   NEBO-Server-<version>.zip  - copy-into-saved-games\ (Mods, Scripts, Config\NEBO.example.lua,
#                                      NEBO\terrain_*_500.bin) + INSTALL.txt
#   NEBO-Client-<version>.zip  - Nebo.Client.exe + CLIENT.txt
#   NEBO-Pilot-<version>.zip   - pilot\NEBO-DTC.lua (the pilots' DTC hook),
#                                      pilot\NEBO-Export.lua (F-16 designations) + PILOT.txt
#   NEBO-StreamDeck-<version>.zip - the Stream Deck plugin (.streamDeckPlugin), the XL profile
#                                      and STREAMDECK.txt (built by streamdeck\build.ps1)
#   NEBO-Setup-<version>.exe   - one wizard for all four parts (server\setup\NEBO.iss, Inno Setup 6);
#                                      the zips stay for manual installs
# The client is the one build.ps1 last built (repo root); run build.ps1 first after client changes.
# Terrain grids come from the test server's NEBO folder (-TerrainFrom): every terrain sampled there at
# the default 500 m step ships, so a server on that terrain samples nothing.
param([string]$TerrainFrom = (Join-Path $env:USERPROFILE 'Saved Games\DCS.server\NEBO'))
$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
$dll = & (Join-Path $PSScriptRoot 'dll\build.ps1') | Select-Object -Last 1
$version = [regex]::Match((Get-Content (Join-Path $PSScriptRoot 'dll\src\nebo.cpp') -Raw), 'kVersion\[\] = "([^"]+)"').Groups[1].Value
if (-not $version) { throw 'version not found in nebo.cpp' }

$release = Join-Path $repo 'release'
$server = Join-Path $release "NEBO-Server-$version"
$paste = Join-Path $server 'copy-into-saved-games'
Remove-Item $server -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $paste 'Mods\Services\NEBO\bin'), (Join-Path $paste 'Mods\Services\NEBO\Scripts'),
    (Join-Path $paste 'Scripts\Hooks'), (Join-Path $paste 'Config') | Out-Null
Copy-Item $dll (Join-Path $paste 'Mods\Services\NEBO\bin\nebo.dll')
Copy-Item (Join-Path $PSScriptRoot 'lua\collector.lua') (Join-Path $paste 'Mods\Services\NEBO\Scripts\')
Copy-Item (Join-Path $PSScriptRoot 'lua\foothold.lua') (Join-Path $paste 'Mods\Services\NEBO\Scripts\')
Copy-Item (Join-Path $PSScriptRoot 'lua\NEBO-hook.lua') (Join-Path $paste 'Scripts\Hooks\')
# Settings ship as an example only: the hook makes NEBO.lua from it on the first start (random
# passwords), and an update never overwrites the server's own NEBO.lua.
Copy-Item (Join-Path $PSScriptRoot 'config\NEBO.example.lua') (Join-Path $paste 'Config\NEBO.example.lua')
(Get-Content (Join-Path $PSScriptRoot 'release\INSTALL.txt') -Raw).Replace('__VERSION__', $version) |
    Set-Content (Join-Path $server 'INSTALL.txt') -Encoding utf8 -NoNewline
$grids = @(Get-ChildItem (Join-Path $TerrainFrom 'terrain_*_500.bin') -ErrorAction SilentlyContinue)
if ($grids.Count -gt 0) {
    New-Item -ItemType Directory -Force (Join-Path $paste 'NEBO') | Out-Null
    $grids | Copy-Item -Destination (Join-Path $paste 'NEBO')
}
Write-Host "Terrain grids: $(if ($grids.Count) { ($grids | % { $_.Name }) -join ', ' } else { 'none' })"

$client = Join-Path $release "NEBO-Client-$version"
Remove-Item $client -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $client | Out-Null
$exe = Join-Path $repo 'Nebo.Client.exe'
if (-not (Test-Path $exe)) { throw 'Nebo.Client.exe missing: run build.ps1 first' }
Copy-Item $exe $client
Copy-Item (Join-Path $PSScriptRoot 'release\CLIENT.txt') (Join-Path $client 'CLIENT.txt')

# Pilot sync: the hook pilots install (the server's address set by them or the server admin).
$pilot = Join-Path $release "NEBO-Pilot-$version"
Remove-Item $pilot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $pilot | Out-Null
Copy-Item (Join-Path $repo 'pilot\NEBO-DTC.lua') $pilot
Copy-Item (Join-Path $repo 'pilot\NEBO-Export.lua') $pilot
(Get-Content (Join-Path $PSScriptRoot 'release\PILOT.txt') -Raw).Replace('__VERSION__', $version) |
    Set-Content (Join-Path $pilot 'PILOT.txt') -Encoding utf8 -NoNewline

# Stream Deck: the plugin (double-click .streamDeckPlugin to install) and the XL profile.
$deck = Join-Path $release "NEBO-StreamDeck-$version"
Remove-Item $deck -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $deck | Out-Null
& (Join-Path $repo 'streamdeck\build.ps1')
$built = Join-Path $release 'streamdeck'
# A .streamDeckPlugin is a zip holding the plugin folder; written by Python, whose zip paths use
# forward slashes (PowerShell 5's Compress-Archive writes backslashes, which installers may refuse).
python -I -c "import pathlib, sys, zipfile; src = pathlib.Path(sys.argv[1]); z = zipfile.ZipFile(sys.argv[2], 'w', zipfile.ZIP_DEFLATED); [z.write(p, p.relative_to(src.parent).as_posix()) for p in sorted(src.rglob('*')) if p.is_file()]; z.close()" `
    (Join-Path $built 'com.kestrelsnest.nebo.sdPlugin') (Join-Path $deck 'com.kestrelsnest.nebo.streamDeckPlugin')
if ($LASTEXITCODE -ne 0) { throw 'streamDeckPlugin failed' }
Copy-Item (Join-Path $built "NEBO.streamDeckProfile") $deck
(Get-Content (Join-Path $PSScriptRoot 'release\STREAMDECK.txt') -Raw).Replace('__VERSION__', $version) |
    Set-Content (Join-Path $deck 'STREAMDECK.txt') -Encoding utf8 -NoNewline

foreach ($dir in $server, $client, $pilot, $deck) {
    # GPL: every package carries the license, its additional permissions and the notices.
    foreach ($f in 'LICENSE', 'LICENSE-EXCEPTION.md', 'THIRD-PARTY-NOTICES.md') { Copy-Item (Join-Path $repo $f) $dir }
    $zip = "$dir.zip"
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $dir '*') -DestinationPath $zip
    Write-Host "$zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
}

# The setup wizard, made from the four package folders above.
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found: winget install JRSoftware.InnoSetup' }
& $iscc /Q "/DVersion=$version" "/DSrc=$release" (Join-Path $PSScriptRoot 'setup\NEBO.iss')
if ($LASTEXITCODE) { throw 'setup build failed' }
$setup = Join-Path $release "NEBO-Setup-$version.exe"
Write-Host "$setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
Write-Host "Client built $((Get-Item $exe).LastWriteTime)"
