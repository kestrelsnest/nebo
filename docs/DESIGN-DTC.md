# DTC tab — design (2026-10-29)

User's request: a third workspace tab **DTC** in the client. The controller builds a data
cartridge like DCS's own DTC manager ("select anything and place waypoints, altitudes and other
things") and **pushes** it to pilots; the pilot hook no longer writes one on every slot change.

## User's choices
- Scope of the first build: **every F/A-18C section**, plus a **departure airfield** that fills
  TACAN (and ICLS for a carrier) and waypoint 1 by itself.
- Recipients: **one or more pilots, or a whole package (P1–P4)**.
- A push **replaces the whole cartridge**.
- The pilot gets an **in-game message** when one arrives.

## Facts that shape it
- DCS loads a cartridge only before spawn (briefing → DTC). A push to a flying pilot is ready for
  their next spawn; nothing changes the jet in the air.
- DCS's DTC manager is a hand-made dialog per aircraft and section (`CoreMods\aircraft\FA-18C\DTC`:
  `MAIN_panels` WYPT, RTE_SEQ, GPS WYPT, TACAN, COMM, SA, HARM, ALR67). Its defaults are pure
  data: each `initialize_<section>` copies a `_defs` table (CMDS, RWR, COMM1/2, NAV_SETTINGS,
  SA_SETTINGS, HARM_SETTINGS, TOO_CLASS defaults).
- Hornet cartridge sizes: ALR67 222 KB (RWR tables of every emitter), HARM 13 KB, the rest a few
  KB. A whole-cartridge push is ~230 KB.
- A pilot's PC cannot take incoming connections (home routers): the hook has to ask.

## Parts
1. **`tools/DtcDefs/hornet.lua`** (DCS's `bin\luae.exe`, run from the DCS folder): runs the
   `_defs` files and DCS's own default functions → `FA-18C_hornet.default.json`
   (a complete default cartridge) and `FA-18C_hornet.defs.json` (limits; MEZ threat types with
   ring ranges and levels; CMDS program names and fields; RWR emitter list; HARM classes; TACAN
   modes). Nothing hand-made. **Since 0.15.1 these tables are made on the controller's PC**
   (`Dtc/DtcData.cs`): the four scripts are built into the client, which runs them with the
   controller's own `bin\luae.exe` on first use (~0.2 s) and keeps the result per DCS version in
   `%AppData%\NEBO\dtc-defs\<version>-g<generator>`. They are Eagle Dynamics' data, so they are
   not in the repository or the client; without DCS the DTC tab says so. Threats the threat
   base names only by wsType get their unit types from the server's `unit_types` message
   (PROTOCOL.md), which replaced `threat_units.json`. The tests make the tables the same way
   (`DtcTestData.cs`, DCS_PATH or the usual folders).
2. **Client model** (`Nebo.Client.Core/Dtc`): a cartridge is kept as a JSON tree (every key DCS
   wrote survives, even ones we do not edit) with typed views per section: NAV points (lat/lon ↔
   DCS x/y through the theatre, altitude ft MSL/AGL, note), the 3 routes (point order, speed,
   ETA/fix time, target flag), NAV settings, GPS waypoints, TACAN stations, COMM1/2 presets,
   SA (threat rings, CAP points, corridors, FAOR/FLOT), HARM settings + TOO classes, CMDS
   programs, RWR settings. Validation from the defs limits (59 waypoints, 25,000 ft baro…).
   Open/save `.dtc` files; drafts in `%AppData%\NEBO\dtc`.
3. **DTC workspace tab**: a map (the GCI map with a DTC layer) and a section panel with DCS's
   section tabs. On the map: click to add a waypoint, drag to move, right-click for altitude /
   delete / insert into a route; threat rings from the picture's identified threats or placed by
   hand; CAP points, corridors, FAOR/FLOT drawn as on the Hornet's SA page. **Departure
   airfield** picker (airfields and own carriers): TACAN channel/band and mode from the field or
   ship, ICLS for a carrier, waypoint 1 at the field. Recipient list: own pilots in a Hornet,
   and packages P1–P4. **Push**.
4. **Server (DLL)**: command `dtc_push {recipients, cartridge}` from a logged-in controller
   (its own size limit, 1 MB); the server keeps the latest cartridge per recipient (player name +
   address, from the slot map), with a version number and the controller's name, for the mission.
   `dtc_request` from the pilot hook carries the version it has; the answer is the new cartridge
   or "nothing new". The server sends the in-game message to the pilot's unit on push.
5. **Pilot hook**: while in a slot, asks every 10 s (tiny request); writes
   `DTC\NEBO_<type>.dtc` only when a newer push is there, whole (no merge), and logs it.
   The old "live data on every slot change" goes.

## Order of work (each step tested before the next)
1. **Done 2026-10-29.** `tools/DtcDefs/hornet.lua` (loads `Scripts/Database/wsTypes.lua` first:
   the threat base's wsTypes need DCS's `wsType_*` constants) → `client/Nebo.Client.Core/Dtc/
   FA-18C_hornet.{default,defs}.json`, embedded. Checked against the user's DCS-saved cartridge:
   every key present, all 190 compiled threat entries' wstypes identical; the only extras are
   3 emitters DCS added since (PGZ-09, Huangfeng, SA-5 Tall King). `Dtc/Cartridge.cs` (JSON
   tree, default, load/save) + `DtcDefs`; 6 tests; all 8 cartridges in the user's DTC folder
   (6 aircraft types) identical after load + save. Note: DCS's CMDS/RWR code loads the
   **F-16C's** threat_base, HARM the Hornet's — kept as DCS does.
2. Server push/pull + hook polling, tested outside DCS (`server/dll/test`) and with the user in a slot.
3. DTC tab: map layer + WYPT/routes/NAV settings + departure airfield.
4. COMM, SA, GPS WYPT, TACAN.
5. HARM, CMDS, RWR.
6. Manual, release.

## Progress (2026-10-29)
- Steps 1-5 built: defaults/defs tool, cartridge model; push (DLL 0.13 `dtc_push`, players by DCS
  player id so dynamic slots work before spawn; pilot hook polls without stalling and rebuilds the
  CMDS/RWR/HARM avionics tables with DCS's code); DTC tab with every Hornet section: waypoints
  (right-click places, drag, Ctrl/Shift multi-select, list drag renumbers, build route 1/2/3, leg
  distances), routes, NAV settings, departure field, COMM 1/2 (fill from mission), GPS, TACAN
  (terrain beacons only), SA (rings, CAP, corridors, FAOR/FLOT on the map, from picture, settings),
  HARM, CMDS, RWR. 255 tests.
- Known limit, by the user's choice: "Save as .dtc" does not rebuild the avionics tables (only a
  push does, in the pilot hook). Ships' TACANs are not offered (DCS keys them by unit id).
- Not yet tested live: a push from the tab to a pilot in DCS.

## Open points
- Which Hornet the cartridge is for when several recipients fly different types: v1 pushes only
  to Hornets; other types are greyed out in the list.
- F-16C and F-14B(U) built, with a shared plan across the three jets and per-type pushes
  (DLL 0.14). Remaining: AH-64D, CH-47F, MiG-29.
