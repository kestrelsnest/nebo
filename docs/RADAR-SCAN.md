# Radar scan model — settings per radar (2026-10-29)

How the server's collector (`server/lua/collector.lua`, "Radar scan model") decides **when** an own radar
paints a contact. **Whether** a radar holds a contact at all is always DCS's own detection
(`getDetectedTargets`); this model only times the plots.

## Rules
- **Period** (s): one sweep. **Rotating** (rotodome, EWR, ship's search radar): a contact is painted
  when the beam passes its bearing, so contacts at different bearings are painted at different
  moments. **Frame** (fighter radar, phased array): everything held is painted once per period.
- **Phase:** each radar platform has its own fixed offset, so several radars refresh a contact more
  often than any one of them. Offsets are spread evenly in the order the radars are first met
  (n-th radar: frac(n × 0.618) of its period), so no two start together. A contact shows its
  latest plot from any radar holding it, and each radar adds at most one plot per sweep.
- **Edge of range:** a paint always lands up to 80 % of the radar's range, then the chance falls
  to 50 % at the edge (the same for every client). Range = DCS's longest air detection distance
  for that radar **at runtime** (`getSensors`), the "Range used" column.
- **From:** `table` = the collector's `SCAN_TYPES` list (first name match). `fallback` = by kind:
  AWACS 10 s rotating · ship 6 s rotating · ground EWR 10 s rotating · other ground 4 s rotating ·
  aircraft / helicopter 4 s frame.
- **NCTR:** a radar dwelling on an NCTR cone paints that cone every second and nothing else.
- A ground or naval **group** is one platform: its first unit with a radar gives the position and
  range, the first unit whose type is in the table gives the period.

## Columns
- *Range used*: runtime range, read from units in the test mission (Foothold Caucasus). Empty: that
  type is not in the mission (aircraft radars are not in DCS's sensor database, so only runtime
  shows them).
- *DB range*: DCS's sensor database (`db.Sensors`), for a reference target; usually larger than the
  runtime figure.
- *DCS scan_period*: DCS's own figure, for comparison; the collector does not use it.

Fixed 2026-10-29: `F-5E-3` matched the AWACS entry `"e-3"` (10 s rotodome); the entry is `"e-3a"` now.
### Aircraft

| Type | Radar (DCS) | Range used, km | DB range, km | Period, s | Kind | From | DCS scan_period |
|---|---|---|---|---|---|---|---|
| A-50 |  | 204 |  | 10 | rotating | table |  |
| A6E |  |  |  | 4 | frame | fallback |  |
| AJS37 |  |  |  | 4 | frame | fallback |  |
| B-1B |  |  |  | 4 | frame | fallback |  |
| B-52H |  |  |  | 4 | frame | fallback |  |
| C-101CC |  |  |  | 4 | frame | fallback |  |
| E-2C |  | 211 |  | 10 | rotating | table |  |
| E-3A |  | 256 |  | 10 | rotating | table |  |
| F-14A | AN/APG-71 |  | 167 | 4 | frame | fallback | 5 |
| F-14A-135-GR | AN/APG-71 | 112 | 167 | 4 | frame | fallback | 5 |
| F-14A-135-GR-Early | AN/APG-71 |  | 167 | 4 | frame | fallback | 5 |
| F-14A-95-GR | AN/APG-71 |  | 167 | 4 | frame | fallback | 5 |
| F-14B | AN/APG-71 | 112 | 167 | 4 | frame | fallback | 5 |
| F-14BU | AN/APG-71 |  | 167 | 4 | frame | fallback | 5 |
| F-15C | AN/APG-63 | 59 | 88 | 4 | frame | fallback | 5 |
| F-15E | AN/APG-63 | 59 | 88 | 4 | frame | fallback | 5 |
| F-15ESE | AN/APG-63 | 59 | 88 | 4 | frame | fallback | 5 |
| F-16A |  |  |  | 4 | frame | fallback |  |
| F-16A MLU |  |  |  | 4 | frame | fallback |  |
| F-16C bl.50 |  |  |  | 4 | frame | fallback |  |
| F-16C bl.52d |  |  |  | 4 | frame | fallback |  |
| F-16C_50 |  | 46 |  | 4 | frame | fallback |  |
| F-4E |  |  |  | 4 | frame | fallback |  |
| F-4E-45MC |  | 40 |  | 4 | frame | fallback |  |
| F-5E | AN/APQ-153 |  | 37 | 4 | frame | fallback | 3 |
| F-5E-3 | AN/APQ-159 | 49 | 74 | 4 | frame | fallback | 3 |
| F-5E-3_FC | AN/APQ-159 |  | 74 | 4 | frame | fallback | 3 |
| F/A-18A |  |  |  | 4 | frame | fallback |  |
| F/A-18C |  |  |  | 4 | frame | fallback |  |
| FA-18C_hornet |  | 51 |  | 4 | frame | fallback |  |
| H-6J |  |  |  | 4 | frame | fallback |  |
| J-11A | N-001 |  | 68 | 4 | frame | fallback | 5 |
| JF-17 |  | 60 |  | 4 | frame | fallback |  |
| KJ-2000 |  |  |  | 5 | frame | table |  |
| M-2000C |  | 53 |  | 4 | frame | fallback |  |
| MQ-9 Reaper |  |  |  | 4 | frame | fallback |  |
| MiG-19P | N-008 | 33 | 50 | 4 | frame | fallback | 3.5 |
| MiG-21Bis | N-008 | 33 | 50 | 4 | frame | fallback | 3.5 |
| MiG-23MLD | N-008 | 33 | 50 | 4 | frame | fallback | 3.5 |
| MiG-25PD | N-005 | 37 | 56 | 4 | frame | fallback | 3.5 |
| MiG-25RBT |  |  |  | 4 | frame | fallback |  |
| MiG-29 Fulcrum | N-019 | 46 | 60 | 4 | frame | fallback | 5 |
| MiG-29A | N-019 | 46 | 60 | 4 | frame | fallback | 5 |
| MiG-29G | N-019 |  | 60 | 4 | frame | fallback | 5 |
| MiG-29S | N-019M | 46 | 60 | 4 | frame | fallback | 5 |
| MiG-31 | BRLS-8B | 96 | 200 | 4 | frame | fallback | 5 |
| Mirage 2000-5 |  |  |  | 4 | frame | fallback |  |
| Mirage-F1AD |  |  |  | 4 | frame | fallback |  |
| Mirage-F1AZ |  |  |  | 4 | frame | fallback |  |
| Mirage-F1B |  |  |  | 4 | frame | fallback |  |
| Mirage-F1BD |  |  |  | 4 | frame | fallback |  |
| Mirage-F1BE |  |  |  | 4 | frame | fallback |  |
| Mirage-F1BQ |  | 53 |  | 4 | frame | fallback |  |
| Mirage-F1C |  |  |  | 4 | frame | fallback |  |
| Mirage-F1C-200 |  |  |  | 4 | frame | fallback |  |
| Mirage-F1CE |  | 53 |  | 4 | frame | fallback |  |
| Mirage-F1CG |  |  |  | 4 | frame | fallback |  |
| Mirage-F1CH |  |  |  | 4 | frame | fallback |  |
| Mirage-F1CJ |  |  |  | 4 | frame | fallback |  |
| Mirage-F1CK |  |  |  | 4 | frame | fallback |  |
| Mirage-F1CR |  |  |  | 4 | frame | fallback |  |
| Mirage-F1CT |  |  |  | 4 | frame | fallback |  |
| Mirage-F1CZ |  |  |  | 4 | frame | fallback |  |
| Mirage-F1DDA |  |  |  | 4 | frame | fallback |  |
| Mirage-F1ED |  |  |  | 4 | frame | fallback |  |
| Mirage-F1EDA |  |  |  | 4 | frame | fallback |  |
| Mirage-F1EE |  | 53 |  | 4 | frame | fallback |  |
| Mirage-F1EH |  |  |  | 4 | frame | fallback |  |
| Mirage-F1EQ |  |  |  | 4 | frame | fallback |  |
| Mirage-F1JA |  |  |  | 4 | frame | fallback |  |
| Mirage-F1M-CE |  |  |  | 4 | frame | fallback |  |
| Mirage-F1M-EE |  |  |  | 4 | frame | fallback |  |
| QF-4E |  |  |  | 4 | frame | fallback |  |
| S-3B |  |  |  | 4 | frame | fallback |  |
| S-3B Tanker |  |  |  | 4 | frame | fallback |  |
| Su-24M |  |  |  | 4 | frame | fallback |  |
| Su-24MR |  |  |  | 4 | frame | fallback |  |
| Su-27 | N-001 | 46 | 68 | 4 | frame | fallback | 5 |
| Su-30 |  |  |  | 4 | frame | fallback |  |
| Su-33 | N-001 | 46 | 68 | 4 | frame | fallback | 5 |
| Su-34 |  | 80 |  | 4 | frame | fallback |  |
| Tornado GR4 |  |  |  | 4 | frame | fallback |  |
| Tornado IDS |  |  |  | 4 | frame | fallback |  |
| Tu-142 |  |  |  | 4 | frame | fallback |  |
| Tu-160 |  |  |  | 4 | frame | fallback |  |
| Tu-22M3 |  |  |  | 4 | frame | fallback |  |
| Tu-95MS |  |  |  | 4 | frame | fallback |  |

### Ships

| Type | Radar (DCS) | Range used, km | DB range, km | Period, s | Kind | From | DCS scan_period |
|---|---|---|---|---|---|---|---|
| ALBATROS | CH MR-320 Topaz-2V air | 134 | 200 | 5 | rotating | table | 5 |
| BDK-775 | rezki search radar |  | 32 | 6 | rotating | fallback | 12 |
| CHAP_Project22160 | CH Pozitiv-ME 1.2 air |  | 80 | 6 | rotating | fallback | 1 |
| CHAP_Project22160_TorM2KM | CH Pozitiv-ME 1.2 air |  | 80 | 6 | rotating | fallback | 1 |
| CVN_71 | carrier search radar |  | 39 | 6 | rotating | table | 12 |
| CVN_72 | carrier search radar | 20 | 39 | 6 | rotating | table | 12 |
| CVN_73 | carrier search radar |  | 39 | 6 | rotating | table | 12 |
| CVN_75 | carrier search radar |  | 39 | 6 | rotating | table | 12 |
| CV_1143_5 | carrier search radar |  | 39 | 6 | rotating | fallback | 12 |
| Essex | carrier search radar |  | 39 | 6 | rotating | fallback | 12 |
| Forrestal | carrier search radar |  | 39 | 6 | rotating | table | 12 |
| KUZNECOW | carrier search radar |  | 39 | 5 | rotating | table | 12 |
| LHA_Tarawa | carrier search radar |  | 39 | 6 | rotating | table | 12 |
| La_Combattante_II |  | 13 |  | 6 | rotating | fallback |  |
| MOLNIYA |  | 19 |  | 5 | rotating | table |  |
| MOSCOW | S-300PS 40B6M tr navy | 107 | 160 | 5 | rotating | table | 1 |
| NEUSTRASH | neustrashimy search radar |  | 34 | 5 | rotating | table | 12 |
| PERRY | Patriot str | 20 | 260 | 5 | rotating | table | 1 |
| PIOTR | S-300PS 40B6M tr navy |  | 160 | 5 | rotating | table | 1 |
| REZKY | rezki search radar | 20 | 32 | 5 | rotating | table | 12 |
| Stennis | carrier search radar | 20 | 39 | 6 | rotating | table | 12 |
| TICONDEROG | AEGIS_search_radar |  | 260 | 4 | frame | table | 1 |
| Type_021_1 |  | 5 |  | 6 | rotating | fallback |  |
| Type_052B | 052B SAM SR |  | 230 | 4 | frame | table | 1 |
| Type_052C | 052C SAM STR |  | 260 | 4 | frame | table | 1 |
| Type_054A | 052B SAM SR |  | 230 | 5 | rotating | table | 1 |
| Type_071 | PLAN Search Radar B |  | 34 | 6 | rotating | table | 12 |
| Type_093 | 052B SAM TR |  | 74 | 6 | rotating | fallback | 1 |
| USS_Arleigh_Burke_IIa | AEGIS_search_radar |  | 260 | 4 | frame | table | 1 |
| VINSON | carrier search radar |  | 39 | 6 | rotating | table | 12 |
| ara_vdm | carrier search radar |  | 39 | 6 | rotating | fallback | 12 |
| atconveyor | carrier search radar |  | 39 | 6 | rotating | fallback | 12 |
| hms_invincible | carrier search radar | 20 | 39 | 6 | rotating | fallback | 12 |
| leander-gun-achilles | Patriot str |  | 260 | 6 | rotating | fallback | 1 |
| leander-gun-andromeda | Patriot str |  | 260 | 6 | rotating | fallback | 1 |
| leander-gun-ariadne | Patriot str |  | 260 | 6 | rotating | fallback | 1 |
| leander-gun-condell | Patriot str |  | 260 | 6 | rotating | fallback | 1 |
| leander-gun-lynch | Patriot str |  | 260 | 6 | rotating | fallback | 1 |
| santafe | 052B SAM TR |  | 74 | 6 | rotating | fallback | 1 |

### Ground radars

| Type | Radar (DCS) | Range used, km | DB range, km | Period, s | Kind | From | DCS scan_period |
|---|---|---|---|---|---|---|---|
| 1L13 EWR | 1L13 EWR |  | 300 | 10 | rotating | table | 1 |
| 2S6 Tunguska | 2S6 Tunguska | 15 | 18 | 1 | rotating | table | 1 |
| 55G6 EWR | 55G6 EWR | 267 | 400 | 10 | rotating | table | 1 |
| CHAP_IRISTSLM_STR | CH TRML-4D air | 80 | 120 | 4 | rotating | fallback | 1 |
| CHAP_PGZ09 | CH PGZ-09 air |  | 20 | 4 | rotating | fallback | 1 |
| CHAP_PantsirS1 | CH 2RL80 air | 24 | 36 | 4 | rotating | fallback | 2 |
| CHAP_TorM2 | CH Tor M2 air | 21 | 32 | 4 | rotating | fallback | 1 |
| Dog Ear radar | Dog Ear radar |  | 35 | 2 | rotating | table | 1 |
| FPS-117 | FPS-117 |  | 463 | 10 | rotating | fallback | 1 |
| FPS-117 Dome | FPS-117 |  | 463 | 10 | rotating | fallback | 1 |
| Gepard | Gepard | 10 | 15 | 1 | rotating | table | 1 |
| HEMTT_C-RAM_Phalanx | C_RAM_Phalanx | 13 | 20 | 4 | rotating | fallback | 1 |
| HQ-7_LN_P | HQ-7 TR |  | 20 | 4 | rotating | fallback | 1 |
| HQ-7_LN_SP | HQ-7 TR |  | 20 | 4 | rotating | fallback | 1 |
| HQ-7_STR_SP | HQ-7 SR |  | 30 | 4 | rotating | fallback | 1 |
| Hawk cwar | Hawk sr |  | 90 | 3 | rotating | table | 1 |
| Hawk sr | Hawk sr |  | 90 | 3 | rotating | table | 1 |
| Hawk tr | Hawk tr |  | 90 | 4 | rotating | fallback | 1 |
| Kub 1S91 str | Kub 1S91 str | 47 | 70 | 4 | rotating | table | 1 |
| NASAMS_Radar_MPQ64F1 | NASAMS_Radar_MPQ64F1 | 27 | 40 | 2 | rotating | table | 2 |
| Osa 9A33 ln | Osa 9A33 ln | 20 | 30 | 2 | rotating | table | 1 |
| P14_SR | P14_SR |  | 400 | 4 | rotating | fallback | 6 |
| Patriot ECS | Patriot str |  | 260 | 4 | rotating | fallback | 1 |
| Patriot str | Patriot str |  | 260 | 1 | frame | table | 1 |
| RD_75 | snr s-125 tr |  | 50 | 4 | rotating | fallback | 1 |
| RLS_19J6 | RLS_19J6 |  | 150 | 4 | rotating | fallback | 6 |
| RPC_5N62V | RPC S-200 TR |  | 400 | 4 | rotating | fallback | 1 |
| Roland ADS | Roland ADS |  | 12 | 1 | rotating | table | 1 |
| Roland Radar | Roland Radar |  | 35 | 1 | rotating | table | 1 |
| S-300PS 40B6M tr | S-300PS 40B6M tr | 107 | 160 | 4 | rotating | fallback | 1 |
| S-300PS 40B6MD sr | S-300PS 40B6MD sr | 107 | 120 | 4 | rotating | fallback | 1 |
| S-300PS 40B6MD sr_19J6 | RLS_19J6 |  | 150 | 4 | rotating | fallback | 6 |
| S-300PS 5H63C 30H6_tr | S-300PS 40B6M tr |  | 160 | 4 | rotating | fallback | 1 |
| S-300PS 64H6E sr | S-300PS 40B6MD sr | 107 | 120 | 12 | rotating | table | 1 |
| SA-11 Buk LN 9A310M1 | SA-11 Buk TR | 40 | 60 | 4 | rotating | fallback | 1 |
| SA-11 Buk SR 9S18M1 | SA-11 Buk SR 9S18M1 | 40 | 100 | 5 | rotating | table | 1 |
| SNR_75V | snr s-125 tr |  | 50 | 4 | rotating | fallback | 1 |
| SON_9 | son-9 tr |  | 24 | 4 | rotating | fallback | 2.5 |
| Silkworm_SR | Mys-M1_SR |  | 200 | 4 | rotating | fallback | 10 |
| Tor 9A331 | Tor 9A331 | 17 | 25 | 1 | rotating | table | 1 |
| ZSU-23-4 Shilka | ZSU-23-4 Shilka | 5 | 8 | 1 | frame | table | 1 |
| p-19 s-125 sr | p-19 s-125 sr | 33 | 80 | 10 | rotating | table | 5 |
| rapier_fsa_blindfire_radar | Tor 9A331 |  | 25 | 2 | rotating | table | 1 |
| rapier_fsa_launcher | Roland Radar |  | 35 | 2 | rotating | table | 1 |
| snr s-125 tr | snr s-125 tr | 33 | 50 | 4 | rotating | fallback | 1 |

### Helicopters

| Type | Radar (DCS) | Range used, km | DB range, km | Period, s | Kind | From | DCS scan_period |
|---|---|---|---|---|---|---|---|
| AH-64D |  |  |  | 4 | frame | fallback |  |
| AH-64D_BLK_II |  |  |  | 4 | frame | fallback |  |
| SH-60B |  |  |  | 4 | frame | fallback |  |


