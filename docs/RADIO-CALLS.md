# Radio calls for NEBO controllers

What to say on the radio as **NEBO**, situation by situation. The wording follows the
air-combat brevity words pilots know from DCS and real-world multi-service brevity, and ICAO
style for airfield traffic — simplified for DCS servers. Use it as a script until it comes
naturally; pilots care more that calls are short, in the same order every time, and on time.

`<…>` = fill in from the scope. *Italic lines* = what the pilot typically says.

---

## 1. Basics

**Order of every call:** who you call → who you are → the message.
> "Viper 1-1, NEBO, …"

**Numbers:**
- Bearings in three digits: "zero-seven-zero" (not "seventy").
- Altitudes in thousands: "two-four thousand" (24,000 ft), below 1,000 ft "five hundred".
- Ranges in miles (NM): "three-five" or "thirty-five".
- Frequencies as set: "two-five-one decimal zero".

**Where NEBO gives you the numbers:**
| You say | Take it from |
|---|---|
| BRAA (bearing, range, altitude, aspect) | right-drag the ruler from the fighter to the group (box shows it) |
| Bullseye | bullseye readout under the cursor / data block |
| Intercept heading ("snap") | pinned engagement line: ICPT point and HDG |
| Group altitude and speed | data block (altitude in hundreds of feet, speed, Mach) |
| Type | data block once identified (NCTR, RWR calls, DCS) |
| Frequencies | SRS tab / your channels (CH1–CH10) |
| Marshal slots, push times | ATC tab → carrier → Marshal |

**Readability:** speak after a short pause on the PTT, one call at a time, stop when a pilot
starts talking.

---

## 2. Coming on and going off station

**On station (on the common frequency):**
> "All players, NEBO is on station, frequency <251.0>. Bullseye is <name>. Picture follows."

**Radio check (answer to a pilot):**
> *"NEBO, Viper 1-1, radio check."*
> "Viper 1-1, NEBO, loud and clear." / "… readable, but broken."

**Off station:**
> "All players, NEBO off station in <five> minutes." … "NEBO off station, good hunting."

**Handing over to another controller:**
> "All players, NEBO, controller change. <Name> has the picture."

---

## 3. Check-in

*"NEBO, Viper 1-1, flight of two Vipers, airborne Incirlik, angels two-zero, four AMRAAMs
each, one hour of gas, checking in for CAP."*

**Answer:**
> "Viper 1-1, NEBO, radar contact, <bullseye 090 for 40>. Proceed CAP point <name>, angels
> two-four. Picture: two groups, …" (or "Picture clean.")

**Pilot not yet on your scope:**
> "Viper 1-1, NEBO, no radar contact. Say position."

**Assigning a package / frequency:**
> "Viper 1-1, you are package one. Push package frequency <256.0>, NEBO monitoring."

---

## 4. Picture

The **picture** is the whole air situation, in a fixed order: number of groups, the formation,
then each group from the most dangerous, by **bullseye**.

> "NEBO, picture, two groups, azimuth one-zero.
> North group, bullseye zero-eight-five, four-five, two-four thousand, hostile, two contacts.
> South group, bullseye zero-nine-five, four-five, two-zero thousand, hostile."

- **Azimuth** = groups side by side as seen from your fighters (range between them in miles).
- **Range** = groups one behind the other ("lead group … trail group …").
- **Wall** = three or more side by side.
- Add **FAST** (over ~600 kt or above Mach 1), **HIGH** (above 40,000 ft), **LOW** (below 5,000
  ft), **HEAVY** (three or more aircraft) when true.

**Nothing out there:**
> "NEBO, picture clean."

**Single group:**
> "NEBO, single group, bullseye zero-nine-zero, three-five, two-two thousand, track west,
> hostile."

---

## 5. BRAA calls to one flight

**BRAA** is from that flight's position: **B**earing, **R**ange, **A**ltitude, **A**spect.

> "Viper 1-1, NEBO, group BRAA zero-seven-zero, three-five, two-four thousand, hot, hostile."

**Aspect** (how the group flies relative to your fighter):
| Word | Group's nose |
|---|---|
| **HOT** | pointing at your fighter |
| **FLANK** (north/south…) | 30–70° off, crossing |
| **BEAM** (east/west…) | about 90°, crossing |
| **DRAG** (east/west…) | pointing away |

Update while they close: every ~10 miles, and at 20 and 10 miles.

---

## 6. Identifying (DECLARE)

*"NEBO, Viper 1-1, declare BRAA zero-seven-zero, three-five, two-four thousand."*

| Answer | Means |
|---|---|
| **HOSTILE** | enemy, may be engaged under your rules of engagement |
| **BANDIT** | known enemy aircraft, engagement not yet cleared |
| **BOGEY** | unknown |
| **FRIENDLY** | own side |
| **NEUTRAL** | neither side |
| **CLEAN** | nothing on the scope there |
| **FURBALL** | friends and enemies mixed together |

> "Viper 1-1, NEBO, hostile." / "… bogey, no factor." / "… friendly."

On a **manual-identification** server contacts start unknown: say **bogey** until NCTR or a
pilot's call identifies them, then:
> "NEBO, update: group bullseye zero-eight-five, four-five, now hostile, MiG-29s."

---

## 7. Commit, intercept, merge

**Sending fighters onto a group:**
> "Viper 1-1, NEBO, commit, group BRAA zero-seven-zero, four-zero, two-four thousand, hot,
> hostile."
*"Viper 1-1 committing."*

**Intercept heading** (the pinned engagement line's ICPT / HDG):
> "Viper 1-1, snap zero-seven-two, group BRAA zero-seven-eight, three-two, one-one thousand,
> hot, hostile."
- **SNAP** = turn to this heading now (urgent). **VECTOR** = steer this heading (routine).

**Closing ranges:**
> "Viper 1-1, group BRAA zero-seven-five, two-zero, hot." … "… one-zero, hot."

**Merged** (fighters and group in the same place):
> "Viper 1-1, NEBO, merged." — then keep calls short, only new threats.

**Splitting groups:**
> "Viper 1-1, group split, north group BRAA zero-six-zero, two-zero, hot; south group BRAA
> zero-eight-five, two-two, flank south."

**Group turned away:**
> "Viper 1-1, group drag east, three-five." (Let the pilot decide whether to chase.)

**Recommit or break off:**
> "Viper 1-1, NEBO, skate." (= leave this fight) / "… recommit, group BRAA …"

---

## 8. Threat and missile calls

**Threat to a flight** (a hostile group inside ~20 miles, pointing at them):
> "Viper 1-1, NEBO, threat, group BRAA three-six-zero, one-five, two-zero thousand, hot!"

**Missile in flight at a player** (NEBO's launch alarm, **Space** jumps to it):
> "Viper 1-1, missile, your <north>, from <bullseye 030 for 20>! Defend!"
Say it once, clearly, then stay quiet unless something new happens.

**Bandit closing on a tanker or AWACS:**
> "Texaco, NEBO, threat, group BRAA two-seven-zero, four-zero, hot, hostile. Recommend
> heading zero-niner-zero."

---

## 9. Surface-to-air threats

**A SAM site known (placed threat ring / F10 mark):**
> "All players, NEBO, SA-11, bullseye zero-six-zero, three-zero, mark on your F10 map.
> Avoid by twenty miles."

**From a pilot's RWR call** (*"Viper 1-1, mud spike, SA-6, three o'clock"*):
> "Viper 1-1, copy, SA-6 your three o'clock." — put the bearing line on the RWR ring; with two
> calls you get a fix:
> "All players, SA-6 now bullseye one-two-zero, two-five, ring on your F10 map."

---

## 10. Packages, strikers and support

**Strikers pushing:**
> "Hawg 2-1, NEBO, picture clean along your route, push."
> "Hawg 2-1, threat, group BRAA …, Viper 1-1 committing."

**Tanker:**
> "Viper 1-1, tanker Texaco, bullseye two-seven-zero, three-zero, angels two-two, TACAN
> five-one-X, frequency <251.75>."

**Fuel (pilot calls):** *JOKER* = time to finish up, *BINGO* = must go home now.
> "Viper 1-1, copy bingo. Home plate <Incirlik>, vector two-four-zero, one-two-zero miles.
> Picture clean along your route."

---

## 11. Handoffs and frequency changes

> "Viper 1-1, contact Incirlik Tower <252.0>." *"Tower <252.0>, Viper 1-1."*
> "Viper 1-1, push package frequency <256.0>."
> "Viper 1-1, NEBO is changing to <270.1>, check in there."

When **frequency is busy**, keep transmissions under five seconds; say "**standby**" to hold a
pilot and come back to them.

---

## 12. Airfield — ground

*"Akrotiri Ground, Ford 3-1, single Viper, parking 32, information Alpha, request taxi."*
> "Ford 3-1, Akrotiri Ground, taxi to runway two-eight via Bravo, Alpha. QNH one-zero-one-seven."
*Readback: "Taxi runway two-eight via Bravo, Alpha, QNH 1017, Ford 3-1."*

Hold short:
> "Ford 3-1, hold short runway two-eight, traffic on final."

---

## 13. Airfield — tower (departure and arrival)

**Line up and take off:**
> "Ford 3-1, Akrotiri Tower, runway two-eight, line up and wait."
> "Ford 3-1, wind two-eight-zero at eight, runway two-eight, cleared for take-off."
> "Ford 3-1, contact NEBO <251.0>." (after departure)

**Arrival:**
*"Akrotiri Tower, Colt 1-1, one-five miles east, information Alpha, for landing."*
> "Colt 1-1, Akrotiri Tower, runway one-zero, report five-mile final. QNH one-zero-one-seven."
> "Colt 1-1, number two, follow the Viper on four-mile final."
> "Colt 1-1, wind two-eight-zero at eight, runway one-zero, cleared to land."

**Go-around:**
> "Colt 1-1, go around, runway occupied." *"Going around, Colt 1-1."*

**ATIS** (NEBO sends airfield information to a pilot's screen — **ATIS…** in the ATC tab):
> "Akrotiri information Alpha: runway one-zero, wind two-eight-zero at eight, QNH one-zero-one-
> seven, TACAN one-zero-seven-X, ILS two-eight one-zero-niner decimal seven."

---

## 14. Approach (radar) — the 10 NM approach view

> "Colt 1-1, Akrotiri Approach, radar contact, two-five miles east. Descend three thousand,
> vector two-eight-four for the ILS runway two-eight."
> "Colt 1-1, one-two miles from touchdown, turn left heading two-niner-zero, cleared ILS
> runway two-eight, contact Tower <252.0>."

On the path (the profile shows the height against the 3° path):
> "Colt 1-1, slightly high, four miles." / "… on glide path."

---

## 15. Carrier

**Check-in with marshal** (ATC tab → carrier → **Marshal** shows slots and push times):
*"NEBO marshal, Hornet 3-1, two-zero miles, angels two-two, state six-point-four."*
> "Hornet 3-1, NEBO marshal, Case III recovery, expected BRC zero-six-four. Marshal mother's
> two-three-five radial, two-one DME, angels six. Expected approach time zero-three-five-zero.
> Altimeter two-niner-niner-two."
*Pilot reads back.*

**Case I** (day, good weather):
> "Hornet 3-1, Case I recovery, BRC zero-six-four, signal Charlie." (= come in and land)

**Approach (Case III):**
*"Hornet 3-1, commencing, angels six, state five-point-niner."*
> "Hornet 3-1, NEBO, radar contact, one-zero miles, fly bullseye… / on glidepath."
At ¾ mile:
> "Hornet 3-1, three-quarter mile, call the ball."
*"Hornet 3-1, Hornet ball, five-point-two."*
**LSO:**
> "Roger ball." … "Little power." … "Right for lineup." … "Wave off, wave off!" (foul deck or
> unsafe)

**After landing — the grade** (NEBO's LSO view grades each pass):
> "Hornet 3-1, OK, three-wire." / "… fair pass, a little high in the middle."

---

## 16. Emergencies

*"NEBO, Viper 1-2, mayday, mayday, mayday, engine fire, bullseye 210 for 30, angels 10."*
> "Viper 1-2, NEBO, copy mayday. Nearest field Akrotiri, two-four-zero, three-five miles.
> Say intentions."
Then tell everyone:
> "All players, NEBO, emergency in progress, keep this frequency clear."

**Pilot ejected** (Foothold CSAR missions):
> "All players, NEBO, Viper 1-2 ejected, bullseye two-one-zero, three-two. Rescue flights
> say status."

**Lost contact with a pilot:**
> "Viper 1-1, NEBO, how do you read?" … (twice) … "Viper 1-1, if you hear NEBO,
> squawk / say position on <guard 243.0>."

---

## 17. Words to keep handy

| Word | Meaning |
|---|---|
| AFFIRM / NEGATIVE | yes / no |
| ANGELS | altitude in thousands of feet |
| BOGEY DOPE | pilot asks for the nearest group |
| CLEAN | nothing on the scope |
| COMMIT | go and intercept |
| FADED | contact lost on the scope ("group bullseye …, faded") |
| JOKER / BINGO | fuel: finish up / go home now |
| MERGED | fighters and group together |
| NO FACTOR | not a threat to you |
| PICTURE | the whole air situation |
| POP-UP | new group appeared suddenly |
| PUSH | change frequency / start the mission leg |
| SKATE | leave the fight |
| SNAP / VECTOR | turn now to / steer |
| SPIKE | pilot's radar warning (from an aircraft) |
| MUD SPIKE | radar warning from a SAM site |
| STANDBY | wait, I will call you back |
| THREAT | a hostile group close and pointing at you |
| WILCO | will comply |

*NEBO by Kestrel's Nest Community. Phraseology simplified for DCS World; not for real-world
use.*
