-- NEBO DTC — pilot sync. A DCS hook for PILOTS (not the server): copy this file into
--   Saved Games\DCS\Scripts\Hooks\
-- Once you have chosen a coalition on a server (slot or not), it asks that server's NEBO
-- every 10 seconds whether a controller has pushed a data cartridge to you. When one arrives it
-- is written to Saved Games\DCS\DTC\NEBO_<aircraft>.dtc (replacing the last one): load it
-- with Open DTC (the dynamic-slot spawn dialog, or the briefing). DCS applies a cartridge at
-- spawn, so one pushed while you fly is for your next spawn. A server answers only its own
-- players, so with several servers listed the hook asks them in turn until the one you are on
-- answers, then keeps to it until you leave.
-- With NEBO-Export.lua also installed (F-16C), each ask to that server carries your current
-- targeting pod point (and the HAD's emitter ellipse) for your side's controllers who listen, and
-- while the pod has a point the hook asks every 3.5 s instead of 10.
--
-- Your NEBO servers, one line each: the address and the port (the server admin may have
-- filled in theirs). Add a line for every server you fly on.

local SERVERS = {
    { host = "", port = 10309 },
}

local lfs = require("lfs")
local socket = require("socket")

local POLL_S = 10       -- seconds between asks of the server you are on
local DESIGNATION_POLL_S = 3.5  -- while the pod has a point (the server refuses asks < 3 s apart)
local SEARCH_MIN_S = 3  -- while finding it: the next server after POLL_S / servers, at least this
local TIMEOUT_S = 8     -- an ask that takes longer is dropped

-- The servers with an address; a line left empty is skipped.
local servers = {}
for _, s in ipairs(SERVERS) do
    if type(s) == "table" and type(s.host) == "string" and s.host ~= "" then
        servers[#servers + 1] = { host = s.host, port = tonumber(s.port) or 10309 }
    end
end
local function name(s) return s.host .. ":" .. s.port end

local function log(msg) net.log("NEBO DTC: " .. msg) end

local function dtcDir() return lfs.writedir() .. "DTC\\" end

-- The status line last logged: a line only when it changes, not every 10 s.
local lastStatus
local function status(msg)
    if msg ~= lastStatus then log(msg) end
    lastStatus = msg
end

-- ---- The tables the avionics read, compiled by DCS's own DTC code ------------------------------
-- A cartridge's CMDS / RWR / HARM settings come with "Avionics" tables the jet actually uses.
-- DCS's DTC manager rebuilds them on every change; a pushed cartridge was edited outside DCS, so
-- they are rebuilt here with the same DCS functions, in a sandbox of their own (nothing leaks into
-- DCS's GUI environment).
local function sandbox()
    local env = setmetatable({}, { __index = _G })
    env.dofile = function(path)
        local chunk = assert(loadfile(path))
        setfenv(chunk, env)
        return chunk()
    end
    env.print = function() end
    env.onAction = function() end
    env.getCurrentTerrainID = function() return "" end
    return env
end

local COMPILERS = {
    ["FA-18C_hornet"] = function(data)
        local env = sandbox()
        env.data = data
        local root = "./CoreMods/aircraft/FA-18C/DTC/"
        env.dofile("Scripts/Database/wsTypes.lua")  -- the wsType_* constants the threat base uses
        env.dofile(root .. "ALR67/CMDS_defs.lua")
        env.dofile(root .. "ALR67/CMDS.lua")
        env.dofile(root .. "ALR67/RWR.lua")
        env.dofile(root .. "HARM/HARM_SETTINGS_defs.lua")
        env.dofile(root .. "HARM/TOO_CLASS.lua")
        env.makeCMDSTable(env.threats_base, data.ALR67.CMDS.CMDS_Threat_table)
        env.update_RWR_Compiled_Table()
        env.compile_HARM_TOO_CLASS_Avionics()
    end,
}

local function writeCartridge(cartridge, from)
    local unitType = tostring(cartridge.type or "")
    if not unitType:match("^[%w%-_ ]+$") then return status("pushed cartridge has no aircraft type") end
    local compile = COMPILERS[unitType]
    if compile then
        local ok, err = pcall(compile, cartridge.data)
        if not ok then return log("cannot rebuild the " .. unitType .. " threat tables: " .. tostring(err)) end
    end
    lfs.mkdir(dtcDir())
    local path = dtcDir() .. "NEBO_" .. unitType .. ".dtc"
    local f = io.open(path, "wb")
    if not f then return log("cannot write " .. path) end
    f:write(net.lua2json(cartridge))
    f:close()
    log(string.format("new cartridge from %s written: %s", tostring(from), path))
    return true
end

-- ---- Asking the server without stalling the game ---------------------------------------------
-- A non-blocking socket advanced a little every frame: connect, send one line, read until the
-- server closes. Nothing waits on the network, so a flying pilot never gets a hitch.
-- current: the listed server you are on (it answered as one of its players), nil while finding
-- it; try: the next one to ask meanwhile; have: per server, the last cartridge version received
-- (each server counts its own).
local poll = { next = 0, tcp = nil, current = nil, try = 1, have = {} }

-- The player's DCS id once they have chosen a side (a slot is not needed: with dynamic slots the
-- DTC is loaded in the spawn dialog, before there is one); nil in the spectators.
local function myPlayer()
    local me = net.get_my_player_id()
    local side = net.get_player_info(me, "side")
    if not me or not side or side == 0 then return nil end
    return tostring(me)
end

-- The designation NEBO-Export.lua last wrote, as a JSON object, while it is fresh (the pod
-- page still shows a point); nil without one. Numbers and a short track text only.
local DESIGNATION_FRESH_S = 15
local DESIGNATION_KEYS = { "lat", "lon", "alt_ft", "range_nm", "had_major_m", "had_minor_m", "had_pgm", "had_brg" }
local function designation()
    local f = io.open(lfs.writedir() .. "NEBO\\designation.txt", "rb")
    if not f then return nil end
    local text = f:read("*a")
    f:close()
    local v = {}
    for k, val in text:gmatch("([%w_]+)=([^\n]*)") do v[k] = val end
    local written = tonumber(v.time)
    if not written or os.time() - written > DESIGNATION_FRESH_S then return nil end
    local parts = {}
    for _, k in ipairs(DESIGNATION_KEYS) do
        local n = tonumber(v[k])
        if n then parts[#parts + 1] = string.format('"%s":%s', k, (k == "lat" or k == "lon") and string.format("%.6f", n) or string.format("%.1f", n)) end
    end
    if not tonumber(v.lat) or not tonumber(v.lon) then return nil end
    parts[#parts + 1] = string.format('"track":"%s"', tostring(v.track or ""):gsub("[^%w ]", ""):sub(1, 16))
    return "{" .. table.concat(parts, ",") .. "}"
end

local function finish(reason)
    if poll.tcp then poll.tcp:close() end
    poll.tcp = nil
    if reason then status(reason) end
end

local function searchGap() return math.max(SEARCH_MIN_S, POLL_S / #servers) end

-- The server you are on: asked every POLL_S from the ask that found it.
local function found(i)
    if poll.current ~= i then poll.next = poll.started + POLL_S end
    poll.current = i
end

-- A server you were on stops answering as yours (you left it, or it restarted): find it again,
-- starting with the next one.
local function lost(i)
    if poll.current ~= i then return end
    poll.current = nil
    poll.next = DCS.getRealTime() + searchGap()
end

local function handle(line, i)
    local s = servers[i]
    local ok, msg = pcall(net.json2lua, line)
    if not ok or type(msg) ~= "table" then return status("unreadable answer from " .. name(s)) end
    if msg.type == "dtc_fail" then
        if msg.reason == "not_on_a_side" then
            lost(i)
            -- While finding the server you are on, the others saying "not mine" is expected.
            return status(#servers > 1 and "looking for your server among " .. #servers or name(s) .. " says: not_on_a_side")
        end
        return status(name(s) .. " says: " .. tostring(msg.reason))
    end
    if msg.type == "dtc_none" then
        found(i)
        return status("connected to " .. name(s) .. ", no new cartridge")
    end
    if msg.type == "dtc_push" and type(msg.cartridge) == "table" then
        found(i)
        if writeCartridge(msg.cartridge, msg.from) then
            poll.have[i] = tonumber(msg.version) or poll.have[i]
            lastStatus = nil
        end
        return
    end
    status("unexpected answer from " .. name(s))
end

local function step()
    local now = DCS.getRealTime()
    if not poll.tcp then
        local player = myPlayer()
        if #servers == 0 or not player or now < poll.next then return end
        local i = poll.current
        -- Only to the server you are on: the others are still being asked whether you are theirs.
        local des = i and designation() or nil
        if i then
            poll.next = now + (des and DESIGNATION_POLL_S or POLL_S)
        else
            i = (poll.try - 1) % #servers + 1
            poll.try = i % #servers + 1
            poll.next = now + searchGap()
        end
        local s = servers[i]
        local tcp = socket.tcp()
        tcp:settimeout(0)
        tcp:connect(s.host, s.port)  -- in progress: finished below once writable
        poll.tcp, poll.index, poll.started, poll.sent, poll.parts = tcp, i, now, false, {}
        poll.request = string.format('{"type":"dtc_request","version":2,"player":"%s","have":%d%s}\n', player, poll.have[i] or 0,
            des and (',"designation":' .. des) or "")
        return
    end
    local i, s = poll.index, servers[poll.index]
    if now - poll.started > TIMEOUT_S then lost(i) return finish("no answer from " .. name(s)) end
    if not poll.sent then
        local _, writable = socket.select(nil, { poll.tcp }, 0)
        if not writable or #writable == 0 then return end
        local _, err = poll.tcp:send(poll.request)
        if err and err ~= "timeout" then lost(i) return finish("no connection to " .. name(s) .. " (" .. err .. ")") end
        poll.sent = true
    end
    local data, err, partial = poll.tcp:receive("*a")
    local chunk = data or partial
    if chunk and #chunk > 0 then poll.parts[#poll.parts + 1] = chunk end
    if data or err == "closed" then  -- the server answers once and closes
        local line = table.concat(poll.parts):match("^[^\n]+")
        finish()
        if line then handle(line, i) else status("empty answer from " .. name(s)) end
    elseif err ~= "timeout" then
        lost(i)
        finish("connection to " .. name(s) .. " failed (" .. tostring(err) .. ")")
    end
end

local hook = {}
function hook.onSimulationFrame()
    local ok, err = pcall(step)
    if not ok then
        finish()
        log("error: " .. tostring(err))
    end
end
-- Ask at once when the pilot picks a coalition or a slot, or the briefing (with its DTC button) opens.
function hook.onPlayerChangeSlot(id)
    if id == net.get_my_player_id() then poll.next = 0 end
end
function hook.onPlayerChangeCoalition(id)
    if id == net.get_my_player_id() then poll.next = 0 end
end
function hook.onShowBriefing()
    poll.next = 0
end
-- Joining or leaving a server: which listed one you are on is found again, and its counts start over.
local function newServer()
    poll.current, poll.have, poll.next = nil, {}, 0
end
hook.onNetConnect = newServer
hook.onNetDisconnect = newServer
DCS.setUserCallbacks(hook)
local names = {}
for _, s in ipairs(servers) do names[#names + 1] = name(s) end
log("loaded" .. (#servers == 0 and " (no server address set)" or (" for " .. table.concat(names, ", "))))
