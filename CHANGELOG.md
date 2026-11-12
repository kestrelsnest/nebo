# Changelog

NEBO ATC/GCI by Kestrel's Nest Community, formerly Overseer and Eagle's Nest (code name DCB).
Newest first. Dates are commit dates. "DLL" is the server module (`nebo.dll`, before 0.16.0
`dcb.dll`), whose version goes up together with the protocol.

## Unreleased
**Fixed**
- Foothold zones never showed when Foothold started after NEBO's first look, for example on a
  server paused until the first player joins, or a mission loading Foothold from disk a few
  seconds in: NEBO gave up after one check. It now checks every 30 s until Foothold is there
  (server: `Scripts\Hooks\NEBO-hook.lua`).

## 0.17.0 — 2026-11-12
**Controllers and pilots**
- A **callsign** in the Connect dialog (required, per server). On a 0.17 server the side's
  pilots see "NEBO: Overlord (GCI) on station, 251.000" when a controller logs in and "off
  station" when they leave (`announce_controllers` in NEBO.lua).
- **Messages** in the bottom-right corner of every map, like a messenger: controllers on and off
  station (with a sound), new pilots, handoffs, take-offs, results; buttons on the ones that need
  you (Take, Accept / Decline, Hand off / Keep).
  - **Take** a new pilot: an ATC channel (CH4–CH6) goes to his tower, you transmit there, and
    he gets an on-screen text with your callsign and the frequency.
  - After take-off: **Keep** (back to your channel, he is told to contact you there).
  - **Hand off** a pilot or a whole package to another controller, who accepts or declines;
    the pilot blinks on the maps until they answer.
- **Objects**: who has each player (**Ctl**, in that controller's colour) and his flight
  (airfield, took off from, landed at); right-click for Take, Hand off, Release (maps too).
- A **colour** per controller (Connect dialog): their pilots are drawn in it on every
  controller's map.
- **Areas of operations**: each controller draws up to 10 on the map (Area button, at the front
  of the filter row); every controller of the side sees them. A new pilot in one of your areas
  alerts you first. Right-click inside one to remove it, or to show it to your side's pilots on
  their F10 map (*Show this area to pilots*).
- **Owners: all / mine / off** map button: everyone's areas and owner colours, only yours, or none.
- **Entering another controller's area**: your airborne pilot flying into it brings a one-click
  hand-off to them, once per entry.
- **Foothold missions on the map**: the missions under a zone's name on your side's F10 map, as
  badges (RWY · SEAD) or, zoomed in close, the full lines ("Bomb runway!").
- **Map frame rate** (Settings → General): draw on change (default), at full frame rate while
  the mouse is on a map, or always.
- A **slow-frame log** (`%LocalAppData%\NEBO\slow-frames.log`): slow frames, missed frames during
  a zoom, and wheel notches or drags that reach the screen late, with the slowest map layers.
- **Chat panel**: the server's all chat and side chat, the mission's announcements, players
  joining and leaving, and controllers' texts to pilots; send an on-screen text to your side or to
  everyone. Joins and chat that mention you or GCI/ATC show in the tray.

**Tables**
- Every table: resize columns by dragging a header's edge (the dark theme's headers had lost
  their grips), move them by dragging a header, hide and show them from a right-click on a
  header. Each table remembers its columns.
- SRS channel plan: CH4–CH6 are ATC channels (were CH6 only); CH1–3 free.

**Fixed**
- Zooming stopped, then jumped: during a smooth zoom the map asked for frames ahead of the
  mouse, so wheel notches reached NEBO up to a second late and all at once. Frames now wait for
  input, and the map draws up to the screen's refresh rate (was about 80 fps).
- Rivers and roads on the Scope and DCS Map base maps are joined into long lines and simplified
  for the zoom level, still smooth (GPU time per frame from 14–30 ms to about 2–3 ms); the
  simplified lines are built in the background when a map opens, so the first zoom no longer
  stalls. The GPU cache is larger.
- Foothold's zone labels showed as F10 mark pins around the zones: they sit exactly on the zone
  edge, which the filter missed.
- Areas drawn on the F10 map by scripts (and controllers' areas for pilots) no longer show as
  mark pins; the server leaves them out of the marks too.

**Server (DLL 0.17)**
- New commands `controller_info` (with `color`), `area_set` (with `f10`), `claim`, `release`,
  `handoff`, `handoff_answer`, `message_side`; new coalition messages `controllers`, `flights`
  and `chat` (docs/PROTOCOL.md). Older clients ignore them.
- The hook reads players' chat and the mission's on-screen texts for a side or all, and draws
  areas flagged for pilots on their F10 map. `relay_chat = false` in NEBO.lua keeps chat out.

## 0.16.0 — 2026-11-11 · NEBO
**One setup**
- `NEBO-Setup-0.16.0.exe` installs every part for the Windows user: controller app, Stream Deck
  plugin, pilot hook, DCS server hook. It finds the DCS Saved Games folders, replaces Overseer /
  Eagle's Nest / DCB files, keeps the pilots' server lists, and for a server asks the port
  (optionally opened in the firewall) and five settings with pictures of the difference
  (enemy ground units, identification, radar scan, flying controllers, data cartridges),
  written to `NEBO.lua` line by line. Running it again updates; uninstalling removes only what
  it copied. Silent install for admins (INSTALL.txt). Not code-signed yet.
- The client tells you when a new version is out (asks GitHub once a day; *Settings → General
  → Updates*) and downloads and runs its setup on a click.
- The zips stay for copying by hand.

**Fixed**
- The DTC tab's "Fill from mission" and threats "From picture" had no data since 0.14.0: the
  server sent the coalition's live DTC data under another message type. Fixed on the server, so
  0.14 and 0.15 clients get it too.

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
