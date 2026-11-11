# NEBO protocol (v1)

TCP, UTF-8, one JSON object per line (`\n`). Default port 10309. Every message has a
`type`. Readers must ignore unknown fields and unknown types, so fields can be added
without breaking older clients (or the future website backend).

**Visibility** says who may see a message on the controller link: `public` goes to both
sides, `coalition` only to that side. **Export** says whether it goes to the planned
website: all mission information does, **except units** (positions, tracks, pictures).

| Type | Direction | Visibility | Export | When |
|---|---|---|---|---|
| `hello` / `discord` / `proof` / `side` | client → server | — | — | login, one line per step ([login 2](#login-2-dll-010)) |
| `hello` / `auth_stage` / `auth_ok` / `side_ok` / `auth_fail` | server → client | — | — | replies to the login steps |
| `auth` | client → server | — | — | the login before 0.10 ([auth](#auth-before-010)) |
| `picture` | server → client | coalition | **no** (units) | every `interval` (1 s) |
| `theatre` | server → client | public | yes | after login, on mission change |
| `terrain` | server → client | public | yes | after login (sampled once per terrain, cached) |
| `foothold` | server → client | public | yes | after login, then when zones change (checked every 10 s) |
| `airfields` | server → client | public | yes | after login, then when an owner changes (checked every 60 s) |
| `weather` | server → client | public | yes | every 60 s |
| `unit_types` | server → client | public | no | after login, on mission change (hook 0.15.1) |
| `lso` | server → client | coalition | **no** (units) | 10 a second while own aircraft are within 10 NM of an own carrier |
| `marks` | server → client | coalition | no | after login, then when the coalition's F10 marks change (checked every 5 s) |
| `ids` | server → client | coalition | no | after login, then when the coalition's identities change (checked every 10 s) |
| `threats` | server → client | coalition | no | after login, then when a controller adds or removes a shared threat |
| `nctr` | server → client | coalition | no | when the coalition's NCTR requests change (every second while one runs) |
| `command` | client → server | — | no | when a controller acts (rate-limited) |
| `command_result` | server → client | that client | no | answer to each `command` |
| `pilots` | server → client | coalition | no | after login, then when the side's players or their slots change (checked every 2 s) |
| `dtc_live` | server → client | coalition | no | every 10 s (`dtc = true`): what the DTC tab fills cartridges from |
| `designations` | server → client | coalition | no | while the side's pilots send targeting pod points (DLL 0.15) |
| `dtc_request` / `dtc_push` / `dtc_none` / `dtc_fail` | pilot hook ↔ server | the pilot's side | no | every 10 s while the pilot is on a side (DLL 0.14) |
| `camera_picture` | server → camera | camera logins | **no** | every `camera_interval` (1 s) while a camera is logged in (DLL 0.16) |
| `camera_focus` | server → camera | camera logins | **no** | 5–20 a second while a camera asks for a focus (DLL 0.16) |
| `camera_set_focus` | camera → server | — | no | when the camera operator changes what is filmed |

## Pilot sync: dtc_push command, dtc_request (DLL 0.14)
A controller builds a whole data cartridge in the client's DTC tab and pushes it to pilots of
its side with a command (rate limits as every command, at most 50 recipients, cartridge ≤ 1 MB,
recipients first and the cartridge last in the line):
`{"type":"command","id":15,"name":"dtc_push","units":["3","7"],"cartridge":{"type":"F-16C_50","data":{…}}}`.
`units` are DCS player ids from `pilots`. The server hook refuses a player not on the server,
not on the controller's side, or in a slot of another aircraft type (with dynamic slots the
type is chosen only at spawn, so a player without a slot is accepted), keeps the newest
cartridge per player and aircraft type for this server run, and puts a text on screen for a
player in a cockpit. Result `"value":"sent to Viper 1-1; not to …"`.

The pilot-side hook `pilot/NEBO-DTC.lua` (in a pilot's `Saved Games\DCS\Scripts\Hooks`) is not
a controller login. While its player is on a side (slot or not) it asks every 10 s, one line
per connection, each server in its `SERVERS` list until one knows the player:
`{"type":"dtc_request","version":2,"player":"<DCS player id>","have":<last version received>}`
(plus `"designation":{…}` while the targeting pod has a point, see below). No password: the
server answers only an address (IP) that belongs to that player, at most once per 3 s per
address, then closes the connection:
- `{"type":"dtc_push","version":<n>,"from":"<controller>","cartridge":{…}}`: the oldest pushed
  cartridge newer than `have`; the hook writes `Saved Games\DCS\DTC\NEBO_<type>.dtc` for the
  DTC dialog and asks again with that version.
- `{"type":"dtc_none"}`: nothing newer.
- `{"type":"dtc_fail","reason":"update" | "too_soon" | "not_on_a_side"}` (`update`: a 0.12
  hook's version 1 request).

## dtc_live (coalition)
What the DTC tab fills cartridges from, rebuilt by the server hook every 10 s while
`dtc = true` in NEBO.lua:
```
{"type":"dtc_live","version":1,"coalition":"blue","theatre":"Caucasus","time":"2026-10-29T08:51:54Z",
 "threats":[{"type":"Kub 1S91 str","name":"SAM SA-6 Kub \"Straight Flush\" STR","x":-280000,"z":640000}],
 "comms":[{"name":"Texaco","mhz":266.000,"mod":0}],
 "points":[{"name":"BULLSEYE","x":-290174,"z":614261,"alt":0}]}
```
- `threats`: what the coalition knows — identified enemy air defence and ship contacts of its
  picture (DCS type and display name), contacts its controllers identified (`type` empty, `name`
  the label, e.g. "11"), and its controllers' shared threats (`name` e.g. "SA-10"). At most 120.
- `comms`: the coalition's live AWACS and tanker groups (Mission Editor group frequency), then
  its own airdromes' towers (UHF, else VHF, from the theatre's `Radio.lua`); one per frequency.
  `mod` 0 AM, 1 FM.
- `points`: the coalition's bullseye, then its own airdromes (`alt` metres).
- Positions in DCS map metres (x north, z east).

## pilots (coalition)
`{"type":"pilots","coalition":"blue","pilots":[{"id":3,"player":"Kestrel","slot":"6209","type":"F-16C_50"}]}`
— the side's players, slot or not (`slot` and `type` empty without one): the DTC tab's
recipients. Sent when the list changes.

## designations (coalition, DLL 0.15)
A pilot's targeting pod point rides on their hook's `dtc_request` (version 2) to the server they
are on: `"designation":{"lat":42.567367,"lon":41.628833,"alt_ft":51,"range_nm":20.0,
"had_major_m":6482,"had_minor_m":1228,"had_pgm":2,"had_brg":103,"track":"INR POINT"}`, read
by `pilot/NEBO-Export.lua` (F-16C) from the TGP page (`TGP_coordinates_lat_lon`, …) and,
with an emitter designated, the HAD page's HTS block (`MajorAxis`, `MinorAxis`, `PGMs`,
`BearingTarget`); in the F/A-18C from the ATFLIR page's coordinates datablock
(`FLIR_coords_root_A`: `N 42:30.63`, `E 41:39.04`, `ELEV 48`; hemisphere dropped, `track`
"FLIR SCENE" from `MPD_FLIR_TrackMode_A`, `range_nm` from `MPD_FLIR_TGT_Range_A`), only while
that range shows (the pod tracks or has a target); sent while fresh (≤15 s old). The DLL rebuilds it from these fields only, each
range-checked (`lat` 0–90, `lon` 0–180: the pod page shows no hemisphere; `track` capitals,
digits and spaces, ≤16), or drops it. The server hook keeps each player's latest, and every
emitter they designated on the HAD (a HAD designation within 2 km of a kept one, or within half
the larger of their ellipses, refines it; otherwise it is a new emitter, at most 20 per player),
each for 10 minutes after it last came, and publishes the coalition's list (again at least every
10 s while it is not empty):
```
{"type":"designations","coalition":"blue","designations":[{"player":2,"name":"Kestrel","slot":"6209",
 "aircraft":"F-16C_50","age":4,"emitter":1,"current":true,"lat":42.567367,"lon":41.628833,"alt_ft":51,
 "range_nm":20.0,"had_major_m":6482,"had_minor_m":1228,"had_pgm":2,"track":"INR POINT"}]}
```
- `emitter`: the player's number for a kept HAD emitter (absent on a plain pod point);
  `current`: what the pod points at now (one per player at most). Both from NEBO 0.14.3's hook on;
  a client without them shows the one designation as current.
- `age`: seconds since the server got it. `had_*`: the HTS error ellipse's full axes (metres)
  and grade when an emitter was designated on the HAD; the client orients it along the line from
  the pilot to the point. Signs: the client picks those nearest the pilot's own aircraft.
- Controllers see it only with a map's **TGP** button on (off by default).

## command / command_result
A controller acting on the server. The DLL accepts only known command names with checked
fields, at most 5 in one second and 30 a minute per connection, and runs them on the sim
thread through fixed mission functions (client text is passed as a string, never run).
Every command and refusal is logged to dcs.log with coalition and client address.

`{"type":"command","id":7,"name":"message_unit","unit":4940,"text":"…","seconds":30}` — an
on-screen text box (`trigger.action.outTextForUnit`, not chat) for one aircraft of the
controller's own coalition with a player in it. `unit`: DCS unit id from the picture; `text`:
≤ 1 500 bytes, no control characters except newlines; `seconds`: 5–120.

`{"type":"command","id":8,"name":"mark_add","x":30000,"z":40000,"text":"SA-6 (GCI)","threat":"SA-6"}`
(DLL 0.6) — an F10 map mark for the controller's own coalition
(`trigger.action.markToCoalition`, not read-only: the coalition's players can delete it in the
game). `x`, `z`: DCS metres (integers, ±2 000 km); `text`: ≤ 200 bytes, one line; `threat`
(optional): ≤ 24 letters, digits, `-`, `/`, spaces — kept with the mark. Result
`"value":"880000"`: the mark id. Clients treat any mark whose text starts with a threat type
("SA-6", "sa11 …", "Patriot") as a live threat with its ring, whoever placed it; the mark is
the threat, so deleting it in the game removes the threat from every controller.

`{"type":"command","id":9,"name":"mark_remove","mark":880000}` — removes a mark NEBO made for
the controller's coalition (any controller of it); other marks are refused (`not a NEBO mark of
your coalition`).

`{"type":"command","id":10,"name":"carrier_lights","unit":445,"mode":2}` (DLL 0.7) — a carrier's
lighting mode, as the Mission Editor's SET CARRIER LIGHTING MODE (`a_set_carrier_illumination_mode`
in the mission env): `mode` −2 off, −1 auto, 0 navigation, 1 launch, 2 recovery. Only a carrier
type NEBO knows, of the controller's own coalition (`not a ship of your coalition (or it is
gone)`, `not a carrier`). Verified in multiplayer on CVN-72 and Stennis.
DCS does not report a carrier's current mode.

`{"type":"command","id":11,"name":"identify","unit":1002192,"label":"50"}` (DLL 0.8) — an identity
for a radar contact, shared with the controller's coalition (`ids` channel). `unit`: an existing
unit of the enemy coalition (`not an enemy unit (or it is gone)`); `label`: ≤ 24 letters, digits,
`-`, `/`, spaces — an RWR label ("29", "6") or a name ("Su-34", "SA-11"); empty clears it. Kept
by the hook per coalition until the unit is gone or the mission changes.

`{"type":"command","id":12,"name":"threat_add","x":-280000,"z":640000,"threat":"SA-11"}` (DLL 0.8)
— a threat shared with the controller's coalition's controllers only (`threats` channel; not an
F10 mark, pilots do not see it). `x`, `z`: DCS metres (±2 000 km); `threat`: ≤ 24 letters,
digits, `-`, `/`, spaces. Result `"value":"1"`: its id. At most 200 per coalition; gone with the
mission. `{"type":"command","id":13,"name":"threat_remove","threat_id":1}` — any controller of
that coalition removes it (`no such threat of your coalition`).

`{"type":"command","id":14,"name":"nctr","x":-300000,"z":600000,"x2":-250000,"z2":680000,"methods":"jem,size"}`
(DLL 0.8) — NCTR on the contacts the coalition holds inside the box (two corners, DCS metres, at
most 400 km a side) with the methods named (`jem`, `hrrp`, `isar`, `size`, `rotor`, `raid`; lower case,
commas). Result `"value":"3"`: the request id; refused with `no contacts in the area`, `three
NCTR requests already running`, `no known method …`.

Answer to the same client: `{"type":"command_result","id":7,"ok":true}` (with `"value"` when
the command returns one) or
`{"type":"command_result","id":7,"ok":false,"error":"no player in that aircraft"}`. Errors
include `unknown command`, field errors (`seconds: 5 to 120`, `text has control characters`),
`too many commands, wait a moment`, `server busy`, `server not ready yet`, `not an aircraft of
your coalition (or it is gone)`.

## marks (coalition)
`{"type":"marks","coalition":"blue","marks":[{"id","text","x","z","lat","lon","time","author",
"nebo","threat"}]}` — the F10 map marks the coalition can see (its own and those for all, at most
300; `world.getMarkPanels`): players', scripts' (Foothold zone labels, CSAR downed pilots) and
NEBO's. `author`: the placing player's name, or "mission data" for scripts; `nebo`: made by a
controller with `mark_add`, then `threat` is its threat type or null; `time`: mission seconds.
Sent per side on its own channel (`publish_side`), only when the list changes. Not exported.

## weather
`{"type":"weather","date":"2025-07-01","time":<mission s since local midnight>,"visibility":<m>,
"fog":{"on","visibility","thickness"},"dust":{"on","density"},"clouds":{"base","thickness",
"density","preset","precipitation"},"airfields":[{"id","elevation","windFrom","windMs","tempC",
"qnhHpa"},…]}` — mission weather plus, per airdrome, the live wind 10 m above the field (from,
true degrees; m/s), temperature (°C) and QNH (hPa, local pressure reduced with the ISA lapse
rate). `preset` is a DCS cloud preset name or null (then `density` 0–10 applies);
`precipitation` 0 none, 1 rain, 2 thunderstorm, 3 snow, 4 snowstorm.

## unit_types
`{"type":"unit_types","version":1,"types":{"<DCS unit type>":[w1,w2,w3,w4],…}}` — every unit
type in the server's Mission Editor unit database with its wsType (the first four attributes),
~13 KB. The data cartridge's threat base names many threats only by wsType; the client turns
them into unit types with this (marking the own side's RWR emitters and HARM radars friendly).
Read from the server's own DCS at run time, so no Eagle Dynamics data ships with NEBO.

## lso (coalition)
`{"type":"lso","coalition":"blue","time":<mission s>,"carriers":[{"id","x","y","z","hdg","vx",
"vz"}],"aircraft":[{"id","x","y","z","vx","vy","vz","hdg","pitch","gear","hook"}]}` — the fast
feed for the LSO view: the side's carriers and its aircraft within 10 NM of one, in DCS metres
(x north, y up, z east), m/s and degrees from grid north. `gear`/`hook` 0..1 from the model's
draw arguments 0 and 25, null when the model has none (25 is not the hook on every type).
Sent 10 times a second only while such aircraft exist; the aircraft list is refreshed with each
picture. The DLL keeps it in its own per-side slot (`publish_side`), so it never displaces the
picture. Coalition-private, not exported.

## nctr (coalition)
`{"type":"nctr","coalition":"red","requests":[{"id":1,"state":"running|done","time":<mission s>,
"left":<s>,"area":[x1,z1,x2,z2],"methods":["jem","size"],"radars":<platforms taking part>,
"cones":[{"platform":"<own unit name>","x","z","center":<grid deg>,"half":<deg>,"range":<m>}],
"results":[{"unit":<contact id>,"method":"jem","status":"pending|ok|none","active":<bool>,"result":"F-16
(F100/F110)"|null,"reason":"range 61 NM (max 40)"|null,"platform":"<own unit name>"|null}]}]}` —
the coalition's NCTR requests, running or done in the last 120 s, one result per contact and
method. JEM answers the engine family, HRRP / ISAR / rotor the DCS type, size `small | medium |
large`, raid the aircraft in the contact's return (`single aircraft`, `2 aircraft`, …); `reason` says why a method failed (or, while pending, why it is not working yet). While a
request runs, each radar taking part looks along its cone (covering the targets it holds, ≤ 30°;
`cones`, running requests only); contacts that only such radars hold and that lie outside all
their cones carry `"frozen":true` in the picture (their last position). Sent when the line changes.

## threats (coalition)
`{"type":"threats","coalition":"blue","threats":[{"id":1,"x":-280000,"z":640000,"threat":"SA-11","time":<mission s>}]}`
— every threat the coalition's controllers share (`threat_add`), DCS metres. Sent after login
and whenever one is added or removed. Bearing lines are not shared (each controller's own).

## ids (coalition)
`{"type":"ids","coalition":"blue","mode":"manual","ids":[{"unit":1002192,"label":"50","time":<mission s>}]}`
— the contacts the coalition's controllers identified (`identify`), and the server's
`identification` setting: `"manual"` (contacts in the picture never carry `type`; only these
identities name them) or `"auto"` (a contact carries its DCS type once DCS identifies it).
Sent after login and whenever an identity is set, cleared, or its unit is gone (checked every
10 s). Clients show the label where the type would be; an identity of a ground unit or ship
that names a SAM ("6", "SA-11") gives it that threat ring.

## login 2 (DLL 0.10)
One line each way per step; any refusal is `{"type":"auth_fail","reason":…}` and the
connection closes. Reasons: `update` (an old client while Discord is on), `protocol` (a step out
of order), `discord` / `discord_down`, `role` (no controller role), `password`, `flying`,
`side`, `closed` (no logins set up).
1. client `{"type":"hello","client":"…","version":2}` → server
   `{"type":"hello","version":2,"nonce":"<32 hex>","discord":"<application id>"}` (`discord`
   only when the server uses Discord login).
2. With Discord: the client asks the Discord app on its PC (local pipe `discord-ipc-N`,
   command `AUTHORIZE`, scopes `identify guilds guilds.members.read`) for a one-time code and
   sends `{"type":"discord","code":"…"}`. The server swaps it with Discord for the user and
   their roles in the guild (its secret never leaves it) →
   `{"type":"auth_stage","stage":"password","user":"<Discord name>"}`.
3. client `{"type":"proof","proof":"<hex HMAC-SHA256(key = server password, message = nonce)>"}`
   → `{"type":"auth_ok","version":2,"user":…,"sides":["blue","red"]}`. The password is never
   sent; the nonce is new for every connection.
4. client `{"type":"side","coalition":"blue"}` → `{"type":"side_ok","coalition":"blue"}`; from
   here the connection works as below. The server re-checks the Discord role every 5 minutes
   and sends `{"type":"closed","reason":"role"}` when it is gone.

A server before 0.10 answers the hello with a bare `{"type":"auth_fail"}`: the client then
reconnects with the old login below.

### Camera login (DLL 0.16)
NEBO Camera adds `"camera":true` to the hello. The server then skips Discord and checks the
proof against `camera_password` only (`auth_fail` `closed` when none is set, `password` when
wrong, `flying` from an address in a cockpit, whatever `allow_flying_controllers` says), answers
`"sides":["camera"]`, and takes `{"type":"side","coalition":"camera"}` →
`{"type":"side_ok","coalition":"camera"}`. A camera receives the public channels and
`camera_picture` / `camera_focus` (every unit of both sides, all missiles), never a coalition
picture or channel, and sends nothing but `camera_set_focus`
(`{"type":"camera_set_focus","ids":[…≤4],"hz":5–20}`, at most 10 a second; `[]` stops the focus
feed). Weapon ids on camera channels are 1000000000 + the missile's id. It is closed with
`{"type":"closed","reason":"flying"}` when its address takes a cockpit. Wire format of the two
camera messages: NEBO Camera's PROTOCOL-CAMERA.md.

## auth (before 0.10)
`{"type":"auth","password":"…","client":"NEBO Client 0.1"}`. The password decides the
coalition. Reply: `{"type":"auth_ok","coalition":"blue"}`, or `{"type":"auth_fail"}` and
the connection closes. `{"type":"auth_fail","reason":"flying"}` (DLL 0.9): the password was
right but a player in a cockpit has the controller's IP address (controllers don't fly).

## closed
`{"type":"closed","reason":"flying"}` (DLL 0.9): sent before the server closes a logged-in
controller's connection because its IP address took a cockpit.

## picture (coalition)
`{"type":"picture","version":1,"coalition":"blue","time":<mission s>,"theatre":"Syria","bullseye":{"x","z","lat","lon"},"units":[…],"weapons":[…]}`

A radar contact (`"detected":true`) also carries `"age"` (DLL 0.9, `radar_scan = true`): seconds
since a radar holding it last painted it. Its position, altitude, heading and speed are those of
that plot, not of the picture's time.

`weapons`: missiles in flight, `[{"id","type","coalition","shooter","target","lat","lon","alt",
"hdg","spd","vs"}]`. The coalition's own missiles carry everything; an enemy missile is
included only while aimed at one of the coalition's units, with `type` and `shooter` null.
`id` is the server's own missile number (not a unit id); `shooter` and `target` are unit ids.
A missile missing from the next picture no longer exists. Coalition-private, not exported.

`carriers`: the coalition's carriers (DLL 0.4), `[{"id","type","windFrom","windMs","tacan",
"icls","link4","acls","radio"}]` — unit id and DCS type, the wind 20 m above the deck (from, true
degrees; m/s), and the beacons the mission's group tasks set (`tacan` "74X JCS", `icls` channel,
`link4` and `radio` MHz, `acls` on/off; null when not set). `events`: the coalition's landings
on carriers in the last minute (`S_EVENT_LAND` on a ship), `[{"id","kind":"trap","unit",
"carrier","time","x","z","cx","cz"}]` with DCS metres of aircraft and ship; a client keeps each
`id` once. Both coalition-private, not exported.

`bullseye`: the coalition's own bullseye (`coalition.getMainRefPoint`), read every picture
since scripts can move it; `null` if unavailable. Coalition-private like the rest of the picture.

Unit: `id, name, type, callsign, player, group, category (airplane|helicopter|ground|ship|structure),
coalition, detected, lat, lon, alt (m MSL), hdg (deg true), spd (m/s), vs (m/s), emitter`.
Own units carry everything. Radar contacts (`detected: true`) carry no name, callsign,
player or group, and `type` only once DCS has identified them.

## theatre (public)
`{"type":"theatre","id":"Syria","x0","z0","x1","z1","grid":{"n":21,"lat":[…],"lon":[…]},"magvar":{"lat0","lon0","step","nlat","nlon","deg":[…]},"towns":[[name,lat,lon],…]}`

Bounds are DCS map metres (x north, z east). `grid` is an n×n lat/lon control grid over
the bounds (row-major, x outer); x/z → lat/lon by bilinear interpolation.
`magvar`: magnetic variation in degrees, east positive, from DCS's own model
(`magvar.get_mag_decl`, mission date) on a lat/lon grid covering the theatre — row i at
`lat0 + i·step`, column j at `lon0 + j·step`, row-major; bilinear between nodes. Magnetic =
true − variation. `null` if the server could not sample it (clients then show true bearings, marked "T").

## terrain (public)
`{"type":"terrain","id","x0","z0","step","nx","nz","heights":<base64 int16 LE>,"surfaces":<base64 uint8>}`

Row i at x = x0 + i·step, column j at z = z0 + j·step. Surfaces: 1 land, 2 shallow water,
3 water, 4 road, 5 runway.

## airfields (public)
`{"type":"airfields","airfields":[{"id","name","category" (0 airdrome, 1 helipad),"x","z","coalition",
"runways":[{"name","course","length","width","x","z"}],"parking":[[number,x,z,type],…]}]}`

From DCS `world.getAirbases()`, `Airbase:getRunways()`, `Airbase:getParking()`; ships are
excluded (they are units). DCS map metres; runway `course` is radians with the opposite sign
of the true heading. Parking `type` is DCS Term_Type (40 helicopter only, 68 hardened
shelter, 72/104 open, 100 small).

## foothold (public)
`{"type":"foothold","zones":[…],"links":[[from,to],…],"missions":[…],"credits":{"red","blue"}}`

Zone: `name, side (0 neutral, 1 red, 2 blue), x, z, r (trigger zone, DCS metres), active,
suspended (idle, units despawned), airbase, facility, logistic, built, max (owner's upgrade
slots), wpt, lat, lon` (zone centre, server-converted for the website). Hidden zones are not sent.
`missions`: Foothold's mission board — `{title, description, side, status: "active"|"available", strike}`
(texts as Foothold prints them). `credits`: each side's Foothold credits. Sent when zones, links or
missions change; a change in credits alone at most once a minute.
