<p align="center"><img src="docs/images/banner.png" alt="NEBO ATC/GCI by Kestrel's Nest Community"></p>

**A GCI and ATC controller station for DCS World multiplayer servers. Free and open source.**
<sub>By [Kestrel's Nest Community](https://kestrelsnest.co.ua/). "Nebo" is "sky" in Ukrainian. Formerly known as Overseer and Eagle's Nest.</sub>

Your controllers sit at their own PC and see what your coalition's radars see — no more. They
call BRAA and bullseye, run intercepts, identify contacts, work the tower and the carrier and
talk on SRS, all from one screen.

![NEBO GCI workspace](docs/images/hero.png)

<p align="center">
<a href="https://github.com/kestrelsnest/nebo/releases/latest"><b>Download NEBO-Setup</b></a> ·
<a href="docs/MANUAL.md"><b>Manual</b></a> ·
<a href="https://discord.gg/FZGRqwc2Eg"><b>Discord</b></a>
</p>

<p align="center"><img src="docs/images/divider.png" alt="" width="100%"></p>

## Why NEBO

- **Real fog of war.** The picture is built on the DCS server from your side's own sensors and
  sent to your side only. An enemy jet no radar of yours holds never leaves the server.
- **Radars sweep, identities are earned.** An AWACS refreshes every 10 s, a SAM search radar
  every second or two. Contacts can arrive as unknowns: you identify them from pilots' RWR calls
  and NCTR (JEM, HRRP, ISAR, size, rotor, raid count), each with its own range and aspect rules.
- **Built from DCS itself.** Maps, charts, satellite imagery, airport diagrams and the pilots'
  data cartridge formats come from the controller's own DCS install — nothing to download.
- **GCI and ATC in one.** Intercepts and engagement lines next to runway queues, approaches,
  marshal and LSO grading.
- **Light on the server.** Measured on two busy Foothold servers: about 3–7 ms of the server's
  time per second, spread so that no frame carries more than one NEBO job; while nobody is
  controlling, the picture slows to one every 5 s.

<p align="center"><img src="docs/images/divider.png" alt="" width="100%"></p>

## Get started

One setup installs every part: download `NEBO-Setup-<version>.exe` from the
[Releases page](https://github.com/kestrelsnest/nebo/releases), run it and tick what you need:
the controller app, the Stream Deck plugin, the pilot hook or the DCS server hook. It finds your
DCS Saved Games folders, replaces old Overseer / Eagle's Nest files, and running it again
updates. The app tells you when a new version is out. The zips on the same page are the
same parts for copying by hand. The setup is not code-signed yet: if Windows SmartScreen says
"Windows protected your PC", click **More info → Run anyway**.

### Run it on your server (admins)
1. Stop the DCS server and run the setup with **DCS server hook** ticked: it asks for the
   server's Saved Games folder, the port (opening it in the Windows firewall if you like) and
   five settings (enemy ground units, identification, radar scan, flying controllers, data
   cartridges), each shown in pictures.
2. Start the server. The first start writes `Config\NEBO.lua` with a random password.
3. Give the password to your controllers (or switch on Discord role login).

By hand: copy `copy-into-saved-games` from `NEBO-Server-<version>.zip` into the server's Saved
Games folder and open TCP port 10309. Silent installs: `INSTALL.txt`.

### Control (controllers)
1. Install the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64) once
   (the setup links it when it is missing).
2. Run the setup with **Controller app** ticked and start NEBO from the Start menu.
3. Confirm your DCS World install when asked (it must be installed, not running).
4. Connection → Connect → New: the server's address, port 10309, the password.
5. Pick your side — then read the [manual](docs/MANUAL.md) and the [radio calls](docs/RADIO-CALLS.md).

### Fly with it (pilots)
Nothing to install to fly with a controller: you hear them on SRS as usual. Two optional extras
come with the setup's **Pilot hook** (it asks for your NEBO servers' addresses):
- **Data cartridges** (work in progress): add one hook file and your controller can push a complete DTC
  (waypoints, threats, radios, countermeasures) for the F/A-18C, F-16C or F-14B(U).
- **Share your targeting pod:** F-16C and F/A-18C pilots can send their TGP / ATFLIR point.

<p align="center"><img src="docs/images/divider.png" alt="" width="100%"></p>

## What you get

### GCI
Speed vectors, data blocks with altitude, speed and Mach, threat rings, missiles in flight
(launches at your players sound an alarm), bullseye under the cursor, a BRAA ruler, packages in
colour, Foothold campaign zones. Flights in formation share one label; labels never cover a
symbol, a vector or each other.

### Engagements
Pin a BRAA line between two aircraft: it follows both and shows the **collision intercept** —
point, time and heading to fly. The Engagements panel ranks them, sounds a short warning at
your alert range, and keys 1–8 (or the Stream Deck) jump the map to each one.

![Engagements](docs/images/engagements.png)

### Identification, RWR and NCTR
An own aircraft's RWR ring turns with its nose: click the clock position a pilot calls for a
bearing line; two lines give a fix with its error. NCTR by your AI radars works a box you drag,
with its cones on the map.

| | |
|---|---|
| ![RWR ring](docs/images/rwr-fix.png) | ![NCTR](docs/images/nctr.png) |
| The RWR ring turns with the jet: a pilot's "3 o'clock" becomes a bearing line | NCTR: the radars' cones on a contact |

### ATC and carrier
Airport diagrams from DCS data (runways, taxiway letters, stands, navaids), a 10 NM approach
view with 3° path altitudes, runway queues, ATIS sent to a pilot's screen, the carrier with BRC,
wind over deck and marshal stack, and LSO grading.

| | | |
|---|---|---|
| ![Airport diagram](docs/images/atc-diagram.png) | ![Approach](docs/images/atc-approach.png) | ![Carrier](docs/images/carrier.png) |
| Airport diagram and runway queue | 10 NM approach | Carrier and marshal |

### Data cartridges for your pilots — *work in progress*
Plan waypoints, routes, threat rings, radios, CMDS, RWR and HARM tables, JDAM targets on the map —
in DCS's own formats and limits, one plan for all three jets — and push it to a pilot or a
whole package. Each pilot gets the cartridge of the jet they fly.

> The DTC is still being built: it works for the F/A-18C, F-16C and F-14B(U), but expect gaps
> and changes between releases. Reports from pilots who try it are very welcome.

### Maps, radio and the Stream Deck
- Base maps: Scope, DCS Map, DCS Charts and DCS Satellite from your own install, and online
  satellite imagery.
- **SRS:** tunes your SRS client, sends your radio position (or the best spot for a player),
  and shows who is on which frequency and who is talking.
- **Stream Deck XL page:** engagements coloured by range, ten SRS channels with transmit
  highlight, map filters with four brightness steps, labels and a hold-to-show "players only".

| | |
|---|---|
| ![Map styles](docs/images/maps.png) | ![Stream Deck](docs/images/streamdeck.png) |
| DCS Map, Charts and Satellite from your install | The Stream Deck XL page |

<p align="center"><img src="docs/images/divider.png" alt="" width="100%"></p>

## Home servers

NEBO is built and run on the **Kestrel's Nest** servers — come and see it live
([kestrelsnest.co.ua](https://kestrelsnest.co.ua/), [Discord](https://discord.gg/FZGRqwc2Eg)).

**Running NEBO on your server?** [Tell us](https://github.com/kestrelsnest/nebo/issues)
and you can be listed here.

<p align="center"><img src="docs/images/divider.png" alt="" width="100%"></p>

## Support the project

NEBO is free, and it stays free — every feature, for every server.

Bug reports and ideas are welcome: [open an issue](https://github.com/kestrelsnest/nebo/issues)
or talk to us on [Discord](https://discord.gg/FZGRqwc2Eg). Building from source and the code's
layout: [DEVELOPMENT.md](docs/DEVELOPMENT.md); contributing: [CONTRIBUTING.md](CONTRIBUTING.md).

<p align="center"><img src="docs/images/divider.png" alt="" width="100%"></p>

## Coming from Overseer or Eagle's Nest?

NEBO is the same project under its new name (0.16.0 on). The server and the client move their
settings over on their first start: the server hook copies `Config\DCB.lua` to `Config\NEBO.lua`
and the client keeps your saved servers, layouts and maps. The setup removes the old server
hook (`Scripts\Hooks\DCB-hook.lua`, `Mods\Services\DCB`) and the old pilot hooks, keeping the
pilots' server lists. Installing by hand: `INSTALL.txt`, `PILOT.txt` and `STREAMDECK.txt` in
the packages say what to replace; old pilot hooks keep working meanwhile.

<p align="center"><img src="docs/images/divider.png" alt="" width="100%"></p>

## Security

The connection is plain TCP: passwords and the picture are not encrypted on the way. Limit the
firewall rule to your controllers' addresses or use a VPN (Tailscale, ZeroTier, WireGuard).
Controller actions on the server are a short, checked, rate-limited and logged list, and
**controllers don't fly**: the server refuses a controller login from the address of a player
in a cockpit. Also set `allow_object_export = false` and `allow_sensor_export = false` in DCS's
own server settings; NEBO does not need them.

## License

NEBO ATC/GCI by Kestrel's Nest Community, © 2026 Bullat951, is free software under the
[GNU GPL v3](LICENSE) with [additional permissions](LICENSE-EXCEPTION.md) (AvalonDock's MS-PL,
DCS World), from version 0.15.1; versions up to 0.15.0 were released as Eagle's Nest under MIT.
Third-party components and data: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

DCS World is a trademark of Eagle Dynamics SA. NEBO is an independent community project,
not affiliated with or endorsed by Eagle Dynamics.
