# Development

How to build NEBO, install a build on a DCS server, and check the client. Wire format:
[PROTOCOL.md](PROTOCOL.md).

## Layout

| Folder | What |
|---|---|
| `client/` | C# .NET 10: `Nebo.Client.Core` (protocol, maths, file formats; testable), `Nebo.Client` (WPF app), `Nebo.Client.Core.Tests` |
| `server/dll/` | C++ server DLL `nebo.dll`: Lua 5.1 module + TCP server (`build.ps1`, CMake, MSVC); `test/` runs it outside DCS |
| `server/lua/` | `NEBO-hook.lua` (DCS GameGUI hook), `collector.lua` (mission env), `foothold.lua` (Foothold exporter) |
| `server/` | `deploy.ps1` (build and install onto a DCS server instance), `package.ps1` (release zips and the setup), `setup/` (`NEBO.iss`, the Inno Setup wizard, its images and option pictures), `config/NEBO.example.lua`, `release/` (the notes inside the zips) |
| `pilot/` | `NEBO-DTC.lua` (pilots' cartridge hook), `NEBO-Export.lua` (TGP / ATFLIR designations), `test/` (offline tests) |
| `streamdeck/` | Stream Deck plugin (`Nebo.StreamDeck`), its manifest, icons and build |
| `tools/` | `DtcDefs` (DTC definitions embedded in Core), `DiagramIndexer` (airport-diagram pictures), `MapCapture` (captured satellite maps) |
| `style/` | `map-style.json`: colours, widths and fonts of the maps |
| `branding/` | logos, the app icon, `make-icons.js` (icons and wizard images) and `make-options.js` (the setup's option pictures) |
| `docs/` | manual, radio calls, protocol, design notes, file formats |

## Requirements

- Windows, **.NET 10 SDK**, **Visual Studio 2022** (any edition with the C++ workload, or the
  Build Tools) for the DLL; its bundled CMake is used unless one is on PATH.
- A DCS World install for a server instance and for the DCS base maps.
- Python 3 for the Stream Deck assets, the DLL tests and the packages; Node only to re-render icons.
- **Inno Setup 6** for the setup (`winget install JRSoftware.InnoSetup`); without it
  `package.ps1` makes the zips only.

## Build

`.\build.ps1` builds everything in one go (one build type) and runs the tests:

- `Nebo.Client.exe` (project root): the client, a single file (needs the .NET 10 Desktop Runtime)
- `app\tools\DiagramIndexer\DiagramIndexer.exe`: places airport-diagram pictures, reads their names
- `server\dll\build\Release\nebo.dll`: the server DLL

A running client is closed (it saves its layout), rebuilt and started again.

Release packages: `server\package.ps1` → `release\NEBO-Server-<version>.zip` (copy-only
install; the hook makes `Config\NEBO.lua` with random passwords on the first start; the 500 m
terrain grids sampled on a server are included), `NEBO-Client-`, `NEBO-Pilot-` and
`NEBO-StreamDeck-<version>.zip`, then `NEBO-Setup-<version>.exe` from those packages
(`ISCC.exe /DVersion=<version> /DSrc=<release folder> server\setup\NEBO.iss`; run ISCC from
PowerShell or cmd, Git Bash mangles its `/D` switches). The version is `kVersion` in
`server/dll/src/nebo.cpp`; the client's `<Version>` must match. The client's new-version bar
reads the latest GitHub release of kestrelsnest/nebo and downloads its `NEBO-Setup-*.exe`
asset, so each release needs the setup attached.

The setup's pages are Pascal Script in `NEBO.iss`. Test it on fake Saved Games folders, never
real ones: `/VERYSILENT /COMPONENTS=pilot,server /DIR=<temp> /PILOTDIRS=<fake> /SERVERDIRS=<fake>
/LOG=<file>`, then check the files and run `unins000.exe /VERYSILENT`. The workbench's
`tools\Screenshots\shoot_setup.ps1` captures every page for the manual.

## A server to test on

`server\deploy.ps1 [-InstanceName DCS.server] [-NoStart]` builds the DLL and installs hook, DLL
and scripts into `Saved Games\<instance>`. A running instance is stopped first (DCS locks the
DLL) and started again with its own command line. The first start writes
`Config\NEBO.lua` with random passwords (git-ignored, never committed).

A headless DCS server instance: `DCS.exe --server --norender -w <instance>` from DCS's `bin`
folder; its web GUI is at http://127.0.0.1:8088.

**Dev probe** (`dev_probe = true` in a test server's `NEBO.lua`, never on a live server): the
hook runs `Saved Games\<instance>\NEBO\probe.lua` once (mission env, or the hook's own env with a
first line `-- env: hook`), writes the result to `probe.out` and deletes the probe. Write probes
without a BOM.

## Tests

- Core: `dotnet test client\Nebo.Client.Core.Tests` (run by `build.ps1`).
- DLL outside DCS: `python -I server\dll\test\<name>_test.py server\dll\build\Release` (camera,
  camera_collector, clients, push; `NEBO_TEST_PASSWORD` sets the test password).
- Pilot scripts outside DCS, from the DCS install folder with its `bin\luae.exe`:
  `pilot\test\export_offline.lua`, `pilot\test\hook_offline.lua <cartridge .dtc>`.

## Snapshot mode (checking the UI without a person)

```
Nebo.Client.exe --snapshot <out dir> 127.0.0.1 10309 <password> [seconds] [mode] [lat,lon,res[,coast|grid|dimN|zone=<name>]] [test]
```

- `mode`: a base map name (opens it floated), `restore`, `atc:<airfield>`, `approach:<airfield>[+q]`,
  `carrier:<name>[@<NM>][+q]`, `lso:<name>`, `views:<airfield>`, `packages`, `srstab[:<SRS user>]`,
  `perf`, `dcsfolder` (the DCS location dialog).
- `test` (after a base map and view): `zoomtest`, `pantest[:<layers>]`, `wheeltest`, `settletest`,
  `switchtest`, `measure`, `point`, `gci` / `gci+send` (triangulation board; `+send` makes a real F10 mark).
- Writes PNGs of every window and `status.txt` (connection, timings, memory, the test's report).
- Use `NEBO_HOME=<empty folder>` for a clean profile. `NEBO_SNAPSHOT_EAM` gives `srstab` an EAM
  password; `NEBO_SNAPSHOT_SRSAPP=1` lets it read the SRS app's port 7082. **Snapshots never send
  commands to the SRS app.**

## Client internals worth knowing

- Settings and layouts: `%AppData%\NEBO` (override with `NEBO_HOME`); caches and DCS Map tiles:
  `%LocalAppData%\NEBO` (`cache`, `tiles`); render errors: `%LocalAppData%\NEBO\render-errors.log`.
  0.15 folders named `DCB` are moved on the first start.
- Saved passwords are encrypted for the Windows user (DPAPI). The entropy labels still read
  "DCB …" so 0.15's saved passwords open: never change them.
- Maps are drawn by Skia on the GPU (OpenGL), with a CPU fallback; `NEBO_RENDERER=cpu` forces it.
- DCS install: `DcsInstall.Find()` (client) — `Settings.DcsPath`, else the first install found by
  `DcsLocator` (ED registry entries, Steam libraries, usual folders).
- DCS Map: tiles at 2^L m/px; levels ≥ 32 m/px pre-rendered in the background, finer on view;
  keyed by renderer version, terrain grid and the DCS files read, so a DCS update starts fresh.
- Airport diagrams from third-party charts (optional, local only):
  `DiagramIndexer.exe index <folder of page PNGs> 127.0.0.1 10309 <password>` writes
  `diagrams.json`; set the folder under *Settings → General → Airfield diagrams for this theatre…*. `DiagramIndexer.exe probe <page.png>`
  prints what is read from one page.
- Captured satellite maps (optional, local only): `tools\MapCapture` drives a DCS instance to
  capture a terrain's satellite view into one `.ensat` file per terrain, which the client reads.
  The imagery is ED's and never ships.
- The Scope base map `client\Nebo.Client\Assets\basemap.json` is made from Natural Earth data
  (public domain) by a separate tool.

## Server internals worth knowing

- The hook (GameGUI env) calls the collector in the mission env once per `interval` (1 s) and
  publishes one picture per coalition through the DLL; other feeds (theatre, terrain, airfields,
  weather, Foothold, F10 marks, LSO) have their own channels and rates.
- At most one heavy job runs per frame; costs are logged once a minute in `dcs.log` ("collect:"
  and "timing:" lines).
- Foothold's state lives in the trigger scripting env, not `net.dostring_in('server')`; the
  exporter is injected there (`foothold.lua`).
- Analytics and website export: [ANALYTICS.md](ANALYTICS.md).
