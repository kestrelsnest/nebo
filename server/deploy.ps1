# Builds nebo.dll and installs NEBO into a DCS server instance. DCS locks the DLL while
# running, so a running instance is stopped first (only that instance - matched by its -w
# argument) and started again afterwards with its own command line, unless -NoStart.
param(
    [string]$InstanceName = 'DCS.server',
    [switch]$NoStart
)
$ErrorActionPreference = 'Stop'

$instance = Join-Path "$env:USERPROFILE\Saved Games" $InstanceName
$dll      = & (Join-Path $PSScriptRoot 'dll\build.ps1') | Select-Object -Last 1
$lua      = Join-Path $PSScriptRoot 'lua'

$procs = @(Get-CimInstance Win32_Process -Filter "Name = 'DCS.exe'" |
    Where-Object { $_.CommandLine -match "-w\s+`"?$([regex]::Escape($InstanceName))`"?(\s|$)" })
foreach ($p in $procs) {
    Write-Host "Stopping $InstanceName (pid $($p.ProcessId))"
    Stop-Process -Id $p.ProcessId -Force
    Wait-Process -Id $p.ProcessId -Timeout 60 -ErrorAction SilentlyContinue
}

# 0.15.x installed as DCB: its hook and DLL would run next to NEBO's on the same port. The
# settings (Config\DCB.lua) and data folder (DCB\) are taken over by the NEBO hook on its first start.
foreach ($old in 'Scripts\Hooks\DCB-hook.lua', 'Mods\Services\DCB') {
    $p = Join-Path $instance $old
    if (Test-Path $p) { Remove-Item $p -Recurse -Force; Write-Host "Removed the old $old" }
}

$svc = Join-Path $instance 'Mods\Services\NEBO'
New-Item -ItemType Directory -Force (Join-Path $svc 'bin'), (Join-Path $svc 'Scripts'), (Join-Path $instance 'Scripts\Hooks') | Out-Null
# Windows can hold the DLL lock for a moment after the process is gone.
$target = Join-Path $svc 'bin\nebo.dll'
for ($i = 0; ; $i++) {
    try { Copy-Item $dll $target -Force; break }
    catch { if ($i -ge 30) { throw }; Start-Sleep -Seconds 1 }
}
Copy-Item (Join-Path $lua 'collector.lua'), (Join-Path $lua 'foothold.lua') (Join-Path $svc 'Scripts') -Force
Copy-Item (Join-Path $lua 'NEBO-hook.lua') (Join-Path $instance 'Scripts\Hooks') -Force
$cfg = Join-Path $instance 'Config\NEBO.lua'
# The example goes next to it; on a fresh instance the hook makes NEBO.lua from it with random
# passwords on the first start (as a release install does).
Copy-Item (Join-Path $PSScriptRoot 'config\NEBO.example.lua') (Join-Path $instance 'Config\NEBO.example.lua') -Force
if (-not (Test-Path $cfg)) { Write-Host "First start creates $cfg with random controller passwords" }
Write-Host "Installed NEBO into $instance"

if ($NoStart) { return }
if ($procs.Count -eq 0) { Write-Host "$InstanceName was not running: start it as usual."; return }
foreach ($p in $procs) {
    # Through Windows' process service, not Start-Process: a process started from a tool that
    # runs in a Windows job (Claude Code) would end when that tool restarts.
    $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
        CommandLine = $p.CommandLine; CurrentDirectory = (Split-Path $p.ExecutablePath) }
    if ($r.ReturnValue -ne 0) { throw "DCS did not start (Win32_Process.Create returned $($r.ReturnValue))" }
}
Write-Host "Started again. Log: $instance\Logs\dcs.log"
