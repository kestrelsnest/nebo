-- A JSON string literal (quotes, backslashes and control characters escaped), shared by every
-- part below: the collector runs as one chunk.
local function jstr(s)
    s = tostring(s):gsub('[%c"\\]', function(c)
        if c == '"' then return '\\"' end
        if c == "\\" then return "\\\\" end
        return string.format("\\u%04x", c:byte())
    end)
    return '"' .. s .. '"'
end

-- Georeference control grid: lat/lon of an n x n grid of DCS x/z points over a rectangle,
-- as "lat,lon;lat,lon;..." row-major (x outer, z inner).
function NEBO_geoGrid(x0, z0, x1, z1, n)
    local out = {}
    for i = 0, n - 1 do
        local x = x0 + (x1 - x0) * i / (n - 1)
        for j = 0, n - 1 do
            local z = z0 + (z1 - z0) * j / (n - 1)
            local lat, lon = coord.LOtoLL({ x = x, y = 0, z = z })
            out[#out + 1] = string.format("%.6f,%.6f", lat, lon)
        end
    end
    return table.concat(out, ";")
end

-- Magnetic variation (degrees, east positive) from DCS's own model for the mission date, on
-- a lat/lon grid: rows from lat0 north in steps, lon0 east in steps; "d,d,...;d,d,..." per row.
function NEBO_magvarGrid(lat0, lon0, step, nlat, nlon)
    local magvar = require("magvar")
    local rows = {}
    for i = 0, nlat - 1 do
        local row = {}
        for j = 0, nlon - 1 do
            row[j + 1] = string.format("%.2f", math.deg(magvar.get_mag_decl(lat0 + i * step, lon0 + j * step)))
        end
        rows[#rows + 1] = table.concat(row, ",")
    end
    return table.concat(rows, ";")
end

-- Terrain rows i0..i0+count-1 of a grid (x = x0 + i*step, z = z0 + j*step, j < nz).
-- Each row: "h,h,...|ddd..." (heights in m, surface types as digits); rows joined by "\n".
function NEBO_terrainRows(x0, z0, step, nx, nz, i0, count)
    local rows = {}
    for i = i0, math.min(i0 + count, nx) - 1 do
        local x = x0 + i * step
        local h, s = {}, {}
        for j = 0, nz - 1 do
            local p = { x = x, y = z0 + j * step }
            h[j + 1] = string.format("%d", land.getHeight(p))
            s[j + 1] = tostring(land.getSurfaceType(p) or 0)
        end
        rows[#rows + 1] = table.concat(h, ",") .. "|" .. table.concat(s)
    end
    return table.concat(rows, "\n")
end

-- Airfields (airdromes and helipads; ships move and are in the picture): static layout from
-- getRunways/getParking, built once and cached; owners re-read on every call. Returns the
-- "airfields" message JSON.
NEBO_airfields = (function()
    local layout = nil  -- { { ab = Airbase, json = "<static fields>" }, ... }

    local function build()
        layout = {}
        for _, ab in ipairs(world.getAirbases() or {}) do
            local desc = ab:getDesc() or {}
            local cat = desc.category
            if cat ~= Airbase.Category.SHIP then
                local p = ab:getPoint()
                local runways = {}
                local okR, rws = pcall(ab.getRunways, ab)
                for _, r in ipairs(okR and rws or {}) do
                    local rp = r.position or {}
                    runways[#runways + 1] = string.format('{"name":%s,"course":%.5f,"length":%.0f,"width":%.0f,"x":%.1f,"z":%.1f}',
                        jstr(r.Name or r.name or ""), r.course or 0, r.length or 0, r.width or 0, rp.x or 0, rp.z or 0)
                end
                local parking = {}
                local okP, spots = pcall(ab.getParking, ab)
                for _, s in ipairs(okP and spots or {}) do
                    local sp = s.vTerminalPos or {}
                    parking[#parking + 1] = string.format('[%d,%.1f,%.1f,%d]', s.Term_Index or 0, sp.x or 0, sp.z or 0, s.Term_Type or 0)
                end
                layout[#layout + 1] = {
                    ab = ab,
                    json = string.format('"id":%d,"name":%s,"category":%d,"x":%.1f,"z":%.1f,"runways":[%s],"parking":[%s]',
                        tonumber(ab:getID()) or 0, jstr(ab:getName()), cat or 0, p.x, p.z,
                        table.concat(runways, ","), table.concat(parking, ",")),
                }
            end
        end
    end

    return function()
        local ok, result = pcall(function()
            if not layout then build() end
            local out = {}
            for _, a in ipairs(layout) do
                local okC, side = pcall(a.ab.getCoalition, a.ab)
                out[#out + 1] = string.format('{%s,"coalition":%d}', a.json, okC and side or 0)
            end
            return '{"type":"airfields","airfields":[' .. table.concat(out, ",") .. "]}"
        end)
        return ok and result or ("ERR:" .. tostring(result))
    end
end)()

-- Pilot sync (data cartridges): what a coalition's pilots get besides threats — its live AWACS
-- and tanker groups with their frequencies (the Mission Editor's group frequency), its own
-- airdromes (position, elevation, DCS id for the tower frequencies) and its bullseye.
function NEBO_dtcData(coalitionName)
    local ok, result = pcall(function()
        local side = coalitionName == "red" and coalition.side.RED or coalition.side.BLUE
        local comms = {}
        local mc = env.mission.coalition and env.mission.coalition[coalitionName]
        for _, country in ipairs(mc and mc.country or {}) do
            for _, g in ipairs(country.plane and country.plane.group or {}) do
                local task = g.task
                if (task == "AWACS" or task == "Refueling") and tonumber(g.frequency) and Group.getByName(g.name) then
                    comms[#comms + 1] = string.format('{"name":%s,"task":%s,"mhz":%.3f,"mod":%d}',
                        jstr(g.name), jstr(task), tonumber(g.frequency), tonumber(g.modulation) or 0)
                end
            end
        end
        local fields = {}
        for _, ab in ipairs(world.getAirbases() or {}) do
            local desc = ab:getDesc() or {}
            local okC, owner = pcall(ab.getCoalition, ab)
            if desc.category == Airbase.Category.AIRDROME and okC and owner == side then
                local p = ab:getPoint()
                fields[#fields + 1] = string.format('{"id":%d,"name":%s,"x":%.1f,"z":%.1f,"alt":%.0f}',
                    tonumber(ab:getID()) or 0, jstr(ab:getName()), p.x, p.z, land.getHeight({ x = p.x, y = p.z }))
            end
        end
        local bulls = "null"
        local okB, ref = pcall(coalition.getMainRefPoint, side)
        if okB and ref then bulls = string.format('{"x":%.1f,"z":%.1f}', ref.x, ref.z) end
        return string.format('{"comms":[%s],"airfields":[%s],"bullseye":%s,"theatre":%s}',
            table.concat(comms, ","), table.concat(fields, ","), bulls, jstr(env.mission.theatre or ""))
    end)
    return ok and result or ("ERR:" .. tostring(result))
end

-- Lat/lon pairs "lat,lon;lat,lon;…" → DCS map metres "x,z;x,z;…" (pilot sync: picture contacts
-- carry lat/lon, cartridges map metres).
function NEBO_llToXz(list)
    local out = {}
    for lat, lon in tostring(list):gmatch("([%-%d%.]+),([%-%d%.]+)") do
        local p = coord.LLtoLO(tonumber(lat), tonumber(lon), 0)
        out[#out + 1] = string.format("%.0f,%.0f", p.x, p.z)
    end
    return table.concat(out, ";")
end

-- Controller command "message_unit": an on-screen text box (not chat) for one player's
-- aircraft. Only a unit of the controller's own coalition with a player in it. The text
-- arrives as a plain string argument (the hook never runs controller text as code).
-- Returns "ok" or the reason it was not shown.
function NEBO_messageUnit(unitId, text, seconds, coalitionName)
    local side = coalitionName == "blue" and coalition.side.BLUE or coalitionName == "red" and coalition.side.RED or nil
    if not side then return "unknown coalition" end
    for _, gcat in ipairs({ Group.Category.AIRPLANE, Group.Category.HELICOPTER }) do
        for _, g in ipairs(coalition.getGroups(side, gcat) or {}) do
            for _, u in ipairs(g:getUnits() or {}) do
                -- getID() returns a string in DCS's mission env ("4940"): compare as numbers.
                if u:isExist() and tonumber(u:getID()) == unitId then
                    if not u:getPlayerName() then return "no player in that aircraft" end
                    trigger.action.outTextForUnit(unitId, text, seconds, false)
                    return "ok"
                end
            end
        end
    end
    return "not an aircraft of your coalition (or it is gone)"
end

-- Controller command "carrier_lights": checks that the unit is a carrier (a type NEBO_carrier
-- knows) of the controller's own coalition. The hook then sets the mode in the mission env
-- (a_set_carrier_illumination_mode lives there). Returns "ok" or the reason.
function NEBO_carrierLightsCheck(unitId, coalitionName)
    local side = coalitionName == "blue" and coalition.side.BLUE or coalitionName == "red" and coalition.side.RED or nil
    if not side then return "unknown coalition" end
    for _, g in ipairs(coalition.getGroups(side, Group.Category.SHIP) or {}) do
        for _, u in ipairs(g:getUnits() or {}) do
            if u:isExist() and tonumber(u:getID()) == unitId then
                if NEBO_carrier and NEBO_carrier.types and not NEBO_carrier.types[u:getTypeName()] then return "not a carrier" end
                return "ok"
            end
        end
    end
    return "not a ship of your coalition (or it is gone)"
end

-- Controller command "identify": the unit must be an existing unit of the controller's enemy
-- (a radar contact); the hook keeps the identity. Returns "ok" or the reason.
local function enemyUnitIds(side)
    local enemy = side == coalition.side.BLUE and coalition.side.RED or coalition.side.BLUE
    local ids = {}
    for cat = 0, 3 do
        for _, g in ipairs(coalition.getGroups(enemy, cat) or {}) do
            for _, u in ipairs(g:getUnits() or {}) do
                if u:isExist() then ids[tonumber(u:getID())] = true end
            end
        end
    end
    return ids
end

function NEBO_identifyCheck(unitId, coalitionName)
    local side = coalitionName == "blue" and coalition.side.BLUE or coalitionName == "red" and coalition.side.RED or nil
    if not side then return "unknown coalition" end
    return enemyUnitIds(side)[unitId] and "ok" or "not an enemy unit (or it is gone)"
end

-- Of the given unit ids ("12,345,…"), the ones still alive as enemies of the side, same form.
function NEBO_identifyAlive(idList, coalitionName)
    local side = coalitionName == "blue" and coalition.side.BLUE or coalition.side.RED
    local alive, out = enemyUnitIds(side), {}
    for id in tostring(idList):gmatch("%d+") do
        if alive[tonumber(id)] then out[#out + 1] = id end
    end
    return table.concat(out, ",")
end

-- F10 map marks. Controllers add marks for their own coalition (commands "mark_add"
-- and "mark_remove", text and threat type as plain string arguments) and can remove only the
-- ones NEBO made for their coalition; NEBO_marks lists every mark each side can see.
NEBO_markReg = NEBO_markReg or { nextId = 880000, marks = {} }  -- id -> { side, threat }
NEBO_markJson = {}  -- mark id -> { key, json }: see NEBO_marks

local MARK_SIDES = { blue = coalition.side.BLUE, red = coalition.side.RED }

local function markJstr(s)
    s = tostring(s):gsub('[%c"\\]', function(c)
        if c == '"' then return '\\"' end
        if c == "\\" then return "\\\\" end
        return string.format("\\u%04x", c:byte())
    end)
    return '"' .. s .. '"'
end

-- Returns "ok:<mark id>" or the reason the mark was not made.
function NEBO_markAdd(x, z, text, threat, coalitionName)
    local side = MARK_SIDES[coalitionName]
    if not side then return "unknown coalition" end
    local used = {}
    for _, m in ipairs(world.getMarkPanels() or {}) do used[m.idx] = true end
    local reg = NEBO_markReg
    local drawn = NEBO_areaDrawn and NEBO_areaDrawn.live or {}
    while used[reg.nextId] or drawn[reg.nextId] do reg.nextId = reg.nextId + 1 end
    local id = reg.nextId
    reg.nextId = id + 1
    local pos = { x = x, y = land.getHeight({ x = x, y = z }), z = z }
    -- Not read-only: the coalition's players can delete (or correct) it on their F10 map.
    trigger.action.markToCoalition(id, text, pos, side, false)
    reg.marks[id] = { side = side, threat = threat ~= "" and threat or nil }
    return "ok:" .. id
end

function NEBO_markRemove(id, coalitionName)
    local m = NEBO_markReg.marks[id]
    if not m or m.side ~= MARK_SIDES[coalitionName] then return "not a NEBO mark of your coalition" end
    trigger.action.removeMark(id)
    NEBO_markReg.marks[id] = nil
    return "ok"
end

-- Controllers' areas of operations shown to their side's pilots (area_set with f10 flags): each a
-- read-only polygon in the controller's colour, lightly filled, and the callsign at its north
-- corner. Removes the controller's previous drawings (ids "a,b,..."), draws spec (areas split by
-- '|', each "lat,lon;..."); returns "ok:<new ids>" or the reason.
NEBO_areaDrawn = NEBO_areaDrawn or { nextId = 890000, live = {} }

function NEBO_areaDraw(coalitionName, old, color, label, spec)
    local side = MARK_SIDES[coalitionName]
    if not side then return "unknown coalition" end
    local reg = NEBO_areaDrawn
    for id in tostring(old):gmatch("%d+") do
        id = tonumber(id)
        pcall(trigger.action.removeMark, id)
        reg.live[id] = nil
    end
    if spec == "" then return "ok:" end
    local r, g, b = tostring(color):match("^#(%x%x)(%x%x)(%x%x)$")
    r, g, b = tonumber(r or "B0", 16) / 255, tonumber(g or "B0", 16) / 255, tonumber(b or "B0", 16) / 255
    local line, fill, clear = { r, g, b, 0.9 }, { r, g, b, 0.12 }, { 0, 0, 0, 0 }
    local used = {}
    for _, m in ipairs(world.getMarkPanels() or {}) do used[m.idx] = true end
    local function newId()
        while used[reg.nextId] or reg.live[reg.nextId] or NEBO_markReg.marks[reg.nextId] do reg.nextId = reg.nextId + 1 end
        local id = reg.nextId
        reg.nextId = id + 1
        return id
    end
    local ids, err = {}, nil
    for area in spec:gmatch("[^|]+") do
        local pts, top = {}, nil
        for lat, lon in area:gmatch("(-?[%d.]+),(-?[%d.]+)") do
            local p = coord.LLtoLO(tonumber(lat), tonumber(lon), 0)
            local v = { x = p.x, y = 0, z = p.z }
            pts[#pts + 1] = v
            if not top or v.x > top.x then top = v end  -- north is +x
        end
        if #pts >= 3 then
            local id = newId()
            local args = { 7, side, id }  -- 7: freeform polygon
            for _, v in ipairs(pts) do args[#args + 1] = v end
            args[#args + 1] = line
            args[#args + 1] = fill
            args[#args + 1] = 1      -- solid line
            args[#args + 1] = true   -- read-only: pilots cannot delete it
            args[#args + 1] = label
            local ok, e = pcall(trigger.action.markupToAll, unpack(args))
            if ok then ids[#ids + 1] = id; reg.live[id] = true else err = tostring(e) end
            if label ~= "" then
                local tid = newId()
                ok, e = pcall(trigger.action.textToAll, side, tid, top, line, clear, 14, true, label)
                if ok then ids[#ids + 1] = tid; reg.live[tid] = true else err = tostring(e) end
            end
        end
    end
    if err and #ids == 0 then return "not drawn: " .. err end
    return "ok:" .. table.concat(ids, ",")
end

-- Per side one line {"type":"marks","coalition","marks":[{id,text,x,z,lat,lon,time,author,
-- nebo,threat}]}: the marks visible to that coalition (its own, and ones for all), at most 300.
function NEBO_marks()
    local ok, result = pcall(function()
        local panels = world.getMarkPanels() or {}
        local lines = {}
        -- Each mark's JSON is kept and rebuilt only when the mark changes (Foothold missions hold
        -- many marks; rebuilding all of them for both sides every 5 s cost up to 90 ms).
        local cache = NEBO_markJson
        local function markJson(m)
            local reg = NEBO_markReg.marks[m.idx]
            local key = string.format("%s|%.1f|%.1f|%s|%s", tostring(m.text), m.pos.x, m.pos.z, tostring(m.time),
                reg and (reg.threat or "nebo") or "")
            local c = cache[m.idx]
            if c and c.key == key then return c.json end
            local author = m.author
            if m.initiator then
                local okN, pn = pcall(m.initiator.getPlayerName, m.initiator)
                if okN and pn then author = pn end
            end
            local lat, lon = coord.LOtoLL(m.pos)
            local json = string.format('{"id":%d,"text":%s,"x":%.1f,"z":%.1f,"lat":%.6f,"lon":%.6f,"time":%.0f,"author":%s,"nebo":%s,"threat":%s}',
                m.idx, markJstr(tostring(m.text or ""):sub(1, 200)), m.pos.x, m.pos.z, lat, lon, tonumber(m.time) or 0,
                author and markJstr(author) or "null", reg and "true" or "false",
                reg and reg.threat and markJstr(reg.threat) or "null")
            cache[m.idx] = { key = key, json = json }
            return json
        end
        -- Controllers' areas on the F10 map (NEBO_areaDraw) are drawings, not marks: left out.
        local drawn = NEBO_areaDrawn and NEBO_areaDrawn.live or {}
        for name, side in pairs(MARK_SIDES) do
            local out = {}
            for _, m in ipairs(panels) do
                local mside = tonumber(m.coalition) or -1
                if (mside == side or mside == -1) and type(m.pos) == "table" and not drawn[m.idx] and #out < 300 then
                    out[#out + 1] = markJson(m)
                end
            end
            lines[#lines + 1] = string.format('{"type":"marks","coalition":"%s","marks":[%s]}', name, table.concat(out, ","))
        end
        -- Forget NEBO marks someone removed in the game, and the JSON kept for gone marks.
        local live = {}
        for _, m in ipairs(panels) do live[m.idx] = true end
        for id in pairs(NEBO_markReg.marks) do if not live[id] then NEBO_markReg.marks[id] = nil end end
        for id in pairs(cache) do if not live[id] then cache[id] = nil end end
        return table.concat(lines, "\n")
    end)
    return ok and result or ("ERR:" .. tostring(result))
end

-- Weather for ATIS: the mission's weather plus, per airfield, the live wind 10 m above the
-- field, the temperature there and QNH (local pressure reduced to sea level with the ISA
-- lapse rate, so it follows the mission's pressure systems). Returns the "weather" message.
function NEBO_weather()
    local ok, result = pcall(function()
        local w = env.mission.weather or {}
        local clouds, fog, vis = w.clouds or {}, w.fog or {}, w.visibility or {}
        local d = env.mission.date or {}
        local fields = {}
        for _, ab in ipairs(world.getAirbases() or {}) do
            local desc = ab:getDesc() or {}
            -- Airdromes only: helipads and FARPs (a few hundred) cost sim-thread time for no ATIS.
            if desc.category == Airbase.Category.AIRDROME then
                local p = ab:getPoint()
                local wind = atmosphere.getWind({ x = p.x, y = p.y + 10, z = p.z })
                local t, pa = atmosphere.getTemperatureAndPressure({ x = p.x, y = p.y + 2, z = p.z })
                local speed = math.sqrt(wind.x * wind.x + wind.z * wind.z)
                -- The vector is where the air goes; wind is named for where it comes from.
                local from = (math.deg(math.atan2(-wind.z, -wind.x)) + 360) % 360
                local qnh = pa and (pa / 100) * (1 - 0.0065 * (p.y + 2) / 288.15) ^ -5.25588 or 0
                fields[#fields + 1] = string.format('{"id":%d,"elevation":%.0f,"windFrom":%.0f,"windMs":%.1f,"tempC":%.1f,"qnhHpa":%.1f}',
                    tonumber(ab:getID()) or 0, p.y, from, speed, (t or 288.15) - 273.15, qnh)
            end
        end
        return string.format('{"type":"weather","date":"%04d-%02d-%02d","time":%.0f,"visibility":%.0f,' ..
            '"fog":{"on":%s,"visibility":%.0f,"thickness":%.0f},"dust":{"on":%s,"density":%.0f},' ..
            '"clouds":{"base":%.0f,"thickness":%.0f,"density":%.0f,"preset":%s,"precipitation":%d},"airfields":[%s]}',
            d.Year or 0, d.Month or 0, d.Day or 0, timer.getAbsTime(), vis.distance or 0,
            w.enable_fog and "true" or "false", fog.visibility or 0, fog.thickness or 0,
            w.enable_dust and "true" or "false", w.dust_density or 0,
            clouds.base or 0, clouds.thickness or 0, clouds.density or 0,
            clouds.preset and string.format("%q", clouds.preset) or "null", clouds.iprecptns or 0,
            table.concat(fields, ","))
    end)
    return ok and result or ("ERR:" .. tostring(result))
end

-- Missiles in flight, from DCS's shot events: { weapon, id, side, shooter, type, t }. The
-- handler is registered once per scripting state (the collector is injected again only
-- into a fresh state, after a mission change); entries go when the weapon stops existing.
NEBO_missiles = (function()
    local live, nextId = {}, 1
    local handler = {}
    function handler:onEvent(e)
        pcall(function()
            if e.id ~= world.event.S_EVENT_SHOT or not e.weapon then return end
            local w = e.weapon
            local desc = w:getDesc()
            if not desc or desc.category ~= Weapon.Category.MISSILE then return end
            local shooter, group, from = nil, nil, nil
            if e.initiator and e.initiator.getID then
                local okS, sid = pcall(e.initiator.getID, e.initiator)
                shooter = okS and tonumber(sid) or nil
                -- the group too: a SAM's launcher fires, its tracking radar guides
                local okG, g = pcall(e.initiator.getGroup, e.initiator)
                if okG and g then
                    local okI, gid = pcall(g.getID, g)
                    group = okI and tonumber(gid) or nil
                end
                -- A ground launcher's position: the target's side gets a launch mark there
                -- (NEBO_launches; the hook makes it a shared threat).
                local okD, udesc = pcall(e.initiator.getDesc, e.initiator)
                if okD and udesc and udesc.category == Unit.Category.GROUND_UNIT then
                    local okP, p = pcall(e.initiator.getPoint, e.initiator)
                    if okP and p then from = { x = p.x, z = p.z } end
                end
            end
            if #live >= 300 then table.remove(live, 1) end
            live[#live + 1] = { weapon = w, id = nextId, side = w:getCoalition(), shooter = shooter,
                group = group, from = from, type = w:getTypeName(), t = timer.getTime() }
            nextId = nextId + 1
        end)
    end
    if NEBO_missileHandler then world.removeEventHandler(NEBO_missileHandler) end
    NEBO_missileHandler = handler
    world.addEventHandler(handler)
    return live
end)()

-- Ground launches at the other side, found while the pictures are built: "side,group,x,z"
-- (side = the target's). The hook takes them every 0.7 s and keeps one launch mark per group.
NEBO_launches = NEBO_launches or {}

function NEBO_takeLaunches()
    local q = NEBO_launches
    if #q == 0 then return "" end
    local out = table.concat(q, "\n")
    for i = #q, 1, -1 do q[i] = nil end
    return out
end

-- Carriers: the types NEBO treats as carriers, their beacons from the mission's group tasks
-- (TACAN, ICLS, Link 4, ACLS; read once), the aircraft near each side's carriers (refreshed
-- with every picture, read 10 times a second by NEBO_lso) and landings on them
-- (S_EVENT_LAND, kept a minute for the pictures).
NEBO_carrier = (function()
    local TYPES = { CVN_70 = true, CVN_71 = true, CVN_72 = true, CVN_73 = true, CVN_75 = true, Stennis = true,
        Forrestal = true, CV_1143_5 = true, KUZNECOW = true, LHA_Tarawa = true, hms_invincible = true, Essex = true }
    local M = { types = TYPES, near = {}, events = {}, nextEvent = 1 }
    local beacons = nil

    local function readBeacons()
        beacons = {}
        for _, side in pairs(env.mission.coalition or {}) do
            for _, country in ipairs(side.country or {}) do
                for _, g in ipairs((country.ship or {}).group or {}) do
                    for _, u in ipairs(g.units or {}) do
                        if TYPES[u.type] then
                            beacons[u.unitId] = beacons[u.unitId] or {}
                            beacons[u.unitId].radio = u.frequency or g.frequency
                        end
                    end
                    for _, p in ipairs((g.route or {}).points or {}) do
                        local tasks = p.task and p.task.params and p.task.params.tasks or {}
                        for _, t in pairs(tasks) do
                            local a = t.params and t.params.action
                            local ap = a and a.params
                            if ap and ap.unitId then
                                local b = beacons[ap.unitId] or {}
                                beacons[ap.unitId] = b
                                if a.id == "ActivateBeacon" then
                                    b.tacan = string.format("%d%s %s", ap.channel or 0, ap.modeChannel or "X", ap.callsign or "")
                                elseif a.id == "ActivateICLS" then
                                    b.icls = ap.channel
                                elseif a.id == "ActivateLink4" then
                                    b.link4 = ap.frequency
                                elseif a.id == "ActivateACLS" then
                                    b.acls = true
                                end
                            end
                        end
                    end
                end
            end
        end
    end

    function M.beacons(id)
        if not beacons then pcall(readBeacons) end
        return (beacons or {})[id] or {}
    end

    local handler = {}
    function handler:onEvent(e)
        pcall(function()
            if e.id ~= world.event.S_EVENT_LAND or not e.initiator or not e.place then return end
            local okU, ship = pcall(e.place.getUnit, e.place)
            if not okU or not ship then ship = e.place end
            local okT, t = pcall(ship.getTypeName, ship)
            if not okT or not TYPES[t] then return end
            local u = e.initiator
            local p, cp = u:getPoint(), ship:getPoint()
            M.events[#M.events + 1] = { id = M.nextEvent, kind = "trap", unit = tonumber(u:getID()), carrier = tonumber(ship:getID()),
                side = u:getCoalition(), t = timer.getTime(), x = p.x, z = p.z, cx = cp.x, cz = cp.z }
            M.nextEvent = M.nextEvent + 1
            if #M.events > 50 then table.remove(M.events, 1) end
        end)
    end
    if NEBO_carrierHandler then world.removeEventHandler(NEBO_carrierHandler) end
    NEBO_carrierHandler = handler
    world.addEventHandler(handler)
    return M
end)()

-- Analytics: DCS events that involve players (spawns, takeoffs, landings, kills either way,
-- crashes, ejections, deaths, leaving a unit), queued here and drained by the hook once a
-- second (NEBO_analyticsDrain) into the server's analytics log. Players are named here; the
-- hook replaces names with {id, name} (id = a keyed hash of the UCID). No positions are kept.
NEBO_analytics = (function()
    local queue = {}
    local E = world.event

    local SIDE = { [0] = "neutral", [1] = "red", [2] = "blue" }

    -- What an object is, for kill statistics: air, helo, sam, ground, infantry, ship, structure.
    local function categoryOf(obj)
        local okC, cat = pcall(Object.getCategory, obj)
        if not okC then return "unknown" end
        if cat == Object.Category.STATIC then return "structure" end
        if cat == Object.Category.SCENERY then return "scenery" end
        if cat ~= Object.Category.UNIT then return "other" end
        local okD, desc = pcall(obj.getDesc, obj)
        local c = okD and desc and desc.category
        if c == 0 then return "air" end
        if c == 1 then return "helo" end
        if c == 3 then return "ship" end
        local function has(attr) local ok, v = pcall(obj.hasAttribute, obj, attr) return ok and v end
        if has("SAM related") or has("AAA") then return "sam" end
        if has("Infantry") then return "infantry" end
        return "ground"
    end

    -- {"type","side","player","category"} of a unit/static, or null.
    local function who(obj)
        if not obj then return "null", nil end
        local okE, exists = pcall(obj.isExist, obj)
        local okT, t = pcall(obj.getTypeName, obj)
        local okS, s = pcall(obj.getCoalition, obj)
        local okP, p = pcall(function() return obj.getPlayerName and obj:getPlayerName() or nil end)
        local player = okP and p or nil
        return string.format('{"type":%s,"side":%s,"player":%s,"category":%s}',
            jstr(okT and t or "?"), jstr(SIDE[okS and s or 0] or "neutral"),
            player and jstr(player) or "null", jstr(categoryOf(obj))), player, okS and s or nil
    end

    local function placeName(place)
        if not place then return "null" end
        local ok, n = pcall(place.getName, place)
        return ok and n and jstr(n) or "null"
    end

    local KINDS = {
        [E.S_EVENT_BIRTH] = "spawn", [E.S_EVENT_TAKEOFF] = "takeoff", [E.S_EVENT_LAND] = "landing",
        [E.S_EVENT_CRASH] = "crash", [E.S_EVENT_EJECTION] = "eject", [E.S_EVENT_PILOT_DEAD] = "pilot_dead",
        [E.S_EVENT_PLAYER_LEAVE_UNIT] = "leave_unit", [E.S_EVENT_KILL] = "kill",
    }

    local handler = {}
    function handler:onEvent(e)
        pcall(function()
            local kind = KINDS[e.id]
            if not kind then return end
            local unit, unitPlayer, unitSide = who(e.initiator)
            if kind == "kill" then
                local target, targetPlayer, targetSide = who(e.target)
                if not unitPlayer and not targetPlayer then return end  -- AI against AI: not kept
                local weapon = e.weapon_name
                if not weapon and e.weapon then
                    local okW, w = pcall(e.weapon.getTypeName, e.weapon)
                    weapon = okW and w or nil
                end
                local friendly = unitSide and targetSide and unitSide == targetSide
                queue[#queue + 1] = string.format('{"kind":"kill","t":%.1f,"unit":%s,"target":%s,"weapon":%s,"friendly":%s}',
                    timer.getTime(), unit, target, weapon and jstr(weapon) or "null", friendly and "true" or "false")
                return
            end
            if not unitPlayer then return end  -- only players' sorties
            local extra = ""
            if kind == "takeoff" or kind == "landing" then extra = ',"place":' .. placeName(e.place) end
            queue[#queue + 1] = string.format('{"kind":%s,"t":%.1f,"unit":%s%s}', jstr(kind), timer.getTime(), unit, extra)
        end)
        if #queue > 2000 then table.remove(queue, 1) end
    end
    if NEBO_analyticsHandler then world.removeEventHandler(NEBO_analyticsHandler) end
    NEBO_analyticsHandler = handler
    world.addEventHandler(handler)

    return { drain = function()
        if #queue == 0 then return "" end
        local out = table.concat(queue, "\n")
        queue = {}
        return out
    end }
end)()

function NEBO_analyticsDrain()
    local ok, result = pcall(NEBO_analytics.drain)
    return ok and result or ""
end

-- Flights (controller workflow, DLL 0.17): players' aircraft spawning, taking off, landing and
-- going away, for the hook's flights channel (who owns which pilot). One line per event, fields
-- separated by tabs: kind, unit id, runtime id (unit.id_, what SRS calls the unitId), group id,
-- group name, player, type, coalition, airfield id, airfield name, mission time. Kinds: birth,
-- air (already flying when NEBO started), ground (already on the ground), takeoff, land, gone.
-- Drained by the hook every half second (NEBO_flightsDrain); NEBO_flightsSeed lists the players
-- already in aircraft when the hook starts or the collector is re-injected.
NEBO_flights = (function()
    local queue = {}
    local players = {}  -- unit id -> true while a player sits in it (dead/leave events may lack the name)
    local E = world.event
    local KIND = {
        [E.S_EVENT_BIRTH] = "birth", [E.S_EVENT_TAKEOFF] = "takeoff", [E.S_EVENT_LAND] = "land",
        [E.S_EVENT_PLAYER_LEAVE_UNIT] = "gone", [E.S_EVENT_DEAD] = "gone", [E.S_EVENT_CRASH] = "gone",
        [E.S_EVENT_EJECTION] = "gone", [E.S_EVENT_PILOT_DEAD] = "gone",
    }

    local function clean(s) return (tostring(s or ""):gsub("[\t\r\n]", " ")) end

    -- The airfield a unit on the ground is at: the nearest within 5 km, else none.
    local function nearestField(u)
        local okP, p = pcall(u.getPoint, u)
        if not okP or not p then return nil end
        local best, bestD = nil, 5000
        for _, ab in ipairs(world.getAirbases() or {}) do
            local okA, q = pcall(ab.getPoint, ab)
            local okD, desc = pcall(ab.getDesc, ab)
            if okA and q and not (okD and desc and desc.category == Airbase.Category.SHIP) then
                local d = math.sqrt((q.x - p.x) ^ 2 + (q.z - p.z) ^ 2)
                if d < bestD then best, bestD = ab, d end
            end
        end
        return best
    end

    local function line(kind, u, place)
        local ok, l = pcall(function()
            local g = u:getGroup()
            local okN, player = pcall(u.getPlayerName, u)
            local fieldId, fieldName = 0, ""
            if place then
                local okI, id = pcall(place.getID, place)
                local okM, name = pcall(place.getName, place)
                fieldId, fieldName = okI and tonumber(id) or 0, okM and name or ""
            end
            return table.concat({ kind, tostring(u:getID()), tostring(tonumber(u.id_) or 0), tostring(g and g:getID() or 0),
                clean(g and g:getName()), clean(okN and player), clean(u:getTypeName()), tostring(u:getCoalition()),
                tostring(fieldId), clean(fieldName), string.format("%.1f", timer.getTime()) }, "\t")
        end)
        return ok and l or nil
    end

    local handler = {}
    function handler:onEvent(e)
        pcall(function()
            local kind = KIND[e.id]
            local u = e.initiator
            if not kind or not u then return end
            local okI, id = pcall(u.getID, u)
            if not okI or not id then return end
            local okP, player = pcall(u.getPlayerName, u)
            if kind == "gone" then
                if not players[id] then return end
                players[id] = nil
                queue[#queue + 1] = string.format("gone\t%d", id)
                return
            end
            if not (okP and player) then return end
            players[id] = true
            local place = e.place
            if kind == "birth" and not place then place = nearestField(u) end
            queue[#queue + 1] = line(kind, u, place)
        end)
        if #queue > 500 then table.remove(queue, 1) end
    end
    if NEBO_flightsHandler then world.removeEventHandler(NEBO_flightsHandler) end
    NEBO_flightsHandler = handler
    world.addEventHandler(handler)

    return {
        drain = function()
            if #queue == 0 then return "" end
            local out = table.concat(queue, "\n")
            queue = {}
            return out
        end,
        seed = function()
            local out = {}
            for side = 1, 2 do
                for _, u in ipairs(coalition.getPlayers(side) or {}) do
                    local okI, id = pcall(u.getID, u)
                    local okA, air = pcall(u.inAir, u)
                    if okI and id then
                        players[id] = true
                        local l = line(okA and air and "air" or "ground", u, not (okA and air) and nearestField(u) or nil)
                        if l then out[#out + 1] = l end
                    end
                end
            end
            return table.concat(out, "\n")
        end,
    }
end)()

function NEBO_flightsDrain()
    local ok, result = pcall(NEBO_flights.drain)
    return ok and result or ""
end

function NEBO_flightsSeed()
    local ok, result = pcall(NEBO_flights.seed)
    return ok and result or ""
end

-- NEBO collector. Injected into the mission scripting env ('server' state) by
-- NEBO-hook.lua; defines NEBO_collect(), which returns the blue picture JSON,
-- a newline, and the red picture JSON. Each side gets its own units plus only
-- the enemy units its radars detect (fog of war).
NEBO_collect = (function()
    local SIDES = { { name = "blue", side = coalition.side.BLUE }, { name = "red", side = coalition.side.RED } }
    local CATEGORY = { [0] = "airplane", [1] = "helicopter", [2] = "ground", [3] = "ship", [4] = "structure" }
    local GROUP_CATEGORIES = { Group.Category.AIRPLANE, Group.Category.HELICOPTER, Group.Category.GROUND, Group.Category.SHIP }

    local function jnum(n, fmt)
        if type(n) ~= "number" or n ~= n or n == math.huge or n == -math.huge then return "null" end
        return string.format(fmt, n)
    end

    -- Radar transmitting. Only units with a radar sensor are asked; getRadar can
    -- raise on some unit types, so it is guarded.
    local function isEmitting(u)
        local ok, hasRadar = pcall(u.hasSensors, u, Unit.SensorType.RADAR)
        if not ok or not hasRadar then return false end
        local ok2, active = pcall(u.getRadar, u)
        return ok2 and active == true
    end

    -- Kinematics shared by own and detected units. A moving unit's heading is taken from its
    -- velocity (its track): getPosition builds four tables per call, the largest share of the
    -- garbage a picture makes on a big server (perf probe, 2026-10-22).
    -- back: seconds to wind the position back along the velocity (a radar plot painted then).
    local function kinematics(u, back)
        local p = u:getPoint()
        local v = u:getVelocity()
        if back and back > 0 then p = { x = p.x - v.x * back, y = p.y - v.y * back, z = p.z - v.z * back } end
        local lat, lon, alt = coord.LOtoLL(p)
        local spd = math.sqrt(v.x * v.x + v.y * v.y + v.z * v.z)
        local hdg
        if spd > 2 then
            hdg = math.deg(math.atan2(v.z, v.x))
        else
            local pos = u:getPosition()
            hdg = math.deg(math.atan2(pos.x.z, pos.x.x))
        end
        if hdg < 0 then hdg = hdg + 360 end
        return string.format('"lat":%s,"lon":%s,"alt":%s,"hdg":%s,"spd":%s,"vs":%s,"emitter":%s',
            jnum(lat, "%.6f"), jnum(lon, "%.6f"), jnum(p.y, "%.1f"), jnum(hdg, "%.1f"), jnum(spd, "%.1f"),
            jnum(v.y, "%.1f"), isEmitting(u) and "true" or "false")
    end

    local function unitCategory(u)
        local desc = u:getDesc()
        return CATEGORY[desc and desc.category] or "unknown"
    end

    -- The unchanging part of an own unit's JSON (id, names, type, category, side), kept per unit
    -- and rebuilt only when its player changes (a player taking or leaving a slot).
    local prefixes = {}

    local function ownUnit(u, sideName)
        local id = u:getID()
        local player = u:getPlayerName()
        local c = prefixes[id]
        if not c or c.player ~= player then
            local g = u:getGroup()
            c = { player = player, json = string.format('{"id":%d,"name":%s,"type":%s,"callsign":%s,"player":%s,"group":%s,"category":%s,"coalition":%s,"detected":false,',
                id, jstr(u:getName()), jstr(u:getTypeName()), jstr(u:getCallsign() or ""),
                player and jstr(player) or "null", jstr(g and g:getName() or ""),
                jstr(unitCategory(u)), jstr(sideName)) }
            prefixes[id] = c
        end
        return c.json .. kinematics(u) .. "}"
    end

    -- identification = "manual" (NEBO.lua): contacts never carry their type; the controllers
    -- identify them (NCTR, pilots' calls, "identify" command). "auto": the type when DCS says it is identified.
    local manualId = (NEBO_settings or {}).identification == "manual"

    -- A radar contact: no name, callsign or player; type only when DCS says it is identified.
    local function contact(u, typeKnown, enemyName, back)
        return string.format('{"id":%d,"name":null,"type":%s,"callsign":null,"player":null,"group":null,"category":%s,"coalition":%s,"detected":true,%s}',
            u:getID(), (typeKnown and not manualId) and jstr(u:getTypeName()) or "null", jstr(unitCategory(u)), jstr(enemyName), kinematics(u, back))
    end

    local function isLiveUnit(obj)
        if not obj or not obj.isExist or not obj:isExist() then return false end
        local ok, cat = pcall(Object.getCategory, obj)
        return ok and cat == Object.Category.UNIT
    end

    -- Enemy ground units as radar contacts: the server's enemy_ground setting (NEBO.lua), "auto"
    -- meaning only in missions dated enemyGroundYear or later. Otherwise they never appear and
    -- ground threats are found by triangulating pilots' RWR calls (bearing lines).
    local enemyGround = (function()
        local s = NEBO_settings or {}
        if s.enemyGround == true or s.enemyGround == false then return s.enemyGround end
        local year = env.mission.date and tonumber(env.mission.date.Year) or 0
        return year >= (tonumber(s.enemyGroundYear) or 2012)
    end)()

    local function isGroundUnit(obj)
        local ok, desc = pcall(obj.getDesc, obj)
        return ok and type(desc) == "table" and desc.category == Unit.Category.GROUND_UNIT
    end

    -- Contacts by unit id: { unit, typeKnown, sources = { [platform key] = true } }, the key
    -- naming the own radar platform that holds it ("u<unit id>" aircraft, "g<group id>" ground
    -- and naval groups): NCTR picks its platforms from them and freezes what only they hold.
    -- An aircraft's radar looks only so far off the nose, but DCS reports AI aircraft "detecting"
    -- by radar what they know from anywhere (a contact right behind a fighter). Its limits come
    -- from DCS's sensor database (NEBO_settings.radarLimits: type -> { az, elLow, elHigh }, the
    -- hook reads them), else ±60°; AWACS see all round. false: no limit.
    local RADAR_LIMITS = (NEBO_settings or {}).radarLimits or {}
    local limitsOf = {}
    local function radarLimitsOf(u)
        local name = u:getTypeName()
        local l = limitsOf[name]
        if l == nil then
            local okA, awacs = pcall(u.hasAttribute, u, "AWACS")
            l = (not (okA and awacs)) and (RADAR_LIMITS[name] or { 60, -60, 60 }) or false
            limitsOf[name] = l
        end
        return l
    end

    -- Is p within the limits, measured off the nose of an aircraft at pos (getPosition)?
    local function inRadarLimits(l, pos, p)
        local dx, dy, dz = p.x - pos.p.x, p.y - pos.p.y, p.z - pos.p.z
        local fwd = dx * pos.x.x + dy * pos.x.y + dz * pos.x.z
        local right = dx * pos.z.x + dy * pos.z.y + dz * pos.z.z
        local up = dx * pos.y.x + dy * pos.y.y + dz * pos.y.z
        local az = math.deg(math.atan2(right, fwd))
        local el = math.deg(math.atan2(up, math.sqrt(fwd * fwd + right * right)))
        return math.abs(az) <= l[1] and el >= l[2] and el <= l[3]
    end

    -- aircraft: the own aircraft whose radar this is (its limits apply), nil for ground/naval groups.
    local function addContacts(ctrl, mySide, contacts, source, aircraft)
        if not ctrl then return end
        local ok, targets = pcall(ctrl.getDetectedTargets, ctrl, Controller.Detection.RADAR)
        if not ok or type(targets) ~= "table" then return end
        local limits = aircraft and radarLimitsOf(aircraft)
        local pos = limits and aircraft:getPosition()
        for _, t in ipairs(targets) do
            local obj = t.object
            if isLiveUnit(obj) and (enemyGround or not isGroundUnit(obj)) and (not limits or inRadarLimits(limits, pos, obj:getPoint())) then
                local c = obj:getCoalition()
                if c ~= mySide and c ~= coalition.side.NEUTRAL then
                    local id = obj:getID()
                    local entry = contacts[id]
                    if not entry then
                        entry = { unit = obj, typeKnown = false, sources = {} }
                        contacts[id] = entry
                    end
                    if t.type then entry.typeKnown = true end
                    entry.sources[source] = true
                end
            end
        end
    end

    local SIDE_NAME = { [coalition.side.RED] = "red", [coalition.side.BLUE] = "blue", [coalition.side.NEUTRAL] = "neutral" }

    -- Live missiles, read once for both pictures; dead ones leave the list. Each carries
    -- its own side and its target's side (who may see it), and the kinematics JSON.
    local function flyingMissiles()
        local live, out, kept = NEBO_missiles, {}, {}
        for _, m in ipairs(live) do
            local w = m.weapon
            local okE, exists = pcall(w.isExist, w)
            if okE and exists and timer.getTime() - m.t < 600 then
                kept[#kept + 1] = m
                local p = w:getPoint()
                local lat, lon, alt = coord.LOtoLL(p)
                local v = w:getVelocity()
                local hdg = math.deg(math.atan2(v.z, v.x))
                if hdg < 0 then hdg = hdg + 360 end
                local targetId, targetSide = nil, nil
                local okT, tgt = pcall(w.getTarget, w)
                if okT and isLiveUnit(tgt) then
                    targetId, targetSide = tonumber(tgt:getID()), tgt:getCoalition()
                end
                -- A ground launch at the other side: one launch report per missile.
                if m.from and m.group and targetSide and targetSide ~= m.side and not m.reported then
                    m.reported = true
                    local q = NEBO_launches
                    if #q < 100 then
                        q[#q + 1] = string.format("%s,%d,%.0f,%.0f", SIDE_NAME[targetSide] or "neutral", m.group, m.from.x, m.from.z)
                    end
                end
                out[#out + 1] = { m = m, targetId = targetId, targetSide = targetSide,
                    kin = string.format('"lat":%s,"lon":%s,"alt":%s,"hdg":%s,"spd":%s,"vs":%s',
                        jnum(lat, "%.6f"), jnum(lon, "%.6f"), jnum(p.y, "%.1f"), jnum(hdg, "%.1f"),
                        jnum(math.sqrt(v.x * v.x + v.y * v.y + v.z * v.z), "%.1f"), jnum(v.y, "%.1f")) }
            end
        end
        for i = #live, 1, -1 do live[i] = nil end
        for i, m in ipairs(kept) do live[i] = m end
        return out
    end

    -- A side sees its own missiles in full and the enemy's only when aimed at its own units
    -- (then without type or shooter: what a warning receiver could tell).
    local function weaponsFor(side, missiles)
        local out = {}
        for _, f in ipairs(missiles) do
            local m = f.m
            if m.side == side then
                out[#out + 1] = string.format('{"id":%d,"type":%s,"coalition":%s,"shooter":%s,"target":%s,%s}',
                    m.id, jstr(m.type or ""), jstr(SIDE_NAME[m.side] or "neutral"),
                    m.shooter and string.format("%d", m.shooter) or "null",
                    f.targetId and string.format("%d", f.targetId) or "null", f.kin)
            elseif f.targetSide == side then
                out[#out + 1] = string.format('{"id":%d,"type":null,"coalition":%s,"shooter":null,"target":%d,%s}',
                    m.id, jstr(SIDE_NAME[m.side] or "neutral"), f.targetId, f.kin)
            end
        end
        return table.concat(out, ",")
    end

    local NEAR_M = 10 * 1852  -- aircraft this close to one of their side's carriers get the fast feed

    local function headingOf(pos)
        local h = math.deg(math.atan2(pos.x.z, pos.x.x))
        return h < 0 and h + 360 or h
    end

    -- The units of each side by kind, from a full walk of the groups every RESCAN_S seconds or
    -- sooner after a birth (Foothold spawns): the seconds between only check the known units.
    -- Late-activated groups therefore appear within RESCAN_S. Ground and ship groups with a
    -- radar are the ones asked for contacts (most ground groups have none).
    local RESCAN_S, GROUND_S = 5, 5
    local known = { nextScan = 0, sides = {}, byId = {} }
    local groundCache = {}  -- unit id -> { json, next }: see groundUnit

    local function hasRadar(u)
        local ok, r = pcall(u.hasSensors, u, Unit.SensorType.RADAR)
        return ok and r == true
    end

    local function rescan(now)
        for _, entry in ipairs(SIDES) do
            local s = { air = {}, ships = {}, ground = {}, radarGroups = {} }
            for _, gcat in ipairs(GROUP_CATEGORIES) do
                for _, g in ipairs(coalition.getGroups(entry.side, gcat) or {}) do
                    if g:isExist() then
                        local radar = false
                        for _, u in ipairs(g:getUnits() or {}) do
                            if u:isExist() and u:isActive() then
                                if gcat == Group.Category.AIRPLANE or gcat == Group.Category.HELICOPTER then
                                    s.air[#s.air + 1] = u
                                elseif gcat == Group.Category.SHIP then
                                    s.ships[#s.ships + 1] = u
                                    radar = radar or hasRadar(u)
                                else
                                    s.ground[#s.ground + 1] = u
                                    radar = radar or hasRadar(u)
                                end
                            end
                        end
                        if radar then s.radarGroups[#s.radarGroups + 1] = g end
                    end
                end
            end
            known.sides[entry.side] = s
        end
        known.nextScan = now + RESCAN_S
        -- Forget the cached JSON of units that are gone (Foothold spawns and loses many).
        local alive, byId = {}, {}
        for _, s in pairs(known.sides) do
            for _, list in ipairs({ s.air, s.ships, s.ground }) do
                for _, u in ipairs(list) do
                    local id = u:getID()
                    alive[id] = true
                    byId[tonumber(id)] = u  -- getID() is a string in DCS
                end
            end
        end
        known.byId = byId
        for id in pairs(prefixes) do if not alive[id] then prefixes[id] = nil end end
        for id in pairs(groundCache) do if not alive[id] then groundCache[id] = nil end end
    end

    -- A unit born (spawned or activated): walk again at the next picture.
    local birth = {}
    function birth:onEvent(e)
        if e.id == world.event.S_EVENT_BIRTH then known.nextScan = 0 end
    end
    if NEBO_birthHandler then world.removeEventHandler(NEBO_birthHandler) end
    NEBO_birthHandler = birth
    world.addEventHandler(birth)

    -- Ground units and structures barely move: their JSON is rebuilt every GROUND_S seconds.
    local function groundUnit(u, sideName, now)
        local id = u:getID()
        local c = groundCache[id]
        if not c or now >= c.next then
            c = { json = ownUnit(u, sideName), next = now + GROUND_S }
            groundCache[id] = c
        end
        return c.json
    end

    -- The side's carriers (beacons, wind 20 m above the deck) as JSON; the side's aircraft near
    -- them are remembered for NEBO_lso.
    local function carriersFor(entry)
        local list, carriers = {}, {}
        local s = known.sides[entry.side] or { ships = {}, air = {} }
        do
            for _, u in ipairs(s.ships) do
                if u:isExist() and NEBO_carrier.types[u:getTypeName()] then
                    carriers[#carriers + 1] = u
                    local p = u:getPoint()
                    local okW, w = pcall(atmosphere.getWind, { x = p.x, y = p.y + 20, z = p.z })
                    local wx, wz = okW and w and w.x or 0, okW and w and w.z or 0
                    local b = NEBO_carrier.beacons(tonumber(u:getID()))
                    list[#list + 1] = string.format('{"id":%s,"type":%s,"windFrom":%s,"windMs":%s,"tacan":%s,"icls":%s,"link4":%s,"acls":%s,"radio":%s}',
                        jnum(tonumber(u:getID()), "%.0f"), jstr(u:getTypeName()),
                        jnum((math.deg(math.atan2(-wz, -wx)) + 360) % 360, "%.0f"), jnum(math.sqrt(wx * wx + wz * wz), "%.1f"),
                        b.tacan and jstr(b.tacan) or "null", b.icls and jnum(b.icls, "%.0f") or "null",
                        b.link4 and jnum(b.link4 / 1e6, "%.3f") or "null", b.acls and "true" or "false",
                        b.radio and jnum(b.radio / 1e6, "%.3f") or "null")
                end
            end
        end
        local near = {}
        if #carriers > 0 then
            local cps = {}
            for i, c in ipairs(carriers) do cps[i] = c:getPoint() end
            for _, u in ipairs(s.air) do
                if u:isExist() then
                    local p = u:getPoint()
                    for _, cp in ipairs(cps) do
                        if (p.x - cp.x) ^ 2 + (p.z - cp.z) ^ 2 < NEAR_M ^ 2 then
                            near[#near + 1] = u
                            break
                        end
                    end
                end
            end
        end
        NEBO_carrier.near[entry.side] = { carriers = carriers, aircraft = near }
        return table.concat(list, ",")
    end

    -- The side's landings on carriers in the last minute (the client keeps each id once).
    local function eventsFor(entry)
        local out, now = {}, timer.getTime()
        for _, e in ipairs(NEBO_carrier.events) do
            if e.side == entry.side and now - e.t < 60 then
                out[#out + 1] = string.format('{"id":%d,"kind":%s,"unit":%s,"carrier":%s,"time":%s,"x":%s,"z":%s,"cx":%s,"cz":%s}',
                    e.id, jstr(e.kind), jnum(e.unit, "%.0f"), jnum(e.carrier, "%.0f"), jnum(e.t, "%.1f"),
                    jnum(e.x, "%.1f"), jnum(e.z, "%.1f"), jnum(e.cx, "%.1f"), jnum(e.cz, "%.1f"))
            end
        end
        return table.concat(out, ",")
    end

    -- The fast LSO feed: per side with aircraft near its carriers, one line with the carriers'
    -- and those aircraft's state (DCS metres, m/s, degrees; gear and hook 0..1 from the model's
    -- draw arguments 0 and 25, which most carrier aircraft use).
    function NEBO_lso()
        local ok, result = pcall(function()
            local lines = {}
            for _, entry in ipairs(SIDES) do
                local near = NEBO_carrier.near[entry.side]
                if near and #near.aircraft > 0 then
                    local cs, as = {}, {}
                    for _, c in ipairs(near.carriers) do
                        if c:isExist() then
                            local p, v = c:getPoint(), c:getVelocity()
                            cs[#cs + 1] = string.format('{"id":%s,"x":%.2f,"y":%.2f,"z":%.2f,"hdg":%.2f,"vx":%.2f,"vz":%.2f}',
                                jnum(tonumber(c:getID()), "%.0f"), p.x, p.y, p.z, headingOf(c:getPosition()), v.x, v.z)
                        end
                    end
                    for _, u in ipairs(near.aircraft) do
                        if u:isExist() then
                            local p, pos, v = u:getPoint(), u:getPosition(), u:getVelocity()
                            local pitch = math.deg(math.asin(math.max(-1, math.min(1, pos.x.y))))
                            local okG, gear = pcall(u.getDrawArgumentValue, u, 0)
                            local okH, hook = pcall(u.getDrawArgumentValue, u, 25)
                            as[#as + 1] = string.format('{"id":%s,"x":%.2f,"y":%.2f,"z":%.2f,"vx":%.2f,"vy":%.2f,"vz":%.2f,"hdg":%.2f,"pitch":%.2f,"gear":%s,"hook":%s}',
                                jnum(tonumber(u:getID()), "%.0f"), p.x, p.y, p.z, v.x, v.y, v.z, headingOf(pos), pitch,
                                okG and jnum(gear, "%.2f") or "null", okH and jnum(hook, "%.2f") or "null")
                        end
                    end
                    lines[#lines + 1] = string.format('{"type":"lso","coalition":"%s","time":%.2f,"carriers":[%s],"aircraft":[%s]}',
                        entry.name, timer.getTime(), table.concat(cs, ","), table.concat(as, ","))
                end
            end
            return table.concat(lines, "\n")
        end)
        return ok and result or ("ERR:" .. tostring(result))
    end

    -- ---- NCTR -------------------------------------------------------------------------------
    -- A controller asks for NCTR on the contacts in an area with some methods (NEBO_nctrStart).
    -- For each contact and method, the own AI radars that hold it and may use the method check
    -- its conditions every picture; a method answers once they held for its dwell time without
    -- a break. After NCTR_WINDOW seconds every open method gets the reason it failed. Radars
    -- taking part are busy: contacts only they hold freeze (see picture). Player radars never
    -- take part (pilots report their own NCTR to the controller).
    local NCTR_WINDOW, NCTR_KEEP = 20, 120
    local NCTR = {  -- dwell s; range NM for fighters (air) and AWACS / ships / ground radars (big)
        jem = { dwell = 8, air = 40, big = 60 },
        hrrp = { dwell = 10, air = 25, big = 50 },
        isar = { dwell = 6, air = 40, big = 60 },
        size = { dwell = 3 },
        rotor = { dwell = 6, air = 20, big = 20 },
        raid = { dwell = 5, air = 30, big = 50 },
    }
    local NCTR_ORDER = { "jem", "hrrp", "isar", "size", "rotor", "raid" }
    local RAID_M, RAID_ALT = 3000, 600  -- aircraft this close to the target share its radar return
    local nctrYear = tonumber((NEBO_settings or {}).nctrYear) or 1985
    local missionYear = env.mission.date and tonumber(env.mission.date.Year) or 2000
    -- Radars without NCTR (cold-war designs), by lower-case type name.
    local COLD_WAR = { "mig-21", "mig-23", "mig-25", "mig-19", "f-4", "f-5", "mirage-f1", "f-14a", "f-86", "mig-15",
        "snr_75", "s-125", "kub 1s91", "1l13", "p-19", "osa 9a33", "zsu-23-4", "shilka", "hawk", "roland" }
    -- Propeller and turboprop aircraft: no jet engine for JEM.
    local PROPS = { "c-130", "an-26", "an-30", "yak-52", "p-51", "tf-51", "bf-109", "fw-190", "spitfire", "mosquito",
        "i-16", "christen", "c-47", "ju-88", "b-17", "tu-95", "tu-142", "e-2", "mq-1", "mq-9", "il-114", "a-4e" }
    local HRRP_TYPES = { "f-15", "fa-18", "f/a-18", "f-16" }
    -- What JEM tells: the engine, so the family sharing it.
    local JEM_FAMILY = {
        { "su-27", "Su-27 family (AL-31F)" }, { "su-30", "Su-27 family (AL-31F)" }, { "su-33", "Su-27 family (AL-31F)" },
        { "su-34", "Su-27 family (AL-31F)" }, { "j-11", "Su-27 family (AL-31F)" }, { "mig-29", "MiG-29 family (RD-33)" },
        { "jf-17", "JF-17 (RD-93)" }, { "mig-31", "MiG-25/31 family (D-30F6)" }, { "mig-25", "MiG-25/31 family (R-15)" },
        { "su-24", "Su-24 (AL-21F)" }, { "su-17", "Su-17 (AL-21F)" }, { "su-25", "Su-25 (R-95)" }, { "mig-23", "MiG-23/27 (R-29)" },
        { "mig-27", "MiG-23/27 (R-29)" }, { "mig-21", "MiG-21 (R-25)" }, { "tu-22", "Tu-22M (NK-25)" }, { "tu-160", "Tu-160 (NK-32)" },
        { "f-15", "F-15 family (F100)" }, { "f-16", "F-16 (F100/F110)" }, { "fa-18", "F/A-18 family (F404)" }, { "f/a-18", "F/A-18 family (F404)" },
        { "f-14", "F-14 (TF30/F110)" }, { "f-4", "F-4 (J79)" }, { "f-5", "F-5 (J85)" }, { "m-2000", "Mirage 2000 (M53)" },
        { "mirage", "Mirage (Atar)" }, { "a-10", "A-10 (TF34)" }, { "b-1", "B-1B (F101)" }, { "av8b", "AV-8B (Pegasus)" },
        { "tornado", "Tornado (RB199)" }, { "kc-135", "KC-135 / E-3 (CFM56/TF33)" }, { "e-3", "KC-135 / E-3 (CFM56/TF33)" },
        { "a-50", "Il-76 family (D-30KP)" }, { "il-76", "Il-76 family (D-30KP)" }, { "il-78", "Il-76 family (D-30KP)" },
    }
    local nctr = { nextId = 1, requests = {}, last = {} }
    local frozenCache = { [coalition.side.BLUE] = {}, [coalition.side.RED] = {} }

    local function anyOf(list, lower)
        for _, p in ipairs(list) do
            if lower:find(p, 1, true) then return true end
        end
        return false
    end

    -- The unit that stands for a platform: itself, or a group's first radar unit.
    local function platformUnit(p)
        if not p or not p:isExist() then return nil end
        if p.getUnits then
            for _, u in ipairs(p:getUnits() or {}) do
                if u:isExist() and hasRadar(u) then return u end
            end
            return nil
        end
        return p
    end

    local nctrCaps = {}  -- type name -> what that radar may do
    local function capsOf(u)
        local name = u:getTypeName()
        local c = nctrCaps[name]
        if c then return c end
        local lower, cat = name:lower(), unitCategory(u)
        local okA, awacs = pcall(u.hasAttribute, u, "AWACS")
        awacs = okA and awacs == true
        local recent = missionYear >= nctrYear
        local modern = recent and not anyOf(COLD_WAR, lower) and cat ~= "helicopter"  -- jets and radar sites
        c = { jem = modern, isar = modern, rotor = modern, raid = modern, size = true,
              hrrp = recent and (awacs or anyOf(HRRP_TYPES, lower)), big = awacs or cat == "ground" or cat == "ship" }
        nctrCaps[name] = c
        return c
    end

    local function targetReason(method, u, cat)
        if method == "jem" then
            if cat ~= "airplane" or anyOf(PROPS, u:getTypeName():lower()) then return "not a jet" end
        elseif method == "hrrp" then
            if cat ~= "airplane" and cat ~= "helicopter" then return "not an aircraft" end
        elseif method == "isar" then
            if cat == "ground" then return "not a ship or aircraft" end
        elseif method == "rotor" then
            if cat ~= "helicopter" then return "not a helicopter" end
        elseif method == "raid" then
            if cat ~= "airplane" and cat ~= "helicopter" then return "not an aircraft" end
        end
        return nil
    end

    local function resultFor(method, u, contacts)
        local name = u:getTypeName()
        if method == "jem" then
            local lower = name:lower()
            for _, f in ipairs(JEM_FAMILY) do
                if lower:find(f[1], 1, true) then return f[2] end
            end
            return name
        elseif method == "size" then
            local ok, desc = pcall(u.getDesc, u)
            local box = ok and desc and desc.box
            local len = box and math.max(box.max.x - box.min.x, box.max.z - box.min.z) or 15
            return len < 10 and "small" or len < 30 and "medium" or "large"
        elseif method == "raid" then
            -- Aircraft of its side close enough to share its return (itself included), not
            -- counting those the side already holds as contacts of their own (resolved).
            local p, n = u:getPoint(), 0
            local s = known.sides[u:getCoalition()]
            for _, o in ipairs(s and s.air or {}) do
                if o:isExist() and (o:getID() == u:getID() or not (contacts and contacts[o:getID()])) then
                    local q = o:getPoint()
                    if (q.x - p.x) ^ 2 + (q.z - p.z) ^ 2 <= RAID_M * RAID_M and math.abs(q.y - p.y) <= RAID_ALT then n = n + 1 end
                end
            end
            n = math.max(n, 1)
            return n == 1 and "single aircraft" or (n .. " aircraft")
        end
        return name  -- HRRP, ISAR and rotor modulation give the type
    end

    -- Can platform unit pu use the method on the target now? true, or false, why not and how close
    -- it came (rank: 1 geometry — aspect, turn, cone; 2 range or lost track; 3 cannot at all), so
    -- the result can give the nearest miss among the radars holding it (one reason, not a list).
    local JEM_ASPECT = 30  -- deg off the nose or tail

    local function nctrInRange(method, pu, tp)
        local caps = capsOf(pu)
        if not caps[method] or pu:getPlayerName() then return false end
        local pp = pu:getPoint()
        local limit = caps.big and NCTR[method].big or NCTR[method].air
        return not limit or math.sqrt((pp.x - tp.x) ^ 2 + (pp.z - tp.z) ^ 2) / 1852 <= limit
    end

    local function nctrCondition(method, pu, tp, tv, tcat, turned)
        local caps = capsOf(pu)
        if not caps[method] then return false, "cannot use this method", 3 end
        if pu:getPlayerName() then return false, "player radar (the pilot reports)", 3 end
        local pp = pu:getPoint()
        local rule = NCTR[method]
        local limit = caps.big and rule.big or rule.air
        local dNm = math.sqrt((pp.x - tp.x) ^ 2 + (pp.z - tp.z) ^ 2) / 1852
        if limit and dNm > limit then return false, string.format("range %.0f NM (max %d)", dNm, limit), 2 end
        if method == "jem" and tv.x * tv.x + tv.z * tv.z > 900 then
            local toRadar = math.deg(math.atan2(pp.z - tp.z, pp.x - tp.x))
            local off = math.abs((toRadar - math.deg(math.atan2(tv.z, tv.x)) + 540) % 360 - 180)
            if off > JEM_ASPECT and off < 180 - JEM_ASPECT then
                return false, string.format("aspect %.0f deg (needs nose or tail within %d)", off, JEM_ASPECT), 1
            end
        elseif method == "isar" and tcat ~= "ship" and not turned then
            return false, "not turning (needs more than 15 deg in 5 s)", 1
        end
        return true
    end

    -- Each radar taking part looks along a cone from itself (grid degrees) that covers the
    -- request's targets it holds, plus a margin, at most NCTR_CONE wide; recomputed every picture
    -- as radar and targets move. A radar holding none of them has no cone and is not busy.
    local NCTR_CONE, NCTR_MARGIN = 30, 3

    local function inCone(cone, p)
        local b = math.deg(math.atan2(p.z - cone.z, p.x - cone.x))
        return math.abs((b - cone.center + 540) % 360 - 180) <= cone.half
    end

    local function nctrCones(r, contacts, platforms)
        r.cones = {}
        for key in pairs(r.busy) do
            local pu = platformUnit(platforms[key])
            if pu then
                local pp = pu:getPoint()
                local bearings, sx, sy, far = {}, 0, 0, 0
                for tid, tg in pairs(r.targets) do
                    local c = contacts[tid]
                    -- Only targets it holds with an open method it may use within range: a radar
                    -- with nothing left it can do there makes no cone (and freezes nothing).
                    local tp = c and c.sources[key] and tg.unit:isExist() and tg.unit:getPoint()
                    local workable = false
                    if tp then
                        for m in pairs(r.methods) do
                            if not tg.res[m] and nctrInRange(m, pu, tp) then workable = true break end
                        end
                    end
                    if workable then
                        local b = math.atan2(tp.z - pp.z, tp.x - pp.x)
                        bearings[#bearings + 1] = math.deg(b)
                        sx, sy = sx + math.cos(b), sy + math.sin(b)
                        far = math.max(far, math.sqrt((tp.x - pp.x) ^ 2 + (tp.z - pp.z) ^ 2))
                    end
                end
                if #bearings > 0 then
                    local center = math.deg(math.atan2(sy, sx))
                    local half = 0
                    for _, b in ipairs(bearings) do half = math.max(half, math.abs((b - center + 540) % 360 - 180)) end
                    r.cones[key] = { x = pp.x, z = pp.z, center = center, half = math.min(NCTR_CONE / 2, half + NCTR_MARGIN),
                        range = far * 1.15, name = pu:getName() }
                end
            end
        end
    end

    -- The cones of the radars taking part in this side's running requests: { [key] = { cone, … } }.
    local function nctrBusy(side)
        local busy = {}
        for _, r in pairs(nctr.requests) do
            if r.side == side and r.state == "running" then
                for key, cone in pairs(r.cones or {}) do
                    busy[key] = busy[key] or {}
                    table.insert(busy[key], cone)
                end
            end
        end
        return busy
    end

    -- Frozen: every radar holding the contact runs NCTR and the contact is outside all their
    -- cones (a radar dwelling on its cone still updates what is in it, not what is elsewhere).
    local function isFrozen(c, busy)
        if next(busy) == nil then return false end
        local p
        for key in pairs(c.sources) do
            local cones = busy[key]
            if not cones then return false end
            p = p or c.unit:getPoint()
            for _, cone in ipairs(cones) do
                if inCone(cone, p) then return false end
            end
        end
        return p ~= nil
    end

    local function nctrStep(side, contacts, platforms, now)
        nctr.last[side] = { contacts = contacts, platforms = platforms }
        for id, r in pairs(nctr.requests) do
            if r.state == "done" and now - r.doneAt > NCTR_KEEP then nctr.requests[id] = nil end
        end
        for _, r in pairs(nctr.requests) do
            if r.side == side and r.state == "running" then
                local dt = math.min(2, now - r.lastT)
                r.lastT = now
                nctrCones(r, contacts, platforms)
                local open = 0
                for tid, tg in pairs(r.targets) do
                    local u = tg.unit
                    local alive = u and u:isExist()
                    if alive then
                        local tp, tv = u:getPoint(), u:getVelocity()
                        local hdg = math.deg(math.atan2(tv.z, tv.x))
                        tg.hist[#tg.hist + 1] = { t = now, hdg = hdg }
                        while #tg.hist > 1 and now - tg.hist[1].t > 6 do table.remove(tg.hist, 1) end
                        local turned = false
                        for _, h in ipairs(tg.hist) do
                            if now - h.t <= 5 and math.abs((hdg - h.hdg + 540) % 360 - 180) > 15 then turned = true end
                        end
                        local c = contacts[tid]
                        -- Radars that held it during the request and lost it say so.
                        for key in pairs(c and c.sources or {}) do
                            local pu = platformUnit(platforms[key])
                            if pu then tg.seen[key] = pu:getName() end
                        end
                        for m in pairs(r.methods) do
                            tg.active[m] = nil
                            if not tg.res[m] then
                                local reasons = {}
                                for key, name in pairs(tg.seen) do
                                    local cone = r.cones[key]
                                    local pu = c and c.sources[key] and platformUnit(platforms[key])
                                    local ok, why, rank
                                    if not pu then
                                        ok, why, rank = false, "lost the track", 2
                                    else
                                        ok, why, rank = nctrCondition(m, pu, tp, tv, tg.cat, turned)
                                        if ok and not (cone and inCone(cone, tp)) then
                                            ok, why, rank = false, string.format("outside its NCTR cone (%d deg max)", NCTR_CONE), 1
                                        end
                                    end
                                    local dk = m .. key
                                    if ok then
                                        tg.active[m] = true
                                        tg.dwell[dk] = (tg.dwell[dk] or 0) + dt
                                        if tg.dwell[dk] >= NCTR[m].dwell and not tg.res[m] then
                                            tg.res[m] = { result = resultFor(m, u, contacts), platform = name }
                                        end
                                    else
                                        tg.dwell[dk] = 0
                                        reasons[#reasons + 1] = { rank = rank, text = name .. ": " .. why }
                                    end
                                end
                                -- One reason: the nearest miss (lowest rank) and the radar it is from.
                                table.sort(reasons, function(a, b) return a.rank < b.rank or (a.rank == b.rank and a.text < b.text) end)
                                tg.why[m] = reasons[1] and reasons[1].text or (not c and "not held by any radar now") or nil
                                if not tg.res[m] then open = open + 1 end
                            end
                        end
                    else
                        for m in pairs(r.methods) do
                            if not tg.res[m] then tg.res[m] = { reason = "contact gone" } end
                        end
                    end
                end
                if open == 0 or now >= r.deadline then
                    for _, tg in pairs(r.targets) do
                        for m in pairs(r.methods) do
                            if not tg.res[m] then tg.res[m] = { reason = tg.why[m] or "no NCTR-capable radar holds it" } end
                        end
                    end
                    r.state, r.doneAt = "done", now
                end
            end
        end
    end

    -- Controller command "nctr": the contacts the side holds inside the area, with the methods
    -- named ("jem,hrrp,…"). Returns "ok:<request id>" or the reason.
    function NEBO_nctrStart(coalitionName, x1, z1, x2, z2, methodList)
        local side = coalitionName == "blue" and coalition.side.BLUE or coalitionName == "red" and coalition.side.RED or nil
        if not side then return "unknown coalition" end
        local methods, any = {}, false
        for m in tostring(methodList):gmatch("[a-z]+") do
            if NCTR[m] then methods[m], any = true, true end
        end
        if not any then return "no known method (jem, hrrp, isar, size, rotor, raid)" end
        local running = 0
        for _, r in pairs(nctr.requests) do
            if r.side == side and r.state == "running" then running = running + 1 end
        end
        if running >= 3 then return "three NCTR requests already running" end
        local last = nctr.last[side]
        if not last then return "server not ready yet" end
        local xa, xb, za, zb = math.min(x1, x2), math.max(x1, x2), math.min(z1, z2), math.max(z1, z2)
        local targets, busy, n = {}, {}, 0
        for id, c in pairs(last.contacts) do
            local u = c.unit
            if u:isExist() then
                local p = u:getPoint()
                if p.x >= xa and p.x <= xb and p.z >= za and p.z <= zb then
                    local cat = unitCategory(u)
                    local tg = { unit = u, cat = cat, res = {}, dwell = {}, why = {}, hist = {}, seen = {}, active = {} }
                    for m in pairs(methods) do
                        local why = targetReason(m, u, cat)
                        if why then tg.res[m] = { reason = why } end
                    end
                    targets[id] = tg
                    n = n + 1
                    -- The AI radars holding it that may use one of the methods take part.
                    for key in pairs(c.sources) do
                        local pu = platformUnit(last.platforms[key])
                        if pu and not pu:getPlayerName() then
                            local caps = capsOf(pu)
                            for m in pairs(methods) do
                                if caps[m] then busy[key] = true end
                            end
                        end
                    end
                end
            end
        end
        if n == 0 then return "no contacts in the area" end
        local now = timer.getTime()
        local id = nctr.nextId
        nctr.nextId = id + 1
        nctr.requests[id] = { id = id, side = side, methods = methods, targets = targets, busy = busy,
            started = now, lastT = now, deadline = now + NCTR_WINDOW, state = "running",
            area = { xa, za, xb, zb } }
        return "ok:" .. id
    end

    -- One line per side with its requests (running, or done in the last NCTR_KEEP s).
    function NEBO_nctr()
        local ok, result = pcall(function()
            local now, lines = timer.getTime(), {}
            for _, entry in ipairs(SIDES) do
                local reqs = {}
                local ids = {}
                for id, r in pairs(nctr.requests) do
                    if r.side == entry.side then ids[#ids + 1] = id end
                end
                table.sort(ids)
                for _, id in ipairs(ids) do
                    local r = nctr.requests[id]
                    local methods, items = {}, {}
                    for _, m in ipairs(NCTR_ORDER) do
                        if r.methods[m] then methods[#methods + 1] = jstr(m) end
                    end
                    for tid, tg in pairs(r.targets) do
                        for _, m in ipairs(NCTR_ORDER) do
                            if r.methods[m] then
                                local res = tg.res[m]
                                local status = not res and "pending" or res.result and "ok" or "none"
                                -- active: a radar is working this method on it now (conditions met, dwelling).
                                items[#items + 1] = string.format('{"unit":%d,"method":"%s","status":"%s","active":%s,"result":%s,"reason":%s,"platform":%s}',
                                    tid, m, status, (not res and r.state == "running" and tg.active[m]) and "true" or "false",
                                    res and res.result and jstr(res.result) or "null",
                                    res and res.reason and jstr(res.reason) or (not res and tg.why[m] and jstr(tg.why[m]) or "null"),
                                    res and res.platform and jstr(res.platform) or "null")
                            end
                        end
                    end
                    -- Each radar's cone (DCS metres, grid degrees) while the request runs.
                    local cones = {}
                    if r.state == "running" then
                        for _, cone in pairs(r.cones or {}) do
                            cones[#cones + 1] = string.format('{"platform":%s,"x":%.0f,"z":%.0f,"center":%.1f,"half":%.1f,"range":%.0f}',
                                jstr(cone.name), cone.x, cone.z, cone.center, cone.half, cone.range)
                        end
                    end
                    reqs[#reqs + 1] = string.format('{"id":%d,"state":"%s","time":%.1f,"left":%.0f,"area":[%.0f,%.0f,%.0f,%.0f],"methods":[%s],"radars":%d,"cones":[%s],"results":[%s]}',
                        id, r.state, r.started, r.state == "running" and math.max(0, r.deadline - now) or 0,
                        r.area[1], r.area[2], r.area[3], r.area[4], table.concat(methods, ","),
                        #cones > 0 and #cones or (function() local k = 0 for _ in pairs(r.busy) do k = k + 1 end return k end)(),
                        table.concat(cones, ","), table.concat(items, ","))
                end
                lines[#lines + 1] = string.format('{"type":"nctr","coalition":"%s","requests":[%s]}', entry.name, table.concat(reqs, ","))
            end
            return table.concat(lines, "\n")
        end)
        return ok and result or ("ERR:" .. tostring(result))
    end

    -- ---- Radar scan model (radar_scan = true, NEBO.lua) ---------------------------------------
    -- Each own radar paints on its own clock: a period T by type (a rotodome 10 s, a ship's
    -- radar 5-6 s, a SAM search radar 2-5 s, a fighter's frame 4 s, a phased array 1 s) and a
    -- fixed phase. A rotating radar paints a target when its beam passes the target's bearing,
    -- an array or a fighter's frame paints all it holds once per T. A paint is a plot: the
    -- contact's position at that instant (wound back along its velocity). A contact shows its
    -- latest plot from any radar holding it, so several radars at other phases refresh it more
    -- often than any one of them. Near the edge of a radar's range a paint may miss (probability
    -- 1 up to 80 % of its range, 0.5 at the edge), the same for every client. Whether a radar
    -- holds a contact at all is still DCS's own detection. A radar dwelling on an NCTR cone
    -- paints the cone every second and nothing else.
    local radarScan = (NEBO_settings or {}).radarScan ~= false
    local SCAN_TYPES = {  -- lower-case type name part, period s, rotating (else a frame or array)
        { "e-3a", 10, true }, { "e-2", 10, true }, { "a-50", 10, true }, { "kj-2000", 5, false },  -- "e-3a": "e-3" matched the F-5E-3
        { "55g6", 10, true }, { "1l13", 10, true }, { "p-19", 10, true }, { "64h6e", 12, true },
        { "9s18m1", 5, true }, { "1s91", 4, true }, { "hawk sr", 3, true }, { "hawk cwar", 3, true },
        { "mpq64", 2, true }, { "dog ear", 2, true }, { "osa", 2, true }, { "tor 9a331", 1, true },
        { "roland", 1, true }, { "gepard", 1, true }, { "2s6", 1, true }, { "rapier", 2, true },
        { "patriot str", 1, false }, { "zsu-23-4", 1, false },
        -- AEGIS-class arrays: 4 s, so contacts still step (at 1 s, a fleet covered the whole map every second).
        { "ticonderog", 4, false }, { "arleigh", 4, false }, { "burke", 4, false }, { "type_052", 4, false },
        { "cvn", 6, true }, { "stennis", 6, true }, { "vinson", 6, true }, { "forrestal", 6, true },
        { "lha", 6, true }, { "tarawa", 6, true }, { "perry", 5, true }, { "type_054", 5, true }, { "type_071", 6, true },
        { "kuznecow", 5, true }, { "moscow", 5, true }, { "piotr", 5, true }, { "neustrash", 5, true },
        { "albatros", 5, true }, { "rezky", 5, true }, { "molniya", 5, true },
    }

    -- A fixed pseudo-random number in [0, 1) for (a, b, c): phases and missed paints.
    local function chance(a, b, c)
        local x = math.sin(a * 12.9898 + b * 78.233 + c * 37.719) * 43758.5453
        return x - math.floor(x)
    end

    local scanTypes = {}  -- type name -> { period, rotating } or false (not in the table)
    local function scanOfType(u)
        local name = u:getTypeName()
        local t = scanTypes[name]
        if t == nil then
            t = false
            local lower = name:lower()
            for _, e in ipairs(SCAN_TYPES) do
                if lower:find(e[1], 1, true) then t = { period = e[2], rotating = e[3] } break end
            end
            scanTypes[name] = t
        end
        return t
    end

    local function fallbackScan(u)
        local okA, awacs = pcall(u.hasAttribute, u, "AWACS")
        if okA and awacs then return { period = 10, rotating = true } end
        local cat = unitCategory(u)
        if cat == "ship" then return { period = 6, rotating = true } end
        if cat == "ground" then
            local okE, ewr = pcall(u.hasAttribute, u, "EWR")
            return { period = (okE and ewr) and 10 or 4, rotating = true }
        end
        return { period = 4, rotating = false }  -- a fighter's (or helicopter's) radar frame
    end

    local radarRanges = {}  -- type name -> metres (DCS's longest air detection range) or false
    local function rangeOf(u)
        local name = u:getTypeName()
        local r = radarRanges[name]
        if r == nil then
            r = false
            local ok, sensors = pcall(u.getSensors, u)
            if ok and type(sensors) == "table" then
                for _, s in ipairs(sensors[Unit.SensorType.RADAR] or {}) do
                    local d = s.detectionDistanceAir
                    for _, h in ipairs({ d and d.upperHemisphere or {}, d and d.lowerHemisphere or {} }) do
                        for _, v in ipairs({ h.headOn or 0, h.tailOn or 0 }) do
                            if type(v) == "number" and v > (r or 0) then r = v end
                        end
                    end
                end
            end
            radarRanges[name] = r
        end
        return r
    end

    -- The scan of an own radar platform ("u<id>" aircraft, "g<id>" group): its radar unit,
    -- period, phase, rotating, range; kept per key, looked up again when its unit is gone.
    local scans = {}
    -- Phases spread evenly instead of by a hash of the id (two radars could land on nearly the
    -- same phase and paint together): the n-th radar met starts at frac(n × 0.618) of its period,
    -- so any number of them stays evenly spaced (golden-ratio sequence).
    local scanCount = 0
    local function scanOf(key, platform)
        local s = scans[key]
        if s and s.unit:isExist() then return s end
        local unit, kind
        if platform and platform:isExist() and platform.getUnits then
            for _, u in ipairs(platform:getUnits() or {}) do
                if u:isExist() and hasRadar(u) then
                    unit = unit or u
                    kind = kind or scanOfType(u) or nil
                end
            end
        elseif platform and platform:isExist() then
            unit = platform
            kind = scanOfType(platform) or nil
        end
        if not unit then scans[key] = nil return nil end
        kind = kind or fallbackScan(unit)
        local num = (tonumber(key:sub(2)) or 0) + (key:sub(1, 1) == "g" and 1e7 or 0)
        local spread = scanCount * 0.6180339887
        scanCount = scanCount + 1
        s = { unit = unit, num = num, period = kind.period, rotating = kind.rotating,
              phase = (spread - math.floor(spread)) * kind.period, range = rangeOf(unit) }
        scans[key] = s
        return s
    end

    local function detectionChance(s, rp, tp)
        if not s.range or s.range <= 0 then return 1 end
        local dx, dy, dz = tp.x - rp.x, tp.y - rp.y, tp.z - rp.z
        local ratio = math.sqrt(dx * dx + dy * dy + dz * dz) / s.range
        if ratio <= 0.8 then return 1 end
        return math.max(0.5, 1 - (ratio - 0.8) * 2.5)
    end

    -- The latest paint of contact c (unit id id) at or before now and after since (its last
    -- plot); nil when no radar holding it painted it since. A new contact (since nil) takes its
    -- latest sweep even when that one would have missed. Each radar counts a sweep once: a
    -- rotating radar's paint time follows the target's bearing, which creeps while it flies, so
    -- the same sweep would otherwise come back a little later every second as a "new" plot
    -- (the position wound back to the old instant, speed and altitude fresh). swept: radar key ->
    -- the last sweep looked at (the plot's), and filled in with this picture's.
    local function latestPaint(c, id, since, busy, platforms, now, swept)
        local best, tp
        for key in pairs(c.sources) do
            local paint
            local cones = busy[key]
            if cones then
                tp = tp or c.unit:getPoint()
                for _, cone in ipairs(cones) do
                    if inCone(cone, tp) then paint = now break end
                end
            else
                local s = scanOf(key, platforms[key])
                if s then
                    tp = tp or c.unit:getPoint()
                    local rp = s.unit:getPoint()
                    local start = s.phase
                    if s.rotating then
                        local brg = math.atan2(tp.z - rp.z, tp.x - rp.x) / (2 * math.pi)
                        if brg < 0 then brg = brg + 1 end
                        start = start + brg * s.period
                    end
                    local k = math.floor((now - start) / s.period)
                    local last = swept[key]
                    local pd = detectionChance(s, rp, tp)
                    for j = 0, 2 do
                        if last and k - j <= last then break end
                        local t = start + (k - j) * s.period
                        if since and t <= since then break end
                        if pd >= 1 or chance(s.num, id, k - j) < pd then paint = t break end
                    end
                    if not paint and not since then paint = start + k * s.period end
                    if not last or k > last then swept[key] = k end
                end
            end
            if paint and (not best or paint > best) then best = paint end
        end
        return best
    end

    local plotCache = { [coalition.side.BLUE] = {}, [coalition.side.RED] = {} }  -- unit id -> { t, json }

    local function picture(entry, enemyName, missiles, now)
        local units, contacts, platforms = {}, {}, {}
        local s = known.sides[entry.side]
        -- Ground and naval sensors are driven by the group controller, aircraft (AWACS,
        -- fighters) by their unit controllers.
        for _, g in ipairs(s.radarGroups) do
            if g:isExist() then
                local key = "g" .. tostring(g:getID())
                platforms[key] = g
                addContacts(g:getController(), entry.side, contacts, key)
            end
        end
        for _, u in ipairs(s.air) do
            if u:isExist() then
                units[#units + 1] = ownUnit(u, entry.name)
                local key = "u" .. tostring(u:getID())
                platforms[key] = u
                addContacts(u:getController(), entry.side, contacts, key, u)
            end
        end
        nctrStep(entry.side, contacts, platforms, now)
        for _, u in ipairs(s.ships) do
            if u:isExist() then units[#units + 1] = ownUnit(u, entry.name) end
        end
        for _, u in ipairs(s.ground) do
            if u:isExist() then units[#units + 1] = groundUnit(u, entry.name, now) end
        end
        -- A contact held only by radars busy with NCTR elsewhere keeps its last position (frozen).
        local busy = nctrBusy(entry.side)
        local cache = frozenCache[entry.side]
        local fresh = {}
        if radarScan then
            -- Each contact at its latest plot, with the plot's age in seconds.
            local plots, kept = plotCache[entry.side], {}
            for id, c in pairs(contacts) do
                local plot = plots[id]
                local swept = plot and plot.swept or {}
                local paint = latestPaint(c, id, plot and plot.t, busy, platforms, now, swept)
                if paint and (not plot or paint > plot.t) then
                    plot = { t = paint, json = contact(c.unit, c.typeKnown, enemyName, now - paint), swept = swept }
                elseif not plot then
                    plot = { t = now, json = contact(c.unit, c.typeKnown, enemyName), swept = swept }
                end
                kept[id] = plot
                local json = plot.json
                if isFrozen(c, busy) then json = json:gsub('"detected":true,', '"detected":true,"frozen":true,', 1) end
                units[#units + 1] = string.format('%s,"age":%d}', json:sub(1, -2), math.floor(now - plot.t + 0.5))
            end
            plotCache[entry.side] = kept
        else
            for id, c in pairs(contacts) do
                local frozen = isFrozen(c, busy)
                local json
                if frozen and cache[id] then
                    json = cache[id]:gsub('"detected":true,', '"detected":true,"frozen":true,', 1)
                else
                    json = contact(c.unit, c.typeKnown, enemyName)
                end
                if not frozen then fresh[id] = json else fresh[id] = cache[id] or json end
                units[#units + 1] = json
            end
            frozenCache[entry.side] = fresh
        end
        -- The side's own bullseye: scripts can move it, so read it with every picture.
        local bulls = "null"
        local okB, ref = pcall(coalition.getMainRefPoint, entry.side)
        if okB and ref then
            local lat, lon = coord.LOtoLL(ref)
            bulls = string.format('{"x":%s,"z":%s,"lat":%s,"lon":%s}', jnum(ref.x, "%.1f"), jnum(ref.z, "%.1f"), jnum(lat, "%.6f"), jnum(lon, "%.6f"))
        end
        return string.format('{"type":"picture","version":1,"coalition":"%s","time":%s,"theatre":%s,"bullseye":%s,"units":[%s],"weapons":[%s],"carriers":[%s],"events":[%s]}',
            entry.name, jnum(timer.getTime(), "%.1f"), jstr(env.mission.theatre or ""), bulls, table.concat(units, ","),
            weaponsFor(entry.side, missiles), carriersFor(entry), eventsFor(entry))
    end

    -- The hook builds the two pictures and the group walk on separate frames (NEBO_collectSide,
    -- NEBO_rescan), so their costs never add up in one frame.
    function NEBO_rescan()
        local ok, err = pcall(function()
            local now = timer.getTime()
            if now >= known.nextScan then rescan(now) end
        end)
        return ok and "" or ("ERR:" .. tostring(err))
    end

    function NEBO_collectSide(sideName)
        local ok, result = pcall(function()
            local now = timer.getTime()
            if not known.sides[coalition.side.BLUE] then rescan(now) end  -- the first picture
            local missiles = flyingMissiles()
            if sideName == "red" then return picture(SIDES[2], "blue", missiles, now) end
            return picture(SIDES[1], "red", missiles, now)
        end)
        if ok then return result end
        return "ERR:" .. tostring(result)
    end

    -- ---- NEBO Camera (camera logins only: both sides, unfiltered) -------------------------
    -- camera_picture: every known unit and live missile with world x/y/z, plus the last 60 s of
    -- events; built only while a camera is logged in (the hook asks). Ground units and ships
    -- reuse their position for CAMERA_GROUND_S. Weapon ids are CAMERA_WEAPON + the missile's id
    -- so they never clash with unit ids. camera_focus: up to 16 ids, position, velocity and
    -- attitude, 10-20 times a second (the hook's focus tick), like the LSO feed.
    local CAMERA_WEAPON = 1000000000
    local CAMERA_GROUND_S = 5
    local CAMERA_CATEGORY = { airplane = "air", helicopter = "heli", ground = "ground", ship = "ship", structure = "static" }
    local cameraCache = {}  -- unit id -> { player, prefix, pos, next }
    local cameraEvents, cameraEventId = {}, 0

    local function cameraEvent(kind, time, unit, other, weapon)
        cameraEventId = cameraEventId + 1
        if #cameraEvents >= 100 then table.remove(cameraEvents, 1) end
        cameraEvents[#cameraEvents + 1] = { id = cameraEventId, kind = kind, time = time, unit = unit, other = other, weapon = weapon }
    end

    local function objectId(obj)
        if not obj or not obj.getID then return nil end
        local ok, id = pcall(obj.getID, obj)
        return ok and tonumber(id) or nil
    end

    local CAMERA_EVENTS = {
        [world.event.S_EVENT_KILL] = "kill", [world.event.S_EVENT_CRASH] = "crash",
        [world.event.S_EVENT_EJECTION] = "eject", [world.event.S_EVENT_TAKEOFF] = "takeoff",
        [world.event.S_EVENT_LAND] = "land",
    }
    local cameraHandler = {}
    function cameraHandler:onEvent(e)
        pcall(function()
            local kind = CAMERA_EVENTS[e.id]
            if not kind then return end
            if kind == "kill" then
                cameraEvent(kind, timer.getTime(), objectId(e.target), objectId(e.initiator), nil)
            else
                cameraEvent(kind, timer.getTime(), objectId(e.initiator), nil, nil)
            end
        end)
    end
    if NEBO_cameraHandler then world.removeEventHandler(NEBO_cameraHandler) end
    NEBO_cameraHandler = cameraHandler
    world.addEventHandler(cameraHandler)

    local function cameraHeading(v, u)
        local spd = math.sqrt(v.x * v.x + v.y * v.y + v.z * v.z)
        local hdg
        if spd > 2 then
            hdg = math.deg(math.atan2(v.z, v.x))
        else
            local pos = u:getPosition()
            hdg = math.deg(math.atan2(pos.x.z, pos.x.x))
        end
        if hdg < 0 then hdg = hdg + 360 end
        return hdg, spd
    end

    local function cameraUnit(u, sideName, slow, now)
        local id = u:getID()
        local player = u:getPlayerName()
        local c = cameraCache[id]
        if not c or c.player ~= player then
            local g = u:getGroup()
            c = { player = player, next = 0, prefix = string.format('{"id":%d,"name":%s,"type":%s,"player":%s,"group":%s,"category":%s,"coalition":%s,',
                id, jstr(u:getName()), jstr(u:getTypeName()), player and jstr(player) or "null", jstr(g and g:getName() or ""),
                jstr(CAMERA_CATEGORY[unitCategory(u)] or "ground"), jstr(sideName)) }
            cameraCache[id] = c
        end
        if not slow or now >= c.next then
            local p, v = u:getPoint(), u:getVelocity()
            local hdg, spd = cameraHeading(v, u)
            c.pos = string.format('"x":%s,"y":%s,"z":%s,"hdg":%s,"spd":%s}', jnum(p.x, "%.1f"), jnum(p.y, "%.1f"), jnum(p.z, "%.1f"),
                jnum(hdg, "%.1f"), jnum(spd, "%.1f"))
            c.next = now + CAMERA_GROUND_S
        end
        return c.prefix .. c.pos
    end

    local function liveMissile(m)
        local okE, exists = pcall(m.weapon.isExist, m.weapon)
        return okE and exists and timer.getTime() - m.t < 600
    end

    function NEBO_cameraPicture()
        local ok, result = pcall(function()
            local now = timer.getTime()
            if not known.sides[coalition.side.BLUE] then rescan(now) end
            local units, alive = {}, {}
            for _, entry in ipairs(SIDES) do
                local s = known.sides[entry.side]
                for _, u in ipairs(s.air) do
                    if u:isExist() then units[#units + 1] = cameraUnit(u, entry.name, false, now) alive[u:getID()] = true end
                end
                for _, list in ipairs({ s.ships, s.ground }) do
                    for _, u in ipairs(list) do
                        if u:isExist() then units[#units + 1] = cameraUnit(u, entry.name, true, now) alive[u:getID()] = true end
                    end
                end
            end
            for id in pairs(cameraCache) do if not alive[id] then cameraCache[id] = nil end end
            local weapons, events = {}, {}
            for _, m in ipairs(NEBO_missiles) do
                if m.t > now - 60 then
                    -- a shot is an event of its own (the missile list keeps them)
                    events[#events + 1] = string.format('{"id":%d,"kind":"shot","time":%s,"unit":%s,"other":null,"weapon":%d}',
                        CAMERA_WEAPON + m.id, jnum(m.t, "%.1f"), m.shooter and string.format("%d", m.shooter) or "null", CAMERA_WEAPON + m.id)
                end
                if liveMissile(m) then
                    local w = m.weapon
                    local p, v = w:getPoint(), w:getVelocity()
                    local hdg = math.deg(math.atan2(v.z, v.x))
                    if hdg < 0 then hdg = hdg + 360 end
                    local okT, tgt = pcall(w.getTarget, w)
                    local target = okT and isLiveUnit(tgt) and tonumber(tgt:getID()) or nil
                    weapons[#weapons + 1] = string.format('{"id":%d,"type":%s,"coalition":%s,"shooter":%s,"target":%s,"x":%s,"y":%s,"z":%s,"hdg":%s,"spd":%s}',
                        CAMERA_WEAPON + m.id, jstr(m.type or ""), jstr(SIDE_NAME[m.side] or "neutral"),
                        m.shooter and string.format("%d", m.shooter) or "null", target and string.format("%d", target) or "null",
                        jnum(p.x, "%.1f"), jnum(p.y, "%.1f"), jnum(p.z, "%.1f"), jnum(hdg, "%.1f"),
                        jnum(math.sqrt(v.x * v.x + v.y * v.y + v.z * v.z), "%.1f"))
                end
            end
            for _, e in ipairs(cameraEvents) do
                if e.time > now - 60 then
                    events[#events + 1] = string.format('{"id":%d,"kind":"%s","time":%s,"unit":%s,"other":%s,"weapon":null}',
                        e.id, e.kind, jnum(e.time, "%.1f"), e.unit and string.format("%d", e.unit) or "null",
                        e.other and string.format("%d", e.other) or "null")
                end
            end
            return string.format('{"type":"camera_picture","version":1,"time":%s,"theatre":%s,"units":[%s],"weapons":[%s],"events":[%s]}',
                jnum(now, "%.3f"), jstr(env.mission.theatre or ""), table.concat(units, ","), table.concat(weapons, ","), table.concat(events, ","))
        end)
        if ok then return result end
        return "ERR:" .. tostring(result)
    end

    local function focusObject(id, kind, obj)
        local pos = obj:getPosition()
        local v = obj:getVelocity()
        local fx, up, rt = pos.x, pos.y, pos.z
        local hdg = math.deg(math.atan2(fx.z, fx.x))
        if hdg < 0 then hdg = hdg + 360 end
        local pitch = math.deg(math.asin(math.max(-1, math.min(1, fx.y))))
        local roll = math.deg(math.atan2(-rt.y, up.y))  -- right wing down = positive
        local p = pos.p
        return string.format('{"id":%d,"kind":"%s","x":%s,"y":%s,"z":%s,"vx":%s,"vy":%s,"vz":%s,"hdg":%s,"pitch":%s,"roll":%s}',
            id, kind, jnum(p.x, "%.2f"), jnum(p.y, "%.2f"), jnum(p.z, "%.2f"), jnum(v.x, "%.2f"), jnum(v.y, "%.2f"), jnum(v.z, "%.2f"),
            jnum(hdg, "%.2f"), jnum(pitch, "%.2f"), jnum(roll, "%.2f"))
    end

    -- ids: "12,34,1000000005" (digits and commas, checked by the hook). Gone objects are left out.
    function NEBO_cameraFocus(ids)
        local ok, result = pcall(function()
            local out, wanted = {}, {}
            for idText in tostring(ids):gmatch("%d+") do
                local id = tonumber(idText)
                if id >= CAMERA_WEAPON then
                    wanted[id - CAMERA_WEAPON] = id
                else
                    local u = known.byId[id]
                    if u and u:isExist() then out[#out + 1] = focusObject(id, "unit", u) end
                end
                if #out >= 16 then break end
            end
            if next(wanted) then
                for _, m in ipairs(NEBO_missiles) do
                    if wanted[m.id] and liveMissile(m) then out[#out + 1] = focusObject(wanted[m.id], "weapon", m.weapon) end
                end
            end
            return string.format('{"type":"camera_focus","time":%s,"objects":[%s]}', jnum(timer.getTime(), "%.3f"), table.concat(out, ","))
        end)
        if ok then return result end
        return "ERR:" .. tostring(result)
    end

    -- Both pictures at once (dev probes and older hooks).
    return function()
        local ok, result = pcall(function()
            local now = timer.getTime()
            if now >= known.nextScan then rescan(now) end
            local missiles = flyingMissiles()
            return picture(SIDES[1], "red", missiles, now) .. "\n" .. picture(SIDES[2], "blue", missiles, now)
        end)
        if ok then return result end
        return "ERR:" .. tostring(result)
    end
end)()
