-- NEBO server hook (GameGUI env). Loads nebo.dll, injects the collector into the
-- mission scripting env and publishes each coalition's picture every interval.
local lfs = require("lfs")

local NEBO = {
    root = lfs.writedir() .. "Mods\\Services\\NEBO\\",
    cfg = { port = 10309, password = "", blue_password = "", red_password = "",
            -- Discord login (client id, secret, guild id, role ids): see NEBO.example.lua
            discord_client_id = "", discord_secret = "", discord_guild = "",
            discord_roles = {}, discord_blue_roles = {}, discord_red_roles = {},
            interval = 1.0, terrain_step = 2000, dev_probe = false,
            -- a side's pictures while none of its controllers is logged in (0 = every interval)
            idle_interval = 5,
            analytics = true, export = true,
            -- "auto": contacts carry their type once DCS identifies them; "manual": never, the
            -- controllers identify them (NCTR, pilots' calls, "identify" command, shared per coalition)
            identification = "auto",
            -- NCTR (JEM, HRRP, ISAR, rotor) only in missions dated this year or later
            nctr_year = 1985,
            -- Enemy ground units in the picture when own radars see them: true, false, or "auto"
            -- (only in missions dated enemy_ground_year or later: modern AWACS/JSTARS-era sensors).
            -- Otherwise ground threats are found by triangulating pilots' RWR calls.
            enemy_ground = "auto", enemy_ground_year = 2012,
            -- Radar scan model: each own radar refreshes its contacts at its own scan rate
            radar_scan = true,
            -- Controller logins refused (and dropped) from an address that sits in a cockpit
            allow_flying_controllers = false,
            -- Pilot sync: players in a slot may fetch their coalition's live data-cartridge data
            -- (threats, comms, nav points) with the NEBO DTC hook on their own PC
            dtc = true,
            -- Dev aid (test server only): "127.0.0.1=blue" counts that address as a pilot of that side
            dtc_test_pilot = "",
            -- NEBO Camera: its own password ("" = off). A camera login sees every unit of both
            -- sides and the missiles (cinematic shots); refused from any address in a cockpit.
            camera_password = "", camera_interval = 1, camera_focus_hz = 20 },
    -- collector timing, logged once a minute
    stats = { n = 0, total = 0, max = 0, bytes = 0, nextLog = 0 },
    started = false,
    sched = {},  -- next run of each per-interval job (see JOBS)
    watchers = { logins = {} },  -- logged-in controllers and login counts per side (watchControllers)
    lastError = nil,
    collectorReady = false,
    theatreSent = false,
    -- terrain sampling: nil (not started), or { x0, z0, step, nx, nz, nextRow, rowsPerCall, done }
    terrain = nil,
    -- Foothold: exporter injected into the trigger env this mission; last published JSON
    foothold = { injected = false, absent = false, next = 0, last = nil },
    airfields = { next = 0, last = nil },
    weather = { next = 0 },
    marks = { next = 0, last = {} },  -- F10 marks per side: last published line
    -- identities per side: unit id -> { label, time }
    ids = { next = 0, sides = { blue = {}, red = {} }, dirty = {}, sent = {} },
    -- shared threats per side: id -> { x, z, threat, time }
    threats = { nextId = 1, sides = { blue = {}, red = {} }, dirty = {}, sent = {}, launchSites = { blue = {}, red = {} } },
    nctrLast = {},  -- last published NCTR line per side
    pilotsLast = {},  -- last published pilots line per side (DTC tab)
    -- pilots' designations per side: player id -> { name, slot, type, current = { json, at },
    -- emitters = { [n] = { json, at, lat, lon, major } }, currentEmitter, nextEmitter } (real time)
    designations = { next = 0, sides = { blue = {}, red = {} }, published = {} },
    pictures = {},  -- last picture line per side (pilot sync reads its contacts)
    -- NEBO Camera: logins seen, next focus message (real time); see cameraTick/cameraFocusTick
    camera = { logins = 0, count = 0, nextFocus = 0 },
    dtc = { next = 0 },
}

local FOOTHOLD_INTERVAL = 10  -- seconds between Foothold zone snapshots
local AIRFIELDS_INTERVAL = 60 -- seconds between airfield owner checks
local WEATHER_INTERVAL = 60   -- seconds between weather (ATIS) snapshots

-- Control grid size for x/z -> lat/lon on the client. 21 was off by 30-140 m over Caucasus
-- (9° from its central meridian: aircraft beside the runway); 81 keeps it under ~8 m (~130 KB once).
local GEO_GRID = 81
local TERRAIN_BUDGET = 0.004 -- seconds of sim-thread time per frame for terrain sampling

local function neboLog(msg)
    log.write("NEBO", log.INFO, msg)
end

local function readFile(path)
    local f = io.open(path, "rb")
    if not f then return nil end
    local s = f:read("*a")
    f:close()
    return s
end

-- First start: Config\NEBO.lua is made from Config\NEBO.example.lua (shipped next to it) with a
-- new random password for each side, so a fresh install works without editing anything. The
-- admin reads the passwords there (and may change them). An existing NEBO.lua is never touched.
local function createConfig(path)
    local example = readFile(lfs.writedir() .. "Config\\NEBO.example.lua")
    if not example then return false end
    local blue, red, server = nebo.random_hex(10), nebo.random_hex(10), nebo.random_hex(10)
    if not blue or not red or not server then return false end
    local text = example:gsub('\npassword%s*=%s*""', '\npassword = "' .. server .. '"', 1)
        :gsub('blue_password%s*=%s*""', 'blue_password = "' .. blue .. '"', 1)
        :gsub('red_password%s*=%s*""', 'red_password = "' .. red .. '"', 1)
    if readFile(path) then return false end  -- never over an existing config
    local f = io.open(path, "w")
    if not f then return false end
    f:write(text)
    f:close()
    neboLog("created " .. path .. " with new controller passwords for blue and red: give them to your controllers")
    return true
end

-- Coming from 0.15.x (code name DCB): the old config becomes NEBO.lua and the old data folder
-- (terrain grids, analytics key, export) becomes NEBO\, once. An old hook left in Hooks would
-- open the same port, so it is named in the log.
local function migrateFromDcb()
    local w = lfs.writedir()
    local cfg, old = w .. "Config\\NEBO.lua", w .. "Config\\DCB.lua"
    if not readFile(cfg) then
        local text = readFile(old)
        if text then
            local f = io.open(cfg, "wb")
            if f then
                f:write(text)
                f:close()
                neboLog("copied the old settings Config\\DCB.lua to Config\\NEBO.lua (the old file can be deleted)")
            end
        end
    end
    if not lfs.attributes(w .. "NEBO") and lfs.attributes(w .. "DCB", "mode") == "directory" then
        local ok, err = os.rename(w .. "DCB", w .. "NEBO")
        neboLog(ok and "moved the old data folder DCB to NEBO" or ("could not move the old data folder DCB: " .. tostring(err)))
    end
    if lfs.attributes(w .. "Scripts\\Hooks\\DCB-hook.lua") then
        neboLog("WARNING: the old hook Scripts\\Hooks\\DCB-hook.lua is still installed; delete it and Mods\\Services\\DCB, then restart")
    end
end

local function loadConfig()
    migrateFromDcb()
    local path = lfs.writedir() .. "Config\\NEBO.lua"
    local text = readFile(path)
    -- Only a missing file is made from the example: one that does not load (a typo) is the
    -- admin's, and is never overwritten.
    if not text and createConfig(path) then text = readFile(path) end
    if not text then
        neboLog("no config at " .. path .. ", using defaults (logins disabled)")
        return
    end
    text = text:gsub("^\239\187\191", "")  -- a UTF-8 BOM (Notepad, PowerShell) is not Lua
    local chunk, loadErr = loadstring(text, "=NEBO.lua")
    if not chunk then
        neboLog("config error in " .. path .. ": " .. tostring(loadErr) .. " - fix it and restart; logins disabled until then")
        return
    end
    local env = {}
    setfenv(chunk, env)
    local ok, err = pcall(chunk)
    if not ok then
        neboLog("config error: " .. tostring(err))
        return
    end
    for k, v in pairs(env) do
        if NEBO.cfg[k] ~= nil then NEBO.cfg[k] = v end
    end
end

package.cpath = NEBO.root .. "bin\\?.dll;" .. package.cpath
local okRequire, nebo = pcall(require, "nebo")
if not okRequire then
    neboLog("failed to load nebo.dll: " .. tostring(nebo))
    return
end
loadConfig()
NEBO.collectorSource = readFile(NEBO.root .. "Scripts\\collector.lua")
NEBO.footholdSource = readFile(NEBO.root .. "Scripts\\foothold.lua")
neboLog("hook loaded, dll " .. nebo.version())

local function idList(t)
    local out = {}
    for _, v in ipairs(type(t) == "table" and t or {}) do out[#out + 1] = tostring(v) end
    return table.concat(out, ",")
end

local function ensureStarted()
    if NEBO.started or not DCS.isServer() then return end
    local c = NEBO.cfg
    for _, k in ipairs({ "discord_client_id", "discord_guild" }) do
        if type(c[k]) == "number" then neboLog(k .. " must be in quotes (Discord IDs are too long for a number): Discord login off") c[k] = "" end
    end
    nebo.set_auth(c.password, c.blue_password, c.red_password, tostring(c.discord_client_id), c.discord_secret,
        tostring(c.discord_guild), idList(c.discord_roles), idList(c.discord_blue_roles), idList(c.discord_red_roles))
    local discordOn = c.discord_client_id ~= "" and c.discord_secret ~= "" and c.discord_guild ~= ""
    neboLog(discordOn and string.format("Discord login on: guild %s, %d access role(s)", tostring(c.discord_guild), #(c.discord_roles or {}))
        or "Discord login off (password only)")
    if nebo.set_camera then
        nebo.set_camera(tostring(c.camera_password or ""), math.floor(tonumber(c.camera_focus_hz) or 20))
        if (c.camera_password or "") ~= "" then neboLog("camera logins on (camera_password set)") end
    end
    local ok, err = nebo.start(NEBO.cfg.port, NEBO.cfg.blue_password, NEBO.cfg.red_password)
    if ok then
        NEBO.started = true
        neboLog(NEBO.cfg.allow_flying_controllers and "controllers may fly (allow_flying_controllers = true)"
            or "controllers don't fly: logins from an address in a cockpit are refused")
    else
        neboLog("start failed: " .. tostring(err))
    end
end

local function reportError(msg)
    -- Log each distinct error once, not every interval.
    if msg ~= NEBO.lastError then
        neboLog(msg)
        NEBO.lastError = msg
    end
end

-- Other ticks' cost (Foothold export, analytics + website export), logged once a minute as
-- "timing: foothold n×, avg, max, total ms/min; analytics …".
local tickTimes = { nextLog = 0 }

local function timed(name, fn)
    local t0 = DCS.getRealTime()
    local ok, err = pcall(fn)
    local dt = DCS.getRealTime() - t0
    local s = tickTimes[name] or { n = 0, total = 0, max = 0, over20 = 0, over50 = 0 }
    s.n, s.total, s.max = s.n + 1, s.total + dt, math.max(s.max, dt)
    if dt > 0.02 then s.over20 = s.over20 + 1 end
    if dt > 0.05 then s.over50 = s.over50 + 1 end
    tickTimes[name] = s
    local now = DCS.getRealTime()
    if now >= tickTimes.nextLog then
        local names = {}
        for k, v in pairs(tickTimes) do
            if type(v) == "table" and v.n > 0 then names[#names + 1] = k end
        end
        table.sort(names)
        local parts = {}
        for _, k in ipairs(names) do
            local v = tickTimes[k]
            parts[#parts + 1] = string.format("%s %d×, avg %.2f ms, max %.1f ms, %.1f ms/min%s", k, v.n, v.total / v.n * 1000, v.max * 1000, v.total * 1000,
                v.over20 > 0 and string.format(", >20 ms %d×, >50 ms %d×", v.over20, v.over50) or "")
        end
        if #parts > 0 then neboLog("timing: " .. table.concat(parts, "; ")) end
        tickTimes = { nextLog = now + 60 }
    end
    return ok, err
end

local function recordTiming(seconds, bytes)
    local s = NEBO.stats
    s.n, s.total, s.bytes = s.n + 1, s.total + seconds, s.bytes + bytes
    if seconds > s.max then s.max = seconds end
    local now = DCS.getRealTime()
    if now >= s.nextLog then
        if s.n > 0 then
            neboLog(string.format("collect: %d pictures (one side each), avg %.1f ms, max %.1f ms, avg %.0f KB", s.n, s.total / s.n * 1000, s.max * 1000, s.bytes / s.n / 1024))
        end
        NEBO.stats = { n = 0, total = 0, max = 0, bytes = 0, nextLog = now + 60 }
    end
end

-- Dev aid (dev_probe = true in NEBO.lua): runs Saved Games\<instance>\NEBO\probe.lua once and
-- writes what it returns to probe.out. Local files only. A first line "-- env: <state>"
-- picks the net.dostring_in state (default "server").
local function runProbe()
    local dir = lfs.writedir() .. "NEBO\\"
    local code = readFile(dir .. "probe.lua")
    if not code then return end
    os.remove(dir .. "probe.lua")
    local env = code:match("^%-%- env: (%w+)") or "server"
    local t0 = DCS.getRealTime()
    local result
    if env == "hook" then
        -- The hook's own env (dev only): analytics helpers are in NEBO_DEV.
        local chunk, err = loadstring(code)
        if chunk then
            setfenv(chunk, getfenv(1))  -- DCS gives each hook file its own globals
            local ok, r = pcall(chunk)
            result = ok and r or ("ERR: " .. tostring(r))
        else
            result = "ERR: " .. tostring(err)
        end
    else
        result = net.dostring_in(env, code)
    end
    local f = io.open(dir .. "probe.out", "w")
    if f then
        f:write(string.format("-- %.1f ms\n", (DCS.getRealTime() - t0) * 1000), tostring(result))
        f:close()
    end
    neboLog("probe run")
end

-- Where each aircraft type's radar can look, from DCS's sensor database (only this env has it):
-- a Lua table literal { ["F-14B"] = { az, elLow, elHigh }, … } in degrees off the nose, the
-- radar's track volume (else its scan volume). All-round radars (AWACS) and types whose radar is
-- not in the database are left out; the collector gives those ±60° unless they are AWACS.
local function radarLimits()
    local ok, text = pcall(function()
        local sensors = {}
        for _, def in pairs(db.Sensors.Sensor or {}) do
            if type(def) == "table" and def.Name and def.category == 1 then sensors[def.Name] = def end
        end
        local out = {}
        for _, list in ipairs({ db.Units.Planes.Plane, db.Units.Helicopters.Helicopter }) do
            for _, e in pairs(list or {}) do
                local radar = e.Sensors and e.Sensors.RADAR
                local def = sensors[type(radar) == "table" and radar[1] or radar or ""]
                local vol = def and (def.track_volume and def.track_volume.azimuth and def.track_volume or def.scan_volume)
                local az, el = vol and vol.azimuth, vol and vol.elevation
                if az and el and az[2] and az[2] < 180 then
                    out[#out + 1] = string.format("[%q]={%g,%g,%g}", e.type, az[2], el[1], el[2])
                end
            end
        end
        return "{" .. table.concat(out, ",") .. "}"
    end)
    return ok and text or "{}"
end

-- One side's picture per call: blue and red are built on different frames (see the jobs below).
local function collect(sideName)
    local t0 = DCS.getRealTime()
    local result = net.dostring_in("server", string.format("return NEBO_collectSide and NEBO_collectSide(%q) or 'NOCOLLECTOR'", sideName))
    if type(result) == "string" and result:sub(1, 1) == "{" then recordTiming(DCS.getRealTime() - t0, #result) end
    if result == "NOCOLLECTOR" then
        if not NEBO.collectorSource then
            reportError("collector.lua missing")
            return
        end
        local eg = NEBO.cfg.enemy_ground
        net.dostring_in("server", string.format("NEBO_settings = { enemyGround = %s, enemyGroundYear = %d, identification = %q, nctrYear = %d, radarScan = %s, radarLimits = %s }\n",
            eg == true and "true" or eg == false and "false" or '"auto"', tonumber(NEBO.cfg.enemy_ground_year) or 2012,
            NEBO.cfg.identification == "manual" and "manual" or "auto", tonumber(NEBO.cfg.nctr_year) or 1985,
            NEBO.cfg.radar_scan == false and "false" or "true", radarLimits())
            .. NEBO.collectorSource)
        neboLog("collector injected")
        return
    end
    if type(result) ~= "string" or result:sub(1, 1) ~= "{" then
        reportError("collect failed: " .. tostring(result))
        return
    end
    NEBO.pictures[sideName] = result
    nebo.publish(sideName, result)
    NEBO.collectorReady = true
    NEBO.lastError = nil
end

-- ---- Theatre: bounds, georeference grid, towns ------------------------------------------

local function jstr(s)
    s = tostring(s):gsub('[%c"\\]', function(c)
        if c == '"' then return '\\"' end
        if c == "\\" then return "\\\\" end
        return string.format("\\u%04x", c:byte())
    end)
    return '"' .. s .. '"'
end

-- Terrain config bounds come as {x=, z=} or {x, y, z}/{x, z} depending on version, in km
-- (Caucasus: x -600..380, z -560..1130); returned in metres.
local function boundXZ(b)
    if type(b) ~= "table" then return nil end
    local x, z
    if b.x then x, z = b.x, b.z or b.y
    elseif #b >= 3 then x, z = b[1], b[3]
    else x, z = b[1], b[2] end
    if not (x and z) then return nil end
    return x * 1000, z * 1000
end

local function readTowns(id)
    local chunk = loadfile("Mods\\terrains\\" .. id .. "\\Map\\towns.lua")
    if not chunk then return {} end
    local env = { require = function() return { translate = function(s) return s end } end }
    setfenv(chunk, env)
    if not pcall(chunk) or type(env.towns) ~= "table" then return {} end
    local list = {}
    for name, t in pairs(env.towns) do
        if type(t) == "table" and t.latitude and t.longitude then
            list[#list + 1] = string.format("[%s,%.5f,%.5f]", jstr(t.display_name or name), t.latitude, t.longitude)
        end
    end
    return list
end

-- Every unit type DCS knows with its wsType (the first four of its attributes), from the Mission
-- Editor's unit database: the client's data cartridge names many threats only by wsType (RWR and
-- HARM tables) and turns them into unit types with this. Read from the server's own DCS at run
-- time, so NEBO ships none of Eagle Dynamics' data; sent to every controller at login.
local function publishUnitTypes()
    local db = me_db_api and me_db_api.unit_by_type
    if type(db) ~= "table" then return end
    local items = {}
    for name, u in pairs(db) do
        local a = type(u) == "table" and u.attribute
        if type(a) == "table" and tonumber(a[1]) and tonumber(a[2]) and tonumber(a[3]) and tonumber(a[4]) then
            items[#items + 1] = string.format("%s:[%d,%d,%d,%d]", jstr(name), a[1], a[2], a[3], a[4])
        end
    end
    table.sort(items)
    nebo.publish_all("unit_types", '{"type":"unit_types","version":1,"types":{' .. table.concat(items, ",") .. "}}")
    neboLog(string.format("unit types published: %d", #items))
end

local function buildTheatre()
    local terrain = require("terrain")
    local id = terrain.GetTerrainConfig("id")
    local sx, sz = boundXZ(terrain.GetTerrainConfig("SW_bound"))
    local nx, nz = boundXZ(terrain.GetTerrainConfig("NE_bound"))
    if not (id and sx and sz and nx and nz) then
        reportError("terrain bounds unavailable")
        return
    end
    local x0, x1 = math.floor(math.min(sx, nx)), math.ceil(math.max(sx, nx))
    local z0, z1 = math.floor(math.min(sz, nz)), math.ceil(math.max(sz, nz))
    neboLog(string.format("theatre %s x %d..%d z %d..%d", id, x0, x1, z0, z1))

    local grid = net.dostring_in("server", string.format("return NEBO_geoGrid(%d,%d,%d,%d,%d)", x0, z0, x1, z1, GEO_GRID))
    if type(grid) ~= "string" or not grid:find(",", 1, true) then
        reportError("geo grid failed: " .. tostring(grid))
        return
    end
    local lats, lons = {}, {}
    for lat, lon in grid:gmatch("([%-%d%.]+),([%-%d%.]+)") do
        lats[#lats + 1] = lat
        lons[#lons + 1] = lon
    end
    local towns = readTowns(id)
    -- Magnetic variation over the theatre's lat/lon box, 0.5° steps (it varies slowly).
    local latMin, latMax, lonMin, lonMax = 90, -90, 180, -180
    for i = 1, #lats do
        local la, lo = tonumber(lats[i]), tonumber(lons[i])
        latMin, latMax = math.min(latMin, la), math.max(latMax, la)
        lonMin, lonMax = math.min(lonMin, lo), math.max(lonMax, lo)
    end
    local step = 0.5
    local lat0, lon0 = math.floor(latMin / step) * step, math.floor(lonMin / step) * step
    local nlat, nlon = math.ceil((latMax - lat0) / step) + 1, math.ceil((lonMax - lon0) / step) + 1
    local mv = net.dostring_in("server", string.format("return NEBO_magvarGrid(%.2f,%.2f,%.2f,%d,%d)", lat0, lon0, step, nlat, nlon))
    local magvar = "null"
    if type(mv) == "string" and mv:find(",", 1, true) then
        magvar = string.format('{"lat0":%.2f,"lon0":%.2f,"step":%.2f,"nlat":%d,"nlon":%d,"deg":[%s]}',
            lat0, lon0, step, nlat, nlon, (mv:gsub(";", ",")))
    else
        reportError("magvar grid failed: " .. tostring(mv))
    end
    nebo.publish_all("theatre", string.format(
        '{"type":"theatre","id":%s,"x0":%d,"z0":%d,"x1":%d,"z1":%d,"grid":{"n":%d,"lat":[%s],"lon":[%s]},"magvar":%s,"towns":[%s]}',
        jstr(id), x0, z0, x1, z1, GEO_GRID, table.concat(lats, ","), table.concat(lons, ","), magvar, table.concat(towns, ",")))
    NEBO.theatreSent = true
    neboLog(string.format("theatre published: %d towns", #towns))
    local okU, errU = pcall(publishUnitTypes)
    if not okU then reportError("unit types: " .. tostring(errU)) end

    local step = math.max(250, math.floor(NEBO.cfg.terrain_step))
    local t = { id = id, x0 = x0, z0 = z0, step = step, nextRow = 0, rowsPerCall = 2 }
    t.nx = math.floor((x1 - x0) / step) + 1
    t.nz = math.floor((z1 - z0) / step) + 1
    local dir = lfs.writedir() .. "NEBO"
    lfs.mkdir(dir)
    local cache = string.format("%s\\terrain_%s_%d.bin", dir, id, step)
    t.done = nebo.terrain_begin(id, x0, z0, step, t.nx, t.nz, cache)
    t.started = DCS.getRealTime()
    NEBO.terrain = t
    neboLog(string.format("terrain %dx%d at %d m: %s", t.nx, t.nz, step, t.done and "from cache" or "sampling"))
end

-- Samples a few terrain rows per frame, keeping to TERRAIN_BUDGET of sim-thread time.
local function sampleTerrain()
    local t = NEBO.terrain
    local t0 = DCS.getRealTime()
    local rows = net.dostring_in("server", string.format("return NEBO_terrainRows(%d,%d,%d,%d,%d,%d,%d)",
        t.x0, t.z0, t.step, t.nx, t.nz, t.nextRow, t.rowsPerCall))
    if type(rows) ~= "string" or rows == "" then
        reportError("terrain sampling failed: " .. tostring(rows))
        t.done = true
        return
    end
    for row in rows:gmatch("[^\n]+") do
        local csv, digits = row:match("^(.-)|(%d*)$")
        if not (csv and nebo.terrain_row(t.nextRow, csv, digits)) then
            reportError("bad terrain row " .. t.nextRow)
            t.done = true
            return
        end
        t.nextRow = t.nextRow + 1
    end
    local spent = DCS.getRealTime() - t0
    if spent < TERRAIN_BUDGET / 2 then t.rowsPerCall = t.rowsPerCall + 1
    elseif spent > TERRAIN_BUDGET and t.rowsPerCall > 1 then t.rowsPerCall = t.rowsPerCall - 1 end
    if t.nextRow >= t.nx then
        t.done = true
        local ok = nebo.terrain_finish()
        neboLog(string.format("terrain sampled in %.0f s: %s", DCS.getRealTime() - t.started, ok and "published" or "incomplete"))
    end
end

-- ---- Foothold (optional): zone ownership and links, read-only ----------------------------

-- Foothold runs in the trigger scripting env; reach it with a_do_script through the
-- 'mission' state. The exporter pushes its result into the 'server' state.
local function footholdTick()
    local fh = NEBO.foothold
    if fh.absent then return end
    local now = DCS.getRealTime()
    if now < fh.next then return end
    fh.next = now + FOOTHOLD_INTERVAL
    if not fh.injected then
        if not NEBO.footholdSource then return end
        net.dostring_in("mission", "a_do_script(" .. string.format("%q", NEBO.footholdSource) .. ")")
        fh.injected = true
    end
    net.dostring_in("mission", 'a_do_script("if NEBO_footholdPush then NEBO_footholdPush() end")')
    local json = net.dostring_in("server", "local j = NEBO_footholdJson; NEBO_footholdJson = nil; return j or ''")
    if json == "none" then
        fh.absent = true
        neboLog("no Foothold in this mission")
    elseif type(json) == "string" and json:sub(1, 4) == "ERR:" then
        reportError("foothold export failed: " .. json:sub(5))
    elseif type(json) == "string" and json:sub(1, 1) == "{" and json ~= fh.last then
        -- Credits tick with Foothold's income every 10 s: a change in credits alone is passed on
        -- at most once a minute; zones, links and missions at once.
        local key = json:gsub(',"credits":%b{}', "")
        if fh.lastKey == key and now < (fh.nextCredits or 0) then return end
        if not fh.last then neboLog(string.format("Foothold detected: publishing zones (%d KB)", math.floor(#json / 1024))) end
        fh.last, fh.lastKey, fh.nextCredits = json, key, now + 60
        nebo.publish_all("foothold", json)
    end
end

-- ---- Airfields: runways and parking (layout once), owners re-checked ----------------------

local function airfieldsTick()
    local af = NEBO.airfields
    local now = DCS.getRealTime()
    if now < af.next then return end
    af.next = now + AIRFIELDS_INTERVAL
    local t0 = DCS.getRealTime()
    local json = net.dostring_in("server", "return NEBO_airfields and NEBO_airfields() or ''")
    if type(json) ~= "string" or json:sub(1, 1) ~= "{" then
        reportError("airfields failed: " .. tostring(json))
        return
    end
    if json ~= af.last then
        if not af.last then
            neboLog(string.format("airfields published (%d KB, %.0f ms)", math.floor(#json / 1024), (DCS.getRealTime() - t0) * 1000))
        end
        af.last = json
        nebo.publish_all("airfields", json)
    end
end

-- ---- Weather for ATIS (public), once a minute -------------------------------------------

local function weatherTick()
    local wx = NEBO.weather
    local now = DCS.getRealTime()
    if now < wx.next then return end
    wx.next = now + WEATHER_INTERVAL
    local json = net.dostring_in("server", "return NEBO_weather and NEBO_weather() or ''")
    if type(json) ~= "string" or json:sub(1, 1) ~= "{" then
        reportError("weather failed: " .. tostring(json))
        return
    end
    nebo.publish_all("weather", json)
end

-- ---- F10 map marks (coalition channels), checked every 5 s, sent when they change ----------

local MARKS_INTERVAL = 5

local function marksTick()
    local mk = NEBO.marks
    local now = DCS.getRealTime()
    if now < mk.next then return end
    mk.next = now + MARKS_INTERVAL
    local result = net.dostring_in("server", "return NEBO_marks and NEBO_marks() or ''")
    if type(result) ~= "string" or result == "" then return end
    if result:sub(1, 4) == "ERR:" then
        reportError("marks failed: " .. result)
        return
    end
    for line in result:gmatch("[^\n]+") do
        local side = line:match('"coalition":"(%a+)"')
        if side and mk.last[side] ~= line then
            mk.last[side] = line
            nebo.publish_side(side, "marks", line)
        end
    end
end

-- ---- Fast LSO feed (coalition channels), 10 Hz while aircraft are near own carriers ------
-- NEBO_lso() returns one JSON line per coalition that has aircraft near its carriers (or '').

local LSO_INTERVAL = 0.1
local LSO_IDLE = 1  -- while no aircraft is near a carrier (10 NM out, 1 s late is nothing)

local function lsoTick()
    local now = DCS.getRealTime()
    if now < (NEBO.nextLso or 0) then return end
    NEBO.nextLso = now + LSO_INTERVAL
    local result = net.dostring_in("server", "return NEBO_lso and NEBO_lso() or ''")
    if type(result) ~= "string" or result == "" then
        NEBO.nextLso = now + LSO_IDLE  -- 10 calls a second cost ~90 ms a minute for nothing
        return
    end
    if result:sub(1, 4) == "ERR:" then
        reportError("lso failed: " .. result)
        return
    end
    for line in result:gmatch("[^\n]+") do
        local side = line:match('"coalition":"(%a+)"')
        if side then nebo.publish_side(side, "lso", line) end
    end
end

-- ---- NEBO Camera (camera channels: camera logins only) --------------------------------
-- camera_picture every camera_interval s while a camera is logged in (a JOBS entry; a new login
-- gets one at once); camera_focus at the rate the cameras asked for (camera_set_focus, at most
-- camera_focus_hz) for the objects they film, built every frame it is due like the LSO feed.
local function cameraTick()
    local result = net.dostring_in("server", "return NEBO_cameraPicture and NEBO_cameraPicture() or ''")
    if type(result) ~= "string" or result == "" then return end
    if result:sub(1, 4) == "ERR:" then
        reportError("camera picture failed: " .. result)
        return
    end
    nebo.publish_camera("camera_picture", result)
end

local function cameraFocusTick()
    local cam = NEBO.camera
    if cam.count == 0 then return end
    local now = DCS.getRealTime()
    if now < cam.nextFocus then return end
    local _, _, ids, hz = nebo.camera()
    if ids == "" or hz <= 0 or not ids:match("^[%d,]+$") then
        cam.nextFocus = now + 0.1
        return
    end
    -- On a fixed schedule, not "now + period": that rounds every period up to whole server
    -- frames (20 Hz asked came out as 16 Hz with 90 ms gaps on the test server).
    cam.nextFocus = math.max(cam.nextFocus, now - 1 / hz) + 1 / hz
    local result = net.dostring_in("server", "return NEBO_cameraFocus and NEBO_cameraFocus('" .. ids .. "') or ''")
    if type(result) ~= "string" or result == "" then return end
    if result:sub(1, 4) == "ERR:" then
        reportError("camera focus failed: " .. result)
        return
    end
    nebo.publish_camera("camera_focus", result)
end

-- ---- Identities (coalition channels): contacts the side's controllers identified ---------
-- Set by the "identify" command; checked every 10 s (gone units drop out) and sent when they
-- change, with the server's identification mode so clients know whether types are hidden.

local IDS_INTERVAL = 10

local function idsTick()
    local ids = NEBO.ids
    local now = DCS.getRealTime()
    if now < ids.next then return end
    ids.next = now + IDS_INTERVAL
    for _, sideName in ipairs({ "blue", "red" }) do
        local side = ids.sides[sideName]
        local list = {}
        for unit in pairs(side) do list[#list + 1] = tostring(unit) end
        if #list > 0 then
            local alive = {}
            local result = net.dostring_in("server", string.format("return NEBO_identifyAlive(%q, %q)", table.concat(list, ","), sideName))
            for id in tostring(result):gmatch("%d+") do alive[tonumber(id)] = true end
            for unit in pairs(side) do
                if not alive[unit] then
                    side[unit] = nil
                    ids.dirty[sideName] = true
                end
            end
        end
        if ids.dirty[sideName] or not ids.sent[sideName] then
            local items = {}
            for unit, e in pairs(side) do
                items[#items + 1] = string.format('{"unit":%d,"label":"%s","time":%.1f}', unit, e.label, e.time)
            end
            nebo.publish_side(sideName, "ids", string.format('{"type":"ids","coalition":"%s","mode":"%s","ids":[%s]}',
                sideName, NEBO.cfg.identification == "manual" and "manual" or "auto", table.concat(items, ",")))
            ids.dirty[sideName], ids.sent[sideName] = false, true
        end
    end
end

-- ---- Shared threats (coalition channels): threats the side's controllers placed ----------
-- Not F10 marks (the pilots do not see them); sent to the side when they change and at once
-- after a login (the DLL keeps the last line). Gone with the mission.

local MAX_THREATS = 200

local function threatsTick()
    local th = NEBO.threats
    for _, sideName in ipairs({ "blue", "red" }) do
        if th.dirty[sideName] or not th.sent[sideName] then
            local items = {}
            for id, t in pairs(th.sides[sideName]) do
                items[#items + 1] = string.format('{"id":%d,"x":%d,"z":%d,"threat":"%s","time":%.1f}', id, t.x, t.z, t.threat, t.time)
            end
            nebo.publish_side(sideName, "threats", string.format('{"type":"threats","coalition":"%s","threats":[%s]}',
                sideName, table.concat(items, ",")))
            th.dirty[sideName], th.sent[sideName] = false, true
        end
    end
end

-- Launch marks: an enemy ground unit firing at a side's unit gives that side a shared "SA-?"
-- threat (SA-8 ring) near the launcher, one per launching group. The first launch places it
-- LAUNCH_BLUR_M off in a random direction; each further launch shrinks the offset linearly,
-- so the LAUNCH_EXACT-th launch puts it on the launcher. It goes LAUNCH_LIFE_S after
-- the group's last launch, or when a controller removes it (the next launch brings it back).
local LAUNCH_BLUR_M, LAUNCH_EXACT, LAUNCH_LIFE_S = 1000, 5, 15 * 60

local function launchesTick()
    local th = NEBO.threats
    local now = DCS.getModelTime()
    local result = net.dostring_in("server", "return NEBO_takeLaunches and NEBO_takeLaunches() or ''")
    if type(result) == "string" and result:sub(1, 4) == "ERR:" then reportError("launches failed: " .. result) end
    for line in (type(result) == "string" and result or ""):gmatch("[^\n]+") do
        local sideName, group, x, z = line:match("^(%a+),(%d+),(%-?%d+),(%-?%d+)$")
        local sites = sideName and th.launchSites[sideName]
        if sites then
            group, x, z = tonumber(group), tonumber(x), tonumber(z)
            local s = sites[group]
            if not s then
                local a = math.random() * 2 * math.pi
                s = { count = 0, dx = math.cos(a), dz = math.sin(a) }
                sites[group] = s
            end
            s.count, s.last = s.count + 1, now
            local off = LAUNCH_BLUR_M * math.max(0, LAUNCH_EXACT - s.count) / (LAUNCH_EXACT - 1)
            local side = th.sides[sideName]
            if not (s.id and side[s.id]) then
                local n = 0
                for _ in pairs(side) do n = n + 1 end
                if n < MAX_THREATS then
                    s.id = th.nextId
                    th.nextId = s.id + 1
                else
                    s.id = nil
                end
            end
            if s.id then
                side[s.id] = { x = math.floor(x + s.dx * off + 0.5), z = math.floor(z + s.dz * off + 0.5), threat = "SA-?", time = now, launch = true }
                th.dirty[sideName] = true
            end
            neboLog(string.format("launch mark for %s: group %d, launch %d, %.0f m off", sideName, group, s.count, off))
        end
    end
    -- Gone LAUNCH_LIFE_S after the group's last launch.
    for sideName, sites in pairs(th.launchSites) do
        for group, s in pairs(sites) do
            if now - s.last > LAUNCH_LIFE_S then
                if s.id and th.sides[sideName][s.id] then
                    th.sides[sideName][s.id] = nil
                    th.dirty[sideName] = true
                end
                sites[group] = nil
            end
        end
    end
    threatsTick()
end

-- ---- NCTR (coalition channels): the side's requests and results, sent when they change ---

local function nctrTick()
    local result = net.dostring_in("server", "return NEBO_nctr and NEBO_nctr() or ''")
    if type(result) ~= "string" or result == "" then return end
    if result:sub(1, 4) == "ERR:" then
        reportError("nctr failed: " .. result)
        return
    end
    for line in result:gmatch("[^\n]+") do
        local side = line:match('"coalition":"(%a+)"')
        if side and NEBO.nctrLast[side] ~= line then
            NEBO.nctrLast[side] = line
            nebo.publish_side(side, "nctr", line)
        end
    end
end

-- ---- Controller commands ------------------------------------------------------------------
-- Already checked by the DLL (known name, field limits, rate). Each runs a fixed function in
-- the mission env with the controller's values as arguments (%q-quoted strings, %d numbers),
-- answers the controller, and is logged with who asked for what.

-- Pilot sync: a player's key for pushed cartridges, stable for this server run, not their UCID.
local PUSH_KEY = nebo.random_hex(16) or tostring(os.time())
local function pilotKey(ucid) return (nebo.hmac_hex(PUSH_KEY, tostring(ucid or "")) or ""):sub(1, 24) end

-- A controller pushes a whole data cartridge to pilots (DCS player ids): each must be on the
-- controller's side and, once in a slot, in the cartridge's aircraft type (with dynamic slots the
-- type is chosen only at spawn). Their hook fetches it.
local function pushCartridge(cmd)
    local okJ, c = pcall(net.json2lua, cmd.cartridge)
    if not okJ or type(c) ~= "table" or type(c.data) ~= "table" or type(c.type) ~= "string" then
        return false, "not a data cartridge"
    end
    local from = cmd.address:match("^%S+%s+(.-)%s*%[%d+%]$") or ("a " .. cmd.coalition .. " controller")
    NEBO.pushVersion = (NEBO.pushVersion or os.time()) + 1
    local side = cmd.coalition == "blue" and 2 or 1
    local sent, refused = {}, {}
    for _, player in ipairs(cmd.units) do
        local id = tonumber(player)
        local who = id and id ~= 1 and net.get_player_info(id)
        local slot = who and who.slot ~= "" and tostring(who.slot) or nil
        local okT, flies = pcall(DCS.getUnitType, slot or "")
        if not who then
            refused[#refused + 1] = "player " .. player .. ": not on the server"
        elseif tonumber(who.side) ~= side then
            refused[#refused + 1] = who.name .. ": not on your side"
        elseif slot and okT and flies and flies ~= "" and flies ~= c.type then
            refused[#refused + 1] = who.name .. ": flies " .. tostring(flies)
        else
            nebo.set_pushed(pilotKey(who.ucid), NEBO.pushVersion, from, cmd.cartridge, c.type)  -- one per aircraft type
            sent[#sent + 1] = who.name
            -- On screen when flying; before spawn the pilot's hook writes the file and logs it.
            local unit = slot and tonumber(slot:match("^%d+"))
            if unit then
                net.dostring_in("server", string.format("return NEBO_messageUnit(%d, %q, %d, %q)", unit,
                    "NEBO: new data cartridge from " .. from .. ". Load NEBO_" .. c.type
                        .. " from the DTC dialog before your next spawn.", 20, cmd.coalition))
            end
        end
    end
    local notTo = #refused > 0 and ("; not to " .. table.concat(refused, "; ")) or ""
    if #sent == 0 then return false, table.concat(refused, "; ") end
    return true, "sent to " .. table.concat(sent, ", ") .. notTo
end

local function runCommand(cmd)
    if not NEBO.collectorReady then return false, "server not ready yet" end
    if cmd.name == "dtc_push" then return pushCartridge(cmd) end
    if cmd.name == "message_unit" then
        local result = net.dostring_in("server", string.format("return NEBO_messageUnit(%d, %q, %d, %q)",
            cmd.unit, cmd.text, cmd.seconds, cmd.coalition))
        if result == "ok" then return true end
        return false, tostring(result)
    end
    if cmd.name == "mark_add" then
        local result = tostring(net.dostring_in("server", string.format("return NEBO_markAdd(%d, %d, %q, %q, %q)",
            cmd.x, cmd.z, cmd.text, cmd.threat or "", cmd.coalition)))
        local id = result:match("^ok:(%d+)$")
        if id then
            NEBO.marks.next = 0  -- send the new mark to the side's controllers at once
            return true, id
        end
        return false, result
    end
    if cmd.name == "carrier_lights" then
        -- Checked in the server env (own coalition's carrier), set in the mission env, where the
        -- Mission Editor's SET CARRIER LIGHTING MODE lives. Verified in MP on CVN-72 and Stennis.
        local check = tostring(net.dostring_in("server", string.format("return NEBO_carrierLightsCheck(%d, %q)", cmd.unit, cmd.coalition)))
        if check ~= "ok" then return false, check end
        local result = tostring(net.dostring_in("mission", string.format(
            "return tostring(pcall(a_set_carrier_illumination_mode, %d, %d))", cmd.unit, cmd.mode)))
        if result:sub(1, 4) == "true" then return true end
        return false, "lights not set: " .. result
    end
    if cmd.name == "nctr" then
        local result = tostring(net.dostring_in("server", string.format("return NEBO_nctrStart(%q, %d, %d, %d, %d, %q)",
            cmd.coalition, cmd.x, cmd.z, cmd.x2, cmd.z2, cmd.text)))
        local id = result:match("^ok:(%d+)$")
        if id then return true, id end
        return false, result
    end
    if cmd.name == "threat_add" then
        local side = NEBO.threats.sides[cmd.coalition]
        local n = 0
        for _ in pairs(side) do n = n + 1 end
        if n >= MAX_THREATS then return false, "too many shared threats (" .. MAX_THREATS .. ")" end
        local id = NEBO.threats.nextId
        NEBO.threats.nextId = id + 1
        side[id] = { x = cmd.x, z = cmd.z, threat = cmd.threat, time = DCS.getModelTime() }
        NEBO.threats.dirty[cmd.coalition] = true
        threatsTick()
        return true, tostring(id)
    end
    if cmd.name == "threat_remove" then
        local side = NEBO.threats.sides[cmd.coalition]
        if not side[cmd.mark] then return false, "no such threat of your coalition" end
        side[cmd.mark] = nil
        NEBO.threats.dirty[cmd.coalition] = true
        threatsTick()
        return true
    end
    if cmd.name == "identify" then
        local check = tostring(net.dostring_in("server", string.format("return NEBO_identifyCheck(%d, %q)", cmd.unit, cmd.coalition)))
        if check ~= "ok" then return false, check end
        local side = NEBO.ids.sides[cmd.coalition]
        local label = cmd.text:match("^%s*(.-)%s*$")
        side[cmd.unit] = label ~= "" and { label = label, time = DCS.getModelTime() } or nil
        NEBO.ids.dirty[cmd.coalition] = true
        NEBO.ids.next = 0  -- to the side's controllers at once
        return true
    end
    if cmd.name == "mark_remove" then
        local result = net.dostring_in("server", string.format("return NEBO_markRemove(%d, %q)", cmd.mark, cmd.coalition))
        if result == "ok" then
            NEBO.marks.next = 0
            return true
        end
        return false, tostring(result)
    end
    return false, "unknown command"
end

-- ---- Controllers don't fly ----------------------------------------------------------------
-- Unless allow_flying_controllers = true (NEBO.lua), the DLL refuses controller logins from the
-- address of any player sitting in a cockpit and closes such a controller's open connection: a
-- pilot cannot watch the coalition's picture while flying. Checked every FLYING_S seconds.
local FLYING_S = 2
NEBO.flyingNext = 0

local function flyingTick()
    local now = DCS.getRealTime()
    if now < NEBO.flyingNext then return end
    NEBO.flyingNext = now + FLYING_S
    local addresses, slots = {}, {}
    local pilots = { red = {}, blue = {} }
    for _, id in ipairs(net.get_player_list() or {}) do
        local info = id ~= 1 and net.get_player_info(id)  -- 1: the server itself
        local side = info and tonumber(info.side)
        local host = info and tostring(info.ipaddr or ""):match("^([^:]+)")
        local inSlot = info and info.slot and info.slot ~= ""
        if inSlot and side and side ~= 0 and host and host ~= "" then addresses[#addresses + 1] = host end
        -- Pilot sync: everyone who chose a side, slot or not. With dynamic slots the cartridge is
        -- loaded in the spawn dialog, before the player has a slot (and an aircraft in the picture).
        if side and side ~= 0 then
            local sideName = side == 1 and "red" or "blue"
            if host and host ~= "" then
                slots[#slots + 1] = { host = host, side = sideName, player = tostring(id), key = pilotKey(info.ucid) }
            end
            local okT, unitType = pcall(DCS.getUnitType, inSlot and info.slot or "")
            local list = pilots[sideName]
            list[#list + 1] = string.format('{"id":%d,"player":%s,"slot":%s,"type":%s}', id, jstr(info.name or "?"),
                jstr(inSlot and tostring(info.slot) or ""), jstr(inSlot and okT and unitType or ""))
        end
    end
    for sideName, list in pairs(pilots) do
        local line = string.format('{"type":"pilots","coalition":"%s","pilots":[%s]}', sideName, table.concat(list, ","))
        if line ~= NEBO.pilotsLast[sideName] then
            NEBO.pilotsLast[sideName] = line
            nebo.publish_side(sideName, "pilots", line)
        end
    end
    nebo.set_flying(NEBO.cfg.allow_flying_controllers and {} or addresses)
    if nebo.set_cockpits then nebo.set_cockpits(addresses) end  -- cameras: always refused from a cockpit
    local list = {}
    if NEBO.cfg.dtc then
        -- Dev: dtc_test_pilot = "127.0.0.1=blue" lets a hook on the server's own PC stand for
        -- that side's players (their DCS connects from another local address).
        local test, testSide = tostring(NEBO.cfg.dtc_test_pilot or ""):match("^([%d%.]+)=(%a+)$")
        for _, s in ipairs(slots) do
            list[#list + 1] = table.concat({ s.host, s.side, s.player, s.key }, "=")
            if test and s.side == testSide then list[#list + 1] = table.concat({ test, s.side, s.player, s.key }, "=") end
        end
    end
    nebo.set_pilots(list)
end

-- ---- Pilots' designations (F-16 targeting pod / HAD, via the pilot hook) ---------------------
-- Each pilot's latest designation, and every emitter they designated on the HAD (the HAD's own
-- emitter symbols cannot be read: the pilot builds the picture by designating them in turn), for
-- their coalition's controllers ("designations" channel, docs/PROTOCOL.md). Each is dropped
-- DESIGNATION_KEEP_S after it last came. The DLL has already checked and rebuilt each one; the
-- player's name, slot and aircraft are added here.
local DESIGNATION_TICK_S = 1
local DESIGNATION_KEEP_S = 600
local DESIGNATION_REPUBLISH_S = 10  -- ages move on: republish a non-empty list this often
local DESIGNATION_EMITTERS = 20     -- kept per pilot
local DESIGNATION_SAME_M = 2000     -- a new HAD point this close to a kept one (or within half the larger ellipse) refines it

local function designationPoint(json)
    local lat, lon = tonumber(json:match('"lat":([%d%.%-]+)')), tonumber(json:match('"lon":([%d%.%-]+)'))
    return lat, lon, tonumber(json:match('"had_major_m":([%d%.]+)'))
end

-- Metres between two points as the pod shows them (unsigned degrees, same hemisphere).
local function designationGap(lat1, lon1, lat2, lon2)
    local k = math.pi / 180
    local x = (lon2 - lon1) * k * math.cos((lat1 + lat2) / 2 * k)
    return math.sqrt(x * x + ((lat2 - lat1) * k) ^ 2) * 6371000
end

-- A new designation for a pilot's entry: the latest point, and for a HAD one the emitter it
-- refines (the kept one it falls inside) or a new emitter. Returns the emitter's number or nil.
local function designationKeep(e, json, now)
    e.current = { json = json, at = now }
    local lat, lon, major = designationPoint(json)
    if not (lat and lon and major) then e.currentEmitter = nil return nil end
    local best, bestGap
    for n, em in pairs(e.emitters) do
        local gap = designationGap(em.lat, em.lon, lat, lon)
        if gap <= math.max(DESIGNATION_SAME_M, math.max(em.major, major) / 2) and (not bestGap or gap < bestGap) then best, bestGap = n, gap end
    end
    if not best then
        local count, oldest = 0, nil
        for n, em in pairs(e.emitters) do
            count = count + 1
            if not oldest or em.at < e.emitters[oldest].at then oldest = n end
        end
        if count >= DESIGNATION_EMITTERS then e.emitters[oldest] = nil end
        e.nextEmitter = e.nextEmitter + 1
        best = e.nextEmitter
    end
    e.emitters[best] = { json = json, at = now, lat = lat, lon = lon, major = major }
    e.currentEmitter = best
    return best
end

local function designationsTick()
    local d = NEBO.designations
    local now = DCS.getRealTime()
    if now < d.next or not nebo.designations then return end  -- (a DLL before 0.15 has none)
    d.next = now + DESIGNATION_TICK_S
    local changed = {}
    for _, item in ipairs(nebo.designations()) do
        local id = tonumber(item.player)
        local info = id and net.get_player_info(id)
        local side = d.sides[item.coalition]
        if info and side then
            local slot = info.slot and tostring(info.slot) or ""
            local okT, unitType = pcall(DCS.getUnitType, slot)
            local e = side[id] or { emitters = {}, nextEmitter = 0 }
            e.name, e.slot, e.type = info.name or "?", slot, okT and unitType or ""
            designationKeep(e, item.json, now)
            side[id] = e
            changed[item.coalition] = true
        end
    end
    for sideName, list in pairs(d.sides) do
        for id, e in pairs(list) do
            for n, em in pairs(e.emitters) do
                if now - em.at > DESIGNATION_KEEP_S then e.emitters[n] = nil changed[sideName] = true end
            end
            if e.current and now - e.current.at > DESIGNATION_KEEP_S then e.current = nil changed[sideName] = true end
            if not e.current and not next(e.emitters) then list[id] = nil end
        end
        local last = d.published[sideName]
        if changed[sideName] or (next(list) and (not last or now - last >= DESIGNATION_REPUBLISH_S)) then
            local items = {}
            -- The rebuilt object with the pilot added: {"player":…,"name":…,…,"age":s,"emitter":n,"current":b,"lat":…}
            local function add(id, e, x, emitter, current)
                items[#items + 1] = string.format('{"player":%d,"name":%s,"slot":%s,"aircraft":%s,"age":%.0f,%s"current":%s,%s', id,
                    jstr(e.name), jstr(e.slot), jstr(e.type), now - x.at, emitter and string.format('"emitter":%d,', emitter) or "",
                    current and "true" or "false", x.json:sub(2))
            end
            for id, e in pairs(list) do
                local currentKept = e.current and e.currentEmitter and e.emitters[e.currentEmitter]
                if e.current and not currentKept then add(id, e, e.current, nil, true) end  -- a plain pod point
                for n, em in pairs(e.emitters) do add(id, e, em, n, currentKept and n == e.currentEmitter) end
            end
            nebo.publish_side(sideName, "designations", string.format('{"type":"designations","coalition":"%s","designations":[%s]}',
                sideName, table.concat(items, ",")))
            d.published[sideName] = now
        end
    end
end

-- ---- DTC live data: each coalition's, every DTC_INTERVAL seconds, for its controllers -------
-- What the DTC tab fills a cartridge from (docs/PROTOCOL.md "dtc_live"; cartridges themselves
-- are pushed by controllers, see pushCartridge): threats the coalition knows (identified enemy air defence and ship
-- contacts, controllers' shared threats and identities), its AWACS/tanker and tower frequencies,
-- its bullseye and airdromes. Positions in DCS map metres.
local DTC_INTERVAL = 10
local MAX_DTC_THREATS = 120

-- Tower frequencies by airdrome id, from the theatre's radio.lua (read once per theatre).
local towerCache = {}
local function towersOf(theatre)
    if towerCache[theatre] then return towerCache[theatre] end
    local towers = {}
    local text = readFile(lfs.currentdir():gsub("[\\/]$", "") .. "\\Mods\\terrains\\" .. theatre .. "\\Radio.lua") or ""
    local starts = {}
    for pos, id in text:gmatch("()radioId%s*=%s*'airfield(%d+)_%d+'") do starts[#starts + 1] = { pos = pos, id = tonumber(id) } end
    for i, s in ipairs(starts) do
        local block = text:sub(s.pos, starts[i + 1] and starts[i + 1].pos - 1 or #text)
        local f = {}
        for band, mod, hz in block:gmatch("%[(%u[%u_]*)%]%s*=%s*{%s*MODULATIONTYPE_(%u+)%s*,%s*([%d%.]+)%s*}") do
            f[band] = { mhz = tonumber(hz) / 1e6, mod = mod == "FM" and 1 or 0 }
        end
        towers[s.id] = f
    end
    towerCache[theatre] = towers
    return towers
end

local function dtcSide(sideName)
    local data = net.dostring_in("server", string.format("return NEBO_dtcData and NEBO_dtcData(%q) or ''", sideName))
    if type(data) ~= "string" or data:sub(1, 1) ~= "{" then return nil, tostring(data) end
    local okD, d = pcall(net.json2lua, data)
    if not okD or type(d) ~= "table" then return nil, "dtc data unreadable" end

    -- Threats: identified enemy air defence and ships in the coalition's picture; contacts the
    -- controllers identified; their shared threats. Type names as DCS's, display names from the
    -- unit database (the pilot's hook matches them to its aircraft's threat list).
    local threats = {}
    local function add(t) if #threats < MAX_DTC_THREATS then threats[#threats + 1] = t end end
    local db = me_db_api and me_db_api.unit_by_type or {}
    local ids = NEBO.ids.sides[sideName] or {}
    local found = {}  -- { type, name, lat, lon }: contacts carry lat/lon, cartridges map metres
    -- The picture's radar contacts, read from its text: a contact is a flat object that starts
    -- {"id":n,"name":null (own units have names). Parsing the whole picture (own units included,
    -- up to 300 KB on a Foothold server) cost tens of ms on the server's frame.
    for obj in (NEBO.pictures[sideName] or ""):gmatch('{"id":%d+,"name":null,[^{}]*}') do
        local cat = obj:match('"category":"(%a+)"')
        local lat, lon = tonumber(obj:match('"lat":([%-%d%.]+)')), tonumber(obj:match('"lon":([%-%d%.]+)'))
        if (cat == "ground" or cat == "ship") and lat and lon then
            local id = tonumber(obj:match('^{"id":(%d+)'))
            local typ = obj:match('"type":"([^"]*)"')
            local def = typ and db[typ]
            local label = ids[id] and ids[id].label
            if def and (def.category == "Air Defence" or cat == "ship") then
                found[#found + 1] = { type = typ, name = def.DisplayName or typ, lat = lat, lon = lon }
            elseif label then
                found[#found + 1] = { type = "", name = label, lat = lat, lon = lon }
            end
        end
    end
    if #found > 0 then
        local ll = {}
        for i, f in ipairs(found) do ll[i] = string.format("%.6f,%.6f", f.lat, f.lon) end
        local xz = tostring(net.dostring_in("server", string.format("return NEBO_llToXz(%q)", table.concat(ll, ";"))))
        local i = 0
        for x, z in xz:gmatch("([%-%d]+),([%-%d]+)") do
            i = i + 1
            local f = found[i]
            if f then add(string.format('{"type":%s,"name":%s,"x":%s,"z":%s}', jstr(f.type), jstr(f.name), x, z)) end
        end
    end
    for _, t in pairs(NEBO.threats.sides[sideName] or {}) do
        add(string.format('{"type":"","name":%s,"x":%d,"z":%d}', jstr(t.threat), t.x, t.z))
    end

    -- Comms: AWACS and tankers, then the own airdromes' towers (UHF, else VHF); one preset per
    -- frequency (missions often give several AWACS groups the same one).
    local comms, used = {}, {}
    local function addComm(name, mhz, mod)
        local key = string.format("%.3f", mhz)
        if used[key] then return end
        used[key] = true
        comms[#comms + 1] = string.format('{"name":%s,"mhz":%.3f,"mod":%d}', jstr(name), mhz, mod or 0)
    end
    for _, c in ipairs(d.comms or {}) do addComm(c.name, c.mhz, c.mod) end
    local towers = towersOf(d.theatre or "")
    local points = {}
    if type(d.bullseye) == "table" then
        points[#points + 1] = string.format('{"name":"BULLSEYE","x":%.0f,"z":%.0f,"alt":0}', d.bullseye.x, d.bullseye.z)
    end
    for _, a in ipairs(d.airfields or {}) do
        local f = towers[a.id] or {}
        local tower = f.UHF or f.VHF_HI
        if tower then addComm(a.name, tower.mhz, tower.mod) end
        points[#points + 1] = string.format('{"name":%s,"x":%.0f,"z":%.0f,"alt":%.0f}', jstr(a.name), a.x, a.z, a.alt or 0)
    end
    return string.format('{"type":"dtc_live","version":1,"coalition":"%s","theatre":%s,"time":"%s","threats":[%s],"comms":[%s],"points":[%s]}',
        sideName, jstr(d.theatre or ""), os.date("!%Y-%m-%dT%H:%M:%SZ"), table.concat(threats, ","), table.concat(comms, ","), table.concat(points, ","))
end

local function dtcTick()
    if not NEBO.cfg.dtc or not NEBO.collectorReady then return end
    local now = DCS.getRealTime()
    if now < NEBO.dtc.next then return end
    -- One side per run, the other half an interval later: each side every DTC_INTERVAL.
    NEBO.dtc.next = now + DTC_INTERVAL / 2
    local sideName = NEBO.dtc.side == "blue" and "red" or "blue"
    NEBO.dtc.side = sideName
    local json, err = dtcSide(sideName)
    -- For the coalition's controllers (the DTC tab fills presets and threats from it).
    if json then nebo.publish_side(sideName, "dtc_live", json) else reportError("dtc failed: " .. tostring(err)) end
end

local function commandsTick()
    for _, cmd in ipairs(nebo.commands()) do
        local okRun, ok, err = pcall(runCommand, cmd)
        if not okRun then ok, err = false, "server error: " .. tostring(ok) end
        nebo.command_result(cmd.client, cmd.id, ok and true or false, err)
        neboLog(string.format("command %s by %s (%s) unit %d mark %d mode %d, %d chars: %s",
            cmd.name, cmd.address, cmd.coalition, cmd.unit, cmd.mark or 0, cmd.mode or 0, #cmd.text,
            ok and ("done" .. (err and (" " .. err) or "")) or ("refused: " .. tostring(err))))
    end
end

-- ---- Analytics log and public export (both local files on the server) ---------------------
-- Analytics: Saved Games\<server>\NEBO\analytics\<UTC start>_<mission>.jsonl, one JSON object
-- per line, every event of a mission session. Players appear as {"id","name"}: id = the first
-- 16 hex digits of HMAC-SHA256(server key, UCID); the key (NEBO\analytics.key) is made once and
-- never leaves the server, so ids are stable across name changes but do not reveal the UCID.
-- Export (for a website, docs/ANALYTICS.md): NEBO\export\state.json (Foothold zones with
-- lat/lon, missions, credits, top players; checked every 10 s, written when it changes) and NEBO\export\events.jsonl
-- (the public events, names only: no ids, no positions).

local AN = { file = nil, players = {}, byName = {}, key = nil, nextExport = 0, statsPublic = nil }
local PUBLIC_KINDS = { kill = true, takeoff = true, landing = true, crash = true, eject = true,
    zone_captured = true, mission_started = true, mission_ended = true }

local function neboDir(sub)
    local d = lfs.writedir() .. "NEBO\\"
    lfs.mkdir(d)
    if sub then
        d = d .. sub .. "\\"
        lfs.mkdir(d)
    end
    return d
end

local function utcNow() return os.date("!%Y-%m-%dT%H:%M:%SZ") end

local function analyticsKey()
    if AN.key then return AN.key end
    local path = neboDir() .. "analytics.key"
    local k = readFile(path)
    k = k and k:match("^%s*(%x+)%s*$")
    if not k or #k < 32 then
        k = nebo.random_hex(32)
        local f = io.open(path, "w")
        if f then
            f:write(k)
            f:close()
        end
    end
    AN.key = k
    return k
end

local function playerId(ucid)
    if not ucid or ucid == "" then return nil end
    local h = nebo.hmac_hex(analyticsKey(), ucid)
    return h and h:sub(1, 16) or nil
end

local function playerJson(id, name)
    return string.format('{"id":%s,"name":%s}', id and jstr(id) or "null", jstr(name or "?"))
end

local function appendLine(path, line)
    local f = io.open(path, "a")
    if not f then return end
    f:write(line, "\n")
    f:close()
end

-- One event: into the session's analytics file, and (public kinds, ids removed) into the export.
local function record(json, kind)
    local line = '{"utc":' .. jstr(utcNow()) .. "," .. json:sub(2)
    if NEBO.cfg.analytics and AN.file then
        AN.file:write(line, "\n")
        AN.file:flush()
    end
    if NEBO.cfg.export and PUBLIC_KINDS[kind] then
        appendLine(neboDir("export") .. "events.jsonl", (line:gsub('"id":"%x+",', ""):gsub('"id":null,', "")))
    end
end

local function sessionOpen()
    if not (NEBO.cfg.analytics or NEBO.cfg.export) then return end
    local mission = tostring(DCS.getMissionName and DCS.getMissionName() or "mission")
    if NEBO.cfg.analytics then
        local safe = mission:gsub("[^%w%-_.]", "_"):sub(1, 60)
        AN.file = io.open(neboDir("analytics") .. os.date("!%Y%m%d-%H%M%S") .. "_" .. safe .. ".jsonl", "a")
    end
    AN.mission = mission
    record(string.format('{"kind":"session_start","mission":%s,"nebo":%s}', jstr(mission), jstr(nebo.version())), "session_start")
end

local function sessionClose()
    if AN.file then
        record('{"kind":"session_end"}', "session_end")
        AN.file:close()
        AN.file = nil
    end
end

-- Names from the mission env → {id, name}; Foothold's raw UCIDs → ids (never written raw).
local function enrich(line)
    line = line:gsub('"player":"(.-)"', function(name)
        local p = AN.byName[name]
        return '"player":' .. playerJson(p and p.id, p and p.name or name)
    end)
    line = line:gsub('"ucid":"(.-)"', function(ucid) return '"id":' .. jstr(playerId(ucid) or "") end)
    return line
end

local function analyticsTick()
    if not (NEBO.cfg.analytics or NEBO.cfg.export) then return end
    local batch = net.dostring_in("server", "return NEBO_analyticsDrain and NEBO_analyticsDrain() or ''") or ""
    local fh = net.dostring_in("server", "local e = NEBO_footholdEvents; NEBO_footholdEvents = nil; return e or ''") or ""
    for _, text in ipairs({ batch, fh }) do
        for raw in tostring(text):gmatch("[^\n]+") do
            if raw:sub(1, 1) == "{" then
                local line = enrich(raw)
                local kind = line:match('"kind":"([%w_]+)"') or "?"
                if kind == "foothold_stats" then
                    -- Keep the scoreboard for the export (names only).
                    AN.statsPublic = line:match('"players":(%b[])')
                    if AN.statsPublic then AN.statsPublic = AN.statsPublic:gsub('"id":"%x*",', "") end
                end
                record(line, kind)
            end
        end
    end
    -- The public state for a website, every 10 s: Foothold's picture, missions, credits, scores.
    local now = DCS.getRealTime()
    if NEBO.cfg.export and now >= AN.nextExport then
        AN.nextExport = now + 10
        local fhJson = NEBO.foothold and NEBO.foothold.last
        -- Written only when something in it changed; "updated" is the time of that change.
        local content = string.format('"mission":%s,"foothold":%s,"players":%s}', jstr(AN.mission or ""), fhJson or "null", AN.statsPublic or "[]")
        if content == AN.lastState then return end
        AN.lastState = content
        local state = '{"updated":' .. jstr(utcNow()) .. "," .. content
        local dir = neboDir("export")
        local tmp = dir .. "state.json.tmp"
        local f = io.open(tmp, "w")
        if f then
            f:write(state)
            f:close()
            os.remove(dir .. "state.json")
            os.rename(tmp, dir .. "state.json")
        end
    end
end

-- Dev aid only (dev_probe = true, never on a live server): the analytics helpers for probes.
if NEBO.cfg.dev_probe then
    NEBO_DEV = { AN = AN, playerId = playerId, enrich = enrich, record = record }
end

local callbacks = {}

function callbacks.onPlayerConnect(id)
    pcall(function()
        local info = net.get_player_info(id)
        if not info then return end
        local p = { id = playerId(info.ucid), name = info.name }
        AN.players[id] = p
        AN.byName[info.name] = p
        record(string.format('{"kind":"connect","player":%s}', playerJson(p.id, p.name)), "connect")
    end)
end

function callbacks.onPlayerDisconnect(id)
    pcall(function()
        local p = AN.players[id]
        if not p then return end
        record(string.format('{"kind":"disconnect","player":%s}', playerJson(p.id, p.name)), "disconnect")
        AN.players[id] = nil
    end)
end

function callbacks.onPlayerChangeSlot(id)
    pcall(function()
        local info = net.get_player_info(id)
        local p = AN.players[id]
        if not info or not p then return end
        local unitType = info.slot and info.slot ~= "" and DCS.getUnitType and DCS.getUnitType(info.slot) or nil
        record(string.format('{"kind":"slot","player":%s,"side":%d,"unit":%s}', playerJson(p.id, p.name),
            tonumber(info.side) or 0, unitType and jstr(unitType) or "null"), "slot")
    end)
end

function callbacks.onSimulationStop()
    pcall(sessionClose)
end

function callbacks.onSimulationStart()
    pcall(nebo.clear_pushed)  -- pushed cartridges belong to the mission that ended
    pcall(sessionClose)
    local okA, errA = pcall(sessionOpen)
    if not okA then neboLog("analytics: " .. tostring(errA)) end
    ensureStarted()
    NEBO.sched = {}  -- the per-interval jobs start again at their phases
    -- A new mission may be on another terrain; the collector is re-injected as well.
    NEBO.collectorReady = false
    NEBO.theatreSent = false
    NEBO.terrain = nil
    NEBO.foothold = { injected = false, absent = false, next = 0, last = nil }
    NEBO.airfields = { next = 0, last = nil }
    NEBO.weather = { next = 0 }
    NEBO.marks = { next = 0, last = {} }
    NEBO.ids = { next = 0, sides = { blue = {}, red = {} }, dirty = {}, sent = {} }
    NEBO.threats = { nextId = 1, sides = { blue = {}, red = {} }, dirty = {}, sent = {}, launchSites = { blue = {}, red = {} } }
    NEBO.nctrLast = {}
    NEBO.pilotsLast = {}
    NEBO.designations = { next = 0, sides = { blue = {}, red = {} }, published = {} }
end

-- ---- Heavy jobs: at most one per server frame -------------------------------------------
-- Every job keeps its own timer; when several are due in the same frame the first in the list
-- runs and the others wait for the next frame (~16 ms). Before, every job of the second ran in the
-- frame of the picture and their times added up (50-115 ms frames on Foothold servers). The jobs
-- without a timer of their own run once per interval, started at their phase of the second.
local function every(name, phase, period)
    return {
        due = function(now)
            local n = NEBO.sched[name]
            if not n then
                n = now + phase * NEBO.cfg.interval
                NEBO.sched[name] = n
            end
            return now >= n
        end,
        mark = function(now) NEBO.sched[name] = now + (period and period() or NEBO.cfg.interval) end,
    }
end

-- Pictures while nobody watches: a side with none of its controllers logged in gets a picture
-- every idle_interval s instead of every interval (most of a server's day); a login gets one at
-- once. The radar model keeps each contact's last paint, so nothing is lost in between; pilots'
-- DTC threats are at most idle_interval s older. An older DLL without nebo.clients(): always every
-- interval.
local function pictureEvery(side)
    return function()
        local idle = tonumber(NEBO.cfg.idle_interval) or 0
        if not nebo.clients or idle <= NEBO.cfg.interval or (NEBO.watchers[side] or 0) > 0 then return NEBO.cfg.interval end
        return idle
    end
end

local function ownTimer(get)  -- a job whose own tick keeps its timer (get() returns its next time)
    return { due = function(now) return now >= (get() or 0) end, mark = function() end }
end

local function ready() return NEBO.collectorReady end
local function live() return NEBO.collectorReady and NEBO.theatreSent end

local JOBS = {
    { name = "picture blue", when = every("blue", 0, pictureEvery("blue")), run = function() collect("blue") end },
    { name = "picture red", when = every("red", 0.5, pictureEvery("red")), cond = ready, run = function() collect("red") end },
    { name = "rescan", when = every("rescan", 0.25), cond = ready, run = function()
        local r = net.dostring_in("server", "return NEBO_rescan and NEBO_rescan() or ''")
        if type(r) == "string" and r:sub(1, 4) == "ERR:" then reportError("rescan failed: " .. r) end
    end },
    { name = "theatre", when = ownTimer(function() return 0 end), cond = function() return NEBO.collectorReady and not NEBO.theatreSent end,
      run = function()
          local ok, err = pcall(buildTheatre)
          if not ok then
              reportError("theatre error: " .. tostring(err))
              NEBO.theatreSent = true  -- do not retry every second
          end
      end },
    { name = "foothold", when = ownTimer(function() return not NEBO.foothold.absent and NEBO.foothold.next or math.huge end), cond = live, run = footholdTick },
    { name = "airfields", when = ownTimer(function() return NEBO.airfields.next end), cond = live, run = airfieldsTick },
    { name = "weather", when = ownTimer(function() return NEBO.weather.next end), cond = live, run = weatherTick },
    { name = "marks", when = ownTimer(function() return NEBO.marks.next end), cond = ready, run = marksTick },
    { name = "ids", when = ownTimer(function() return NEBO.ids.next end), cond = ready, run = idsTick },
    { name = "dtc", when = ownTimer(function() return NEBO.dtc.next end), cond = function() return NEBO.cfg.dtc and NEBO.collectorReady end, run = dtcTick },
    { name = "flying", when = ownTimer(function() return NEBO.flyingNext end), run = flyingTick },
    { name = "designations", when = ownTimer(function() return NEBO.designations.next end), run = designationsTick },
    { name = "analytics", when = every("analytics", 0.6), cond = ready, run = analyticsTick },
    { name = "threats", when = every("threats", 0.7), cond = ready, run = launchesTick },
    { name = "nctr", when = every("nctr", 0.8), cond = ready, run = nctrTick },
    { name = "probe", when = every("probe", 0.9), cond = function() return NEBO.cfg.dev_probe and NEBO.collectorReady end, run = runProbe },
    { name = "camera", when = every("camera", 0.4, function() return tonumber(NEBO.cfg.camera_interval) or 1 end),
      cond = function() return NEBO.collectorReady and NEBO.camera.count > 0 end, run = cameraTick },
}
local CAMERA_JOB = JOBS[#JOBS]

-- Controllers per side from the DLL (a C call, every frame): a new login makes that side's
-- picture due now; a side gaining or losing its last controller is logged.
local function watchControllers(now)
    if not nebo.clients then return end
    local w = NEBO.watchers
    local b, r, bl, rl = nebo.clients()
    for _, s in ipairs({ { "blue", b, bl, JOBS[1] }, { "red", r, rl, JOBS[2] } }) do
        local side, count, logins, job = s[1], s[2], s[3], s[4]
        if logins > (w.logins[side] or 0) then
            NEBO.sched[side] = now
            job.wait = 0
        end
        if (count > 0) ~= ((w[side] or 0) > 0) and (tonumber(NEBO.cfg.idle_interval) or 0) > NEBO.cfg.interval then
            neboLog(string.format("%s pictures every %s s (%s)", side, count > 0 and tostring(NEBO.cfg.interval) or tostring(NEBO.cfg.idle_interval),
                count > 0 and "controller logged in" or "no controller"))
        end
        w[side], w.logins[side] = count, logins
    end
    if nebo.camera then
        local cam = NEBO.camera
        local count, logins = nebo.camera()
        if logins > cam.logins then  -- a new camera: its picture now
            NEBO.sched.camera = now
            CAMERA_JOB.wait = 0
        end
        if (count > 0) ~= (cam.count > 0) then neboLog(count > 0 and "camera logged in: camera pictures on" or "no camera: camera pictures off") end
        cam.count, cam.logins = count, logins
    end
end

local function frameTick()
    for _, line in ipairs(nebo.poll()) do neboLog(line) end
    -- Commands every frame: a controller's call reaches the player without waiting a second.
    local okCmd, errCmd = timed("commands", commandsTick)
    if not okCmd then reportError("commands error: " .. tostring(errCmd)) end
    if NEBO.terrain and not NEBO.terrain.done then
        local ok, err = pcall(sampleTerrain)
        if not ok then
            reportError("terrain error: " .. tostring(err))
            NEBO.terrain.done = true
        end
    end
    if NEBO.collectorReady then
        local okLso, errLso = timed("lso", lsoTick)
        if not okLso then reportError("lso error: " .. tostring(errLso)) end
        if nebo.camera then
            local okCam, errCam = timed("camera focus", cameraFocusTick)
            if not okCam then reportError("camera focus error: " .. tostring(errCam)) end
        end
    end
    local now = DCS.getRealTime()
    watchControllers(now)
    for _, job in ipairs(JOBS) do
        -- job.wait: a job runs at most once per interval, also when its tick returns early
        -- without moving its timer (it would otherwise take every frame).
        if now >= (job.wait or 0) and (not job.cond or job.cond()) and job.when.due(now) then
            job.when.mark(now)
            job.wait = now + NEBO.cfg.interval * 0.9
            local ok, err = timed(job.name, job.run)
            if not ok then reportError(job.name .. " error: " .. tostring(err)) end
            return
        end
    end
end

-- NEBO's whole share of each server frame, logged with the jobs as "frame".
function callbacks.onSimulationFrame()
    if not NEBO.started then return end
    local ok, err = timed("frame", frameTick)
    if not ok then reportError("frame error: " .. tostring(err)) end
end

DCS.setUserCallbacks(callbacks)
