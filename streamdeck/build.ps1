# Builds the NEBO Stream Deck plugin and profile:
#   release\streamdeck\com.kestrelsnest.nebo.sdPlugin   (the plugin folder)
#   release\streamdeck\NEBO.streamDeckProfile          (XL layout; double-click to import)
# -Install copies the plugin into Stream Deck's plugins folder and restarts the Stream Deck app.
param([switch]$Install)
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$repo = Split-Path $here -Parent
$out = Join-Path $repo 'release\streamdeck'
$plugin = Join-Path $out 'com.kestrelsnest.nebo.sdPlugin'
Remove-Item $plugin -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $plugin | Out-Null
Copy-Item (Join-Path $here 'com.kestrelsnest.nebo.sdPlugin\*') $plugin -Recurse

# The plugin exe: self-contained single file, so players' PCs need no .NET install.
dotnet publish (Join-Path $here 'Nebo.StreamDeck\Nebo.StreamDeck.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:PublishTrimmed=true -o (Join-Path $plugin 'bin') --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'plugin build failed' }
Get-ChildItem (Join-Path $plugin 'bin') -Filter *.pdb | Remove-Item

python (Join-Path $here 'build_assets.py') $plugin (Join-Path $out "NEBO.streamDeckProfile")
if ($LASTEXITCODE -ne 0) { throw 'assets failed' }

if ($Install) {
    $target = Join-Path $env:APPDATA 'Elgato\StreamDeck\Plugins\com.kestrelsnest.nebo.sdPlugin'
    $sd = Get-Process StreamDeck -ErrorAction SilentlyContinue
    $exe = if ($sd) { $sd.Path } else { Join-Path $env:ProgramFiles 'Elgato\StreamDeck\StreamDeck.exe' }
    # Stream Deck locks the plugin's exe: stop it, copy, start it again (through Windows' process
    # service, so it outlives the shell that ran this).
    $sd | Stop-Process -Force
    Get-Process NeboDeck -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
    Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item $plugin $target -Recurse
    $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "`"$exe`" --runinbk"; CurrentDirectory = (Split-Path $exe) }
    Write-Host "Installed to $target; Stream Deck restarted (Win32_Process.Create: $($r.ReturnValue))"
}
Write-Host "Plugin: $plugin"
Write-Host "Profile: $(Join-Path $out "NEBO.streamDeckProfile")"
