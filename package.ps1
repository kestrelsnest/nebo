# Builds the release packages into release\:
#   NEBO-Client-<version>.zip     - Nebo.Client.exe + CLIENT.txt
#   NEBO-Pilot-<version>.zip      - pilot\NEBO-DTC.lua (the pilots' DTC hook),
#                                   pilot\NEBO-Export.lua (F-16 designations) + PILOT.txt
#   NEBO-StreamDeck-<version>.zip - the Stream Deck plugin (.streamDeckPlugin), the XL profile
#                                   and STREAMDECK.txt (built by streamdeck\build.ps1)
#   NEBO-Setup-<version>.exe      - one wizard for the three parts (setup\NEBO.iss, Inno Setup 6);
#                                   the zips stay for manual installs
# The DCS server part is not in this repository: it is handed to server admins separately.
# The version is the client's <Version>; the Stream Deck plugin's must match.
# The client is the one build.ps1 last built (repo root); run build.ps1 first after client changes.
$ErrorActionPreference = 'Stop'

$repo = $PSScriptRoot
$notes = Join-Path $repo 'setup\notes'
$version = [regex]::Match((Get-Content (Join-Path $repo 'client\Nebo.Client\Nebo.Client.csproj') -Raw), '<Version>([^<]+)</Version>').Groups[1].Value
if (-not $version) { throw 'version not found in Nebo.Client.csproj' }
$v = [regex]::Match((Get-Content (Join-Path $repo 'streamdeck\Nebo.StreamDeck\Nebo.StreamDeck.csproj') -Raw), '<Version>([^<]+)</Version>').Groups[1].Value
if ($v -ne $version) { throw "Nebo.StreamDeck.csproj has version '$v', the client has $version" }

$release = Join-Path $repo 'release'
$client = Join-Path $release "NEBO-Client-$version"
Remove-Item $client -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $client | Out-Null
$exe = Join-Path $repo 'Nebo.Client.exe'
if (-not (Test-Path $exe)) { throw 'Nebo.Client.exe missing: run build.ps1 first' }
Copy-Item $exe $client
Copy-Item (Join-Path $notes 'CLIENT.txt') (Join-Path $client 'CLIENT.txt')

# Pilot sync: the hook pilots install (the server's address set by them or the server admin).
$pilot = Join-Path $release "NEBO-Pilot-$version"
Remove-Item $pilot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $pilot | Out-Null
Copy-Item (Join-Path $repo 'pilot\NEBO-DTC.lua') $pilot
Copy-Item (Join-Path $repo 'pilot\NEBO-Export.lua') $pilot
(Get-Content (Join-Path $notes 'PILOT.txt') -Raw).Replace('__VERSION__', $version) |
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
(Get-Content (Join-Path $notes 'STREAMDECK.txt') -Raw).Replace('__VERSION__', $version) |
    Set-Content (Join-Path $deck 'STREAMDECK.txt') -Encoding utf8 -NoNewline

foreach ($dir in $client, $pilot, $deck) {
    # GPL: every package carries the license, its additional permissions and the notices.
    foreach ($f in 'LICENSE', 'LICENSE-EXCEPTION.md', 'THIRD-PARTY-NOTICES.md') { Copy-Item (Join-Path $repo $f) $dir }
    $zip = "$dir.zip"
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $dir '*') -DestinationPath $zip
    Write-Host "$zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
}

# The setup wizard, made from the three package folders above.
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found: winget install JRSoftware.InnoSetup' }
& $iscc /Q "/DVersion=$version" "/DSrc=$release" (Join-Path $repo 'setup\NEBO.iss')
if ($LASTEXITCODE) { throw 'setup build failed' }
$setup = Join-Path $release "NEBO-Setup-$version.exe"
Write-Host "$setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
Write-Host "Client built $((Get-Item $exe).LastWriteTime)"
