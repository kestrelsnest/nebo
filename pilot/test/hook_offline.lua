-- The pilot hook outside DCS, with DCS's GUI functions and LuaSocket stubbed: polling only in a
-- slot, the request it sends, a pushed cartridge arriving in pieces over several frames, the
-- avionics tables rebuilt by DCS's own code, the file written, "nothing new" afterwards.
-- usage (from the DCS folder, so CoreMods are found):
--   cd "E:\DCS World" && bin\luae.exe <repo>\pilot\test\hook_offline.lua <repo>\pilot\NEBO-DTC.lua <default cartridge .json> <temp dir>
local hookFile, cartridgeFile, tmp = arg[1], arg[2], arg[3]:gsub("[\\/]$", "") .. "\\"
os.execute('mkdir "' .. tmp .. 'DTC" 2>nul')

-- ---- A small JSON reader and the writer DCS's net.lua2json behaves like -------------------
local function decode(s)
    local i = 1
    local function ws() i = s:find("[^ \t\r\n]", i) or #s + 1 end
    local value
    local function str()
        local out, j = {}, i + 1
        while true do
            local c = s:sub(j, j)
            if c == '"' then i = j + 1 return table.concat(out) end
            if c == "\\" then
                local e = s:sub(j + 1, j + 1)
                if e == "u" then out[#out + 1] = string.char(tonumber(s:sub(j + 2, j + 5), 16) % 256) j = j + 6
                else out[#out + 1] = ({ n = "\n", t = "\t", r = "\r", b = "\b", f = "\f" })[e] or e j = j + 2 end
            else out[#out + 1] = c j = j + 1 end
        end
    end
    function value()
        ws()
        local c = s:sub(i, i)
        if c == "{" then
            local t = {}
            i = i + 1 ws()
            if s:sub(i, i) == "}" then i = i + 1 return t end
            while true do
                ws() local k = str() ws() i = i + 1  -- ':'
                t[k] = value() ws()
                local d = s:sub(i, i) i = i + 1
                if d == "}" then return t end
            end
        elseif c == "[" then
            local t = {}
            i = i + 1 ws()
            if s:sub(i, i) == "]" then i = i + 1 return t end
            while true do
                t[#t + 1] = value() ws()
                local d = s:sub(i, i) i = i + 1
                if d == "]" then return t end
            end
        elseif c == '"' then return str()
        elseif s:sub(i, i + 3) == "true" then i = i + 4 return true
        elseif s:sub(i, i + 4) == "false" then i = i + 5 return false
        elseif s:sub(i, i + 3) == "null" then i = i + 4 return nil
        else
            local n = s:match("^-?[%d%.eE+-]+", i)
            i = i + #n
            return tonumber(n)
        end
    end
    return value()
end

local function encode(v)
    local t = type(v)
    if t == "table" then
        local n = 0
        for _ in pairs(v) do n = n + 1 end
        if n == #v then
            local out = {}
            for i = 1, #v do out[i] = encode(v[i]) end
            return "[" .. table.concat(out, ",") .. "]"
        end
        local out = {}
        for k, x in pairs(v) do out[#out + 1] = string.format("%q:%s", tostring(k), encode(x)) end
        return "{" .. table.concat(out, ",") .. "}"
    elseif t == "string" then return (string.format("%q", v):gsub("\\\n", "\\n"))
    else return tostring(v) end
end

-- ---- Stubs ---------------------------------------------------------------------------------
local logs = {}
local state = { side = 2, slot = "1001", clock = 100 }
local requests, replies = {}, {}

net = {
    log = function(m) logs[#logs + 1] = m end,
    get_my_player_id = function() return 7 end,
    get_player_info = function(_, k) return k == "side" and state.side or state.slot end,
    json2lua = decode,
    lua2json = encode,
}
local callbacks
DCS = { getRealTime = function() return state.clock end, setUserCallbacks = function(t) callbacks = t end }
-- byHost[host]: a function giving that server's reply (several servers); else the replies queue.
local byHost, asked = {}, {}
package.loaded["socket"] = {
    tcp = function()
        local parts
        return {
            settimeout = function() end,
            connect = function(_, host)
                asked[#asked + 1] = host
                local reply = byHost[host] and byHost[host]() or table.remove(replies, 1) or '{"type":"dtc_none"}\n'
                local half = math.floor(#reply / 2)
                parts = { reply:sub(1, half), reply:sub(half + 1) }
                return nil, "timeout"
            end,
            send = function(_, line) requests[#requests + 1] = line return #line end,
            receive = function()  -- the reply in two pieces, then "closed"
                local p = table.remove(parts, 1)
                if #parts > 0 then return nil, "timeout", p end
                if p then return p end
                return nil, "closed", ""
            end,
            close = function() end,
        }
    end,
    select = function(_, w) return {}, w end,
}
package.loaded["lfs"] = { writedir = function() return tmp end, mkdir = function() return true end }

local src = io.open(hookFile, "rb"):read("*a"):gsub('host = ""', 'host = "127.0.0.1"', 1)
assert(loadstring(src, "=NEBO-DTC.lua"))()

local pass = true
local function expect(name, ok, info)
    print(string.format("%-60s %s %s", name, ok and "PASS" or "FAIL", info or ""))
    pass = pass and ok
end
local function frames(n) for _ = 1, n do callbacks.onSimulationFrame() end end

-- The pushed cartridge: the DCS default with its compiled tables taken out (the hook must rebuild them).
local default = decode(io.open(cartridgeFile, "rb"):read("*a"))
local pushed = decode(io.open(cartridgeFile, "rb"):read("*a"))
pushed.data.ALR67.CMDS.CMDS_Threat_table.CMDS_Avionics_Threat_Table = {}
pushed.data.ALR67.RWR.RWR_Avionics_Threat_Table = {}
pushed.data.HARM.TOO_CLASS_Avionics_Table = {}
pushed.name = "Strike 1"

-- 1. a spectator (no side): nothing asked
state.side, state.slot = 0, ""
frames(5)
expect("spectator: no request", #requests == 0)

-- 2. on a side with no slot yet (dynamic slots: the spawn dialog): one request, the push
-- arrives over several frames, the file is written
state.side = 2
replies[1] = '{"type":"dtc_push","version":5,"from":"Tester","cartridge":' .. encode(pushed) .. '}\n'
callbacks.onPlayerChangeCoalition(7)
frames(6)
expect("request names the player and the version it has", requests[1] == '{"type":"dtc_request","version":2,"player":"7","have":0}\n', requests[1])
local f = io.open(tmp .. "DTC\\NEBO_FA-18C_hornet.dtc", "rb")
expect("cartridge written", f ~= nil, logs[#logs])
local written = f and decode(f:read("*a"))
if f then f:close() end

-- 3. the avionics tables rebuilt by DCS's code equal the ones in DCS's default
local function same(a, b)  -- same structure and values (key order does not matter)
    if type(a) ~= type(b) then return false end
    if type(a) ~= "table" then return a == b end
    for k, v in pairs(a) do if not same(v, b[k]) then return false end end
    for k in pairs(b) do if a[k] == nil then return false end end
    return true
end
if written then
    print(string.format("   rebuilt sizes: CMDS %d (default %d), RWR NORM %d (default %d), HARM %d (default %d)",
        #written.data.ALR67.CMDS.CMDS_Threat_table.CMDS_Avionics_Threat_Table, #default.data.ALR67.CMDS.CMDS_Threat_table.CMDS_Avionics_Threat_Table,
        #(written.data.ALR67.RWR.RWR_Avionics_Threat_Table.NORM or {}), #(default.data.ALR67.RWR.RWR_Avionics_Threat_Table.NORM or {}),
        #written.data.HARM.TOO_CLASS_Avionics_Table, #default.data.HARM.TOO_CLASS_Avionics_Table))
end
if written then
    expect("name kept", written.name == "Strike 1")
    expect("CMDS avionics table rebuilt", #written.data.ALR67.CMDS.CMDS_Threat_table.CMDS_Avionics_Threat_Table > 100
        and same(written.data.ALR67.CMDS.CMDS_Threat_table.CMDS_Avionics_Threat_Table, default.data.ALR67.CMDS.CMDS_Threat_table.CMDS_Avionics_Threat_Table))
    expect("RWR avionics table rebuilt", same(written.data.ALR67.RWR.RWR_Avionics_Threat_Table, default.data.ALR67.RWR.RWR_Avionics_Threat_Table))
    expect("HARM avionics table rebuilt", #written.data.HARM.TOO_CLASS_Avionics_Table > 50
        and same(written.data.HARM.TOO_CLASS_Avionics_Table, default.data.HARM.TOO_CLASS_Avionics_Table))
end

-- 4. ten seconds later: asks with the version it has, "nothing new" is logged once
state.clock = 111
frames(4)
expect("next request carries have=5", requests[2] and requests[2]:find('"have":5') ~= nil, requests[2])
expect("nothing new: said in the log", logs[#logs]:find("no new cartridge") ~= nil, logs[#logs])
local before = #logs
state.clock = 122
frames(4)
expect("same status not logged again", #logs == before)

-- 5. no sandbox leak into the GUI environment
expect("DCS's DTC globals stayed in the sandbox", rawget(_G, "makeCMDSTable") == nil and rawget(_G, "data") == nil)

-- 6. several servers: asks them in turn until the one you are on answers, then keeps to it; the
-- versions are counted per server; leaving it (not_on_a_side) or a new connection searches again
local multi = io.open(hookFile, "rb"):read("*a"):gsub('{ host = "", port = 10309 },',
    '{ host = "a.test", port = 10309 }, { host = "", port = 1 }, { host = "b.test", port = 10320 }, { host = "c.test", port = 10309 },', 1)
assert(loadstring(multi, "=NEBO-DTC.lua (3 servers)"))()
expect("loaded names the three servers (empty line skipped)", logs[#logs]:find("a.test:10309, b.test:10320, c.test:10309", 1, true) ~= nil, logs[#logs])
local notMine = function() return '{"type":"dtc_fail","reason":"not_on_a_side"}\n' end
local onB = true
byHost["a.test"], byHost["c.test"] = notMine, notMine
byHost["b.test"] = function()
    if not onB then return notMine() end
    return '{"type":"dtc_push","version":9,"from":"B ctrl","cartridge":' .. encode(pushed) .. '}\n'
end
asked, requests = {}, {}
state.side = 2
local function at(t) state.clock = t frames(4) end
at(200) at(203.4) at(206.8) at(210.2) at(216.9)
expect("asks a, then b; b answers, so b again 10 s later, never c", table.concat(asked, ",") == "a.test,b.test,b.test", table.concat(asked, ","))
expect("a was asked with have=0", requests[1] ~= nil and requests[1]:find('"have":0') ~= nil, requests[1])
expect("b's next request carries b's version", requests[3] ~= nil and requests[3]:find('"have":9') ~= nil, requests[3])
onB = false
asked = {}
at(227) at(230.4) at(233.8) at(237.2)
expect("left b: searching again, c then a then b", table.concat(asked, ",") == "b.test,c.test,a.test,b.test", table.concat(asked, ","))
onB = true
asked, requests = {}, {}
callbacks.onNetConnect(7)
at(240) at(243.4) at(246.8)
local bHave
for k, r in ipairs(requests) do if asked[k] == "b.test" then bHave = r:match('"have":(%d+)') end end
expect("new connection: versions start over (b asked with have=0)", bHave == "0", table.concat(asked, ","))

-- 7. a designation written by NEBO-Export.lua rides on the asks to the server you are on,
-- only while fresh (15 s), and never while still looking for that server
os.execute('mkdir "' .. tmp .. 'NEBO" 2>nul')
local function designationFile(age)
    local f = assert(io.open(tmp .. "NEBO\\designation.txt", "wb"))
    f:write(string.format("time=%d\nseq=3\nlat=42.567367\nlon=41.628833\nalt_ft=51.0\ntrack=INR POINT\nrange_nm=20.0\nhad_major_m=6482.0\nhad_minor_m=1228.3\nhad_pgm=2\nhad_brg=103\n",
        os.time() - age))
    f:close()
end
designationFile(0)
asked, requests = {}, {}
at(256.9)  -- b is the server you are on (found above): its next ask
expect("fresh designation sent to the server you are on", asked[1] == "b.test" and requests[1] ~= nil
    and requests[1]:find('"designation":{"lat":42.567367,"lon":41.628833,"alt_ft":51.0,"range_nm":20.0,"had_major_m":6482.0,"had_minor_m":1228.3,"had_pgm":2.0,"had_brg":103.0,"track":"INR POINT"}', 1, true) ~= nil,
    requests[1])
asked, requests = {}, {}
at(259.9)
expect("with a designation: no ask before 3.5 s", #asked == 0, table.concat(asked, ","))
at(260.4)
expect("with a designation: the next ask 3.5 s later, with it", asked[1] == "b.test" and requests[1] ~= nil
    and requests[1]:find('"designation":', 1, true) ~= nil, table.concat(asked, ","))
designationFile(60)
requests = {}
at(267)
expect("stale designation (60 s old): not sent", requests[1] ~= nil and not requests[1]:find("designation", 1, true), requests[1])
designationFile(0)
onB = false
asked, requests = {}, {}
at(277) at(280.4)
expect("while looking for your server: never sent", #requests == 2 and not requests[2]:find("designation", 1, true), table.concat(asked, ","))
os.remove(tmp .. "NEBO\\designation.txt")

print(pass and "ALL PASS" or "SOME FAILED")
