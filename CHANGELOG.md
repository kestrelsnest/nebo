# Changelog

NEBO ATC/GCI by Kestrel's Nest Community, formerly Overseer and Eagle's Nest (code name DCB).
Newest first. Dates are commit dates. "DLL" is the server module (`nebo.dll`, before 0.16.0
`dcb.dll`), whose version goes up together with the protocol.

## 0.16.0 — unreleased · NEBO
**Name**
- Renamed from Overseer to **NEBO ATC/GCI by Kestrel's Nest Community** ("nebo" is "sky" in
  Ukrainian), everywhere, including the installed files:
  - server: hook `NEBO-hook.lua`, `Mods\Services\NEBO\bin\nebo.dll`, settings `Config\NEBO.lua`,
    data folder `NEBO\`;
  - client `Nebo.Client.exe`, settings in `%AppData%\NEBO` and caches/maps in `%LocalAppData%\NEBO`;
  - pilot hook `NEBO-DTC.lua`, export script `NEBO-Export.lua`, cartridges `NEBO_<jet>.dtc`;
  - Stream Deck plugin `com.kestrelsnest.nebo`, profile `NEBO.streamDeckProfile`;
  - packages `NEBO-*`.
- New logo and app icon; Help menu with the manual, Discord and About NEBO.
- **Moving over:** the server hook copies `Config\DCB.lua` to `NEBO.lua` and moves the `DCB` data
  folder on its first start, and warns in dcs.log while the old `DCB-hook.lua` is still installed
  (`server\deploy.ps1` removes it). The client moves `%AppData%\DCB` and `%LocalAppData%\DCB`
  (settings, layouts, captured maps) on its first start. Old pilot hooks keep working; the notes
  say how to replace them.
- Analytics files name the DLL version under `"nebo"` (was `"dcb"`).

**Server (DLL 0.16)**
- Camera login for the separate NEBO camera app: `camera_password` in `NEBO.lua`, a camera
  picture of both sides only while a camera is logged in, and camera focus messages
  (PROTOCOL.md, "Camera login").

## 0.15.2 — 2026-11-08
- **Launch marks:** an enemy ground unit firing at a side's unit gives that side's controllers a
  shared "SA-?" threat with an SA-8 ring. It starts about 1 km off and closes in with each launch
  from the same group, reaching the launcher at the 5th. It goes 15 minutes after the last launch.
- **Right-click the map → Select <airfield>:** the nearest airfield becomes the selected one, and
  the ATC tab and SRS list follow.
- The ATC and SRS airfield lists start with the own side's airfields only.
- **DTC map filter:** the open jet's plan, shown read-only on any map.
- **Fix:** a package member no longer turns a whole formation block its package colour.
- **Engagements:** right-click a row to remove it; the panel has a red and white striped border.

## 0.15.1 — 2026-11-03 · Overseer
**Name and licence**
- Renamed from Eagle's Nest to **Overseer by Kestrel's Nest Community**, including the installed
  files:
  - pilot hook `Overseer-DTC.lua` and export script `Overseer-Export.lua`;
  - cartridges `Overseer_<jet>.dtc` in the `Saved Games\DCS\Overseer\` folder;
  - Stream Deck plugin `com.kestrelsnest.overseer` with the Overseer page;
  - packages `Overseer-*`.
- The old pilot hook still works with Overseer servers.
- Free software under **GPL-3.0-only**, with section-7 permissions for AvalonDock (MS-PL) and for
  running with DCS World. 0.15.0 and earlier stay MIT. The server package is public.

**Server**
- Lighter: at most one heavy job per frame. Frames over 20 ms dropped from 38 to 9 (Edge of
  Levant) and from 18 to 7 (Iron Tempest).
- Idle picture rate: a side with no controller logged in gets a picture every 5 s
  (`idle_interval`). A controller who logs in gets a fresh one at once.
- DTC data costs about a third of before.
- F10 marks are rebuilt only when they change, and the LSO feed is asked once a second while no
  aircraft is near a carrier.
- A per-job timing line goes into the log every minute.

**Client**
- Map filters have four stages (off, 30 %, 60 %, 100 %): click steps through them, right click
  turns a filter on or off, and the wheel steps up or down. The Stream Deck filter keys cycle
  through the stages too.
- The DTC tables are made from the controller's own DCS install and no longer ship with the
  client. The server sends DCS's unit types at login. Nothing from Eagle Dynamics ships except
  the server's terrain height grids.
- The DTC is marked as work in progress.

## 0.15.0 — 2026-11-01
- **Pilot designations:**
  - The export script reads the F-16's TGP page (including the HAD's HTS ellipse) and the
    F/A-18C's ATFLIR coordinates.
  - The server keeps each designated HAD emitter for 10 minutes.
  - Controllers see the designations behind the TGP button.
- **Map labels:**
  - Flights in formation share one label.
  - Labels never cover symbols, vectors or other labels, and they can be dragged.
  - Short labels show the type; contacts near the mouse get full labels.
  - Players get a dot and a name plate; object tags come from the Objects list.
- **Engagements:**
  - A left click while dragging the ruler pins a BRAA line with a collision intercept.
  - The Engagements panel lists launches first, has an alert distance and sound, and markers
    1–8 with keys 1–8.
- **Settings window:** General, Keys, Alerts and SRS tabs. Also a slimmer SRS tab, the best radio
  spot for a player, and SRS status in the status bar.
- **Stream Deck:** a local link from the client and a plugin with an XL profile.

## 0.14.3 — 2026-10-29
- One pilot hook for several servers: `SERVERS = { ... }`. The hook finds the server the player
  is on and stays with it.

## 0.14.2 — 2026-10-29
- Launch alarm sound options: sound on or off, volume, 1–3 alarms per launch, and a test sound.
- Lighter Scope roads on Syria.

## 0.14.1 — 2026-10-29
- AEGIS ships (Ticonderoga, Arleigh Burke, Type 052) scan every 4 s. The A-50 and KJ-2000 are
  back in the scan table.
- **Scope base map:** terrain relief, rivers and main roads from the DCS install.
- **Landing queue:** right-click an own aircraft on the map to add it to a base's landing queue.

## 0.14.0 — 2026-10-29 · DTC
**DTC tab** (data cartridges pushed to pilots)
- Whole cartridges for the **F/A-18C, F-16C and F-14B(U)**. Defaults and limits come from DCS's
  own DTC code.
  - Hornet: waypoints, routes, NAV, COMM, GPS, SA, HARM, CMDS, RWR, TACAN.
  - Viper: steerpoints, DEST, THREAT, GEO lines, VIP/VRP, COMM, CMDS, RWR, ROE, COLR. The RWR
    and CMDS tables are compiled the way DCS does it.
  - Tomcat: flight plans 1–12, lines, points, JDAM targets with launch zones, CMDS, TIS.
- **Shared plan** ("Same points for every jet"): the route, points, lines and threats drawn for
  one jet are copied to the other two. Each jet keeps its own settings. Anything over a jet's
  limits is cut and listed.
- RWR, HARM and ROE emitters are marked friendly or hostile by the controller's side.
- **Push:** to pilots or packages by DCS player id, so dynamic slots work. Each pilot gets the
  cartridge for the jet they fly; a pilot still in the spawn dialog gets all three.
  - DLL 0.14 keeps one pushed cartridge per player and aircraft type (0.12: pilot sync; 0.13:
    pushed cartridges).
  - The pilot hook is its own package (`Pilot` zip). It polls without stalling the game.
- Fix: crash on start when the DTC tab was restored.

**GCI**
- Missiles show Mach.
- **Launch alarm:** an enemy missile at a player sounds an alarm, rings the target and shows a
  banner. Space jumps to the newest launch.

**Radar scan model**
- One plot per sweep.
- Radar phases are spread evenly.
- Aircraft radars use their own limits from DCS's sensor data.
- The F-5E-3 no longer counts as an E-3.

Also: airfield chart sheets, and Marianas terrain data in the server package.

## 0.11.0 — 2026-10-28
- **RWR picture removed** (server and client). In its place, the RWR ring: a ring around the own
  aircraft with 12/3/6/9 o'clock buttons and free azimuth lines.
- Data blocks show Mach.
- Before removal, RWR hearing ranges were reworked and full-fidelity jets made receivers.

## 0.10.0 — 2026-10-26 · Login 2
- **Login:**
  - Optional Discord role check, re-checked every 5 minutes.
  - The server password is proven, never sent.
  - The controller picks a side.
  - `DCB.lua` takes one `password`; the old per-side passwords still work.
- The connect dialog keeps a list of saved servers. Passwords are encrypted for the Windows user.
- SRS is put into External AWACS Mode automatically.
- A `DCB.lua` that fails to load is reported and never overwritten.

## 0.9.0 — 2026-10-25
- **Radar scan model** (`radar_scan`): each radar paints on its own clock, and a contact shows
  its latest plot with its age.
- **Controllers don't fly** (`allow_flying_controllers = false`).
- Detachable GCI and ATC tabs, and key and mouse bindings for every action.
- Parallel runway names (L/C/R) and terrain-aware SRS transmit coverage.

## 0.8.1 — 2026-10-24
- GCI windows lose the ATC tab and gain an SRS position list (airfields, helipads, own carriers
  and AWACS; carriers and AWACS are followed as they move).

## 0.8.0 — 2026-10-24 · Identification
- Manual identification (`identification = "manual"`) and an Identify menu.
- **NCTR** on request: JEM, HRRP, ISAR, size, rotor and raid, each with range, aspect and dwell
  rules.
- Placed threats are shared among a side's controllers.
- Caucasus maps (georeference, taxi networks, satellite textures).

## 0.7.0 — 2026-10-22 · Carriers
- Set a carrier's lights (off, auto, navigation, launch, recovery).
- Case I/II/III follows DCS (Case III after sunset). The Carrier view draws the guidance lines.

## First commit — 2026-10-22 · Eagle's Nest
- **Server:** `dcb.dll`, GameGUI hook and mission collector. They provide fog-of-war pictures,
  missiles, carriers and the LSO feed, airfields, weather, Foothold, F10 marks and analytics.
- **Client:** WPF with a GPU map (scope, DCS map, charts, satellite) and GCI and ATC workspaces.
  It also has airfield diagrams, approach, carrier and LSO views, an SRS bridge, packages, ATIS
  and threat rings.
- Finds the DCS install automatically. The server installs by copying one folder, with random
  passwords on first start.
- MIT licence, front page and operations manual.
