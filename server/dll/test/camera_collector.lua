-- The collector's camera functions outside DCS: a stubbed mission scripting env (two jets, a
-- tank, a ship, one missile in flight, a kill event), then NEBO_cameraPicture() and
-- NEBO_cameraFocus() printed as JSON lines for camera_collector_test.py to check.
-- usage: "E:\DCS World\bin\luae.exe" camera_collector.lua <collector.lua>
local t = 1234.5
timer = { getTime = function() return t end }
env = { mission = { theatre = "Syria", date = { Year = 2016 } }, info = function() end }
coord = { LOtoLL = function(p) return 33 + p.x / 1e5, 36 + p.z / 1e5, p.y end }
coalition = { side = { NEUTRAL = 0, RED = 1, BLUE = 2 }, getMainRefPoint = function() return { x = 0, y = 0, z = 0 } end }
Group = { Category = { AIRPLANE = 0, HELICOPTER = 1, GROUND = 2, SHIP = 3 } }
Unit = { Category = { AIRPLANE = 0, HELICOPTER = 1, GROUND_UNIT = 2, SHIP = 3, STRUCTURE = 4 }, SensorType = { RADAR = 1 } }
Weapon = { Category = { SHELL = 0, MISSILE = 1 } }
Object = { Category = { UNIT = 1, WEAPON = 2 }, getCategory = function(o) return o.cat end }
local handlers = {}
world = {
    event = { S_EVENT_SHOT = 1, S_EVENT_HIT = 2, S_EVENT_TAKEOFF = 3, S_EVENT_LAND = 4, S_EVENT_CRASH = 5, S_EVENT_EJECTION = 6,
              S_EVENT_BIRTH = 15, S_EVENT_PILOT_DEAD = 9, S_EVENT_PLAYER_LEAVE_UNIT = 21, S_EVENT_KILL = 28 },
    addEventHandler = function(h) handlers[#handlers + 1] = h end,
    removeEventHandler = function() end,
}
local function fire(e) for _, h in ipairs(handlers) do h:onEvent(e) end end

-- An object: position p, velocity v, forward f, up u, right r (unit vectors).
local function object(o)
    o.cat = o.cat or Object.Category.UNIT
    o.isExist = function() return not o.dead end
    o.isActive = function() return true end
    o.getID = function() return tostring(o.id) end  -- DCS gives ids as strings
    o.getName = function() return o.name end
    o.getTypeName = function() return o.type end
    o.getCallsign = function() return "" end
    o.getPlayerName = function() return o.player end
    o.getCoalition = function() return o.side end
    o.getPoint = function() return { x = o.p[1], y = o.p[2], z = o.p[3] } end
    o.getVelocity = function() return { x = o.v[1], y = o.v[2], z = o.v[3] } end
    o.getPosition = function()
        local f, u, r = o.f or { 1, 0, 0 }, o.u or { 0, 1, 0 }, o.r or { 0, 0, 1 }
        return { p = o.getPoint(), x = { x = f[1], y = f[2], z = f[3] }, y = { x = u[1], y = u[2], z = u[3] }, z = { x = r[1], y = r[2], z = r[3] } }
    end
    o.getDesc = function() return { category = o.ucat } end
    o.getGroup = function() return o.group end
    o.getController = function() return { getDetectedTargets = function() return {} end } end
    o.hasSensors = function() return false end
    o.getRadar = function() return false end
    o.getTarget = function() return o.target end
    return o
end
local function group(name, units)
    local g = { name = name, units = units }
    g.isExist = function() return true end
    g.getName = function() return name end
    g.getID = function() return #name end
    g.getUnits = function() return units end
    g.getController = function() return { getDetectedTargets = function() return {} end } end
    for _, u in ipairs(units) do u.group = g end
    return g
end

local s2 = math.sqrt(0.5)
local viper = object({ id = 16777473, name = "Kestrel 1-1", type = "F-16C_50", player = "Bullat", side = 2, ucat = 0,
    p = { -281000, 6100, 640000 }, v = { 0, 0, -231 }, f = { 0, 0, -1 }, u = { s2, s2, 0 }, r = { s2, -s2, 0 } })  -- west, 45 deg right bank
local flanker = object({ id = 16777999, name = "Su-27 #1", type = "Su-27", side = 1, ucat = 0,
    p = { -270000, 7000, 600000 }, v = { -200, 0, 0 }, f = { -1, 0, 0 } })
local tank = object({ id = 501, name = "T-72 #1", type = "T-72B", side = 1, ucat = 2, p = { -260000, 300, 590000 }, v = { 0, 0, 0 } })
local ship = object({ id = 777, name = "CVN-74", type = "Stennis", side = 2, ucat = 3, p = { -300000, 0, 650000 }, v = { 10, 0, 0 } })
local groups = {
    [2] = { [0] = { group("Kestrel 1", { viper }) }, [3] = { group("CSG", { ship }) } },
    [1] = { [0] = { group("Flanker", { flanker }) }, [2] = { group("Armor", { tank }) } },
}
coalition.getGroups = function(side, cat) return (groups[side] or {})[cat] or {} end

local hook = io.open(arg[1]):read("*a")
NEBO_settings = {}
assert(loadstring(hook))()

local aim120 = object({ cat = Object.Category.WEAPON, id = 9, type = "AIM_120C", side = 2, p = { -280500, 6050, 638000 },
    v = { 0, -10, -980 }, f = { 0, 0, -1 }, target = flanker })
aim120.getDesc = function() return { category = Weapon.Category.MISSILE } end
fire({ id = world.event.S_EVENT_SHOT, initiator = viper, weapon = aim120 })
t = t + 5
fire({ id = world.event.S_EVENT_KILL, initiator = viper, target = tank })
t = t + 1

if arg[2] then
    -- Serve mode: nebo.dll from arg[2] on port arg[3] with camera password arg[4] for arg[5] s,
    -- fed by the collector exactly like NEBO-hook.lua (camera_picture every second while a camera
    -- is logged in, camera_focus at the rate and ids the camera asked for). The jet flies on.
    package.cpath = arg[2] .. "\\?.dll;" .. package.cpath
    local nebo = require("nebo")
    io.stdout:setvbuf("no")
    nebo.set_auth("", "", "", "", "", "", "", "", "")
    nebo.set_camera(arg[4], 20)
    print("start:", nebo.start(tonumber(arg[3]), "", ""))
    NEBO_rescan()
    local c0, t0, nextPicture, nextFocus = os.clock(), t, 0, 0
    while os.clock() - c0 < tonumber(arg[5]) do
        local dt = os.clock() - c0 + t0 - t
        t = t + dt
        for _, o in ipairs({ viper, aim120 }) do
            for i = 1, 3 do o.p[i] = o.p[i] + o.v[i] * dt end
        end
        local count, _, ids, hz = nebo.camera()
        if count > 0 and t >= nextPicture then
            nextPicture = t + 1
            nebo.publish_camera("camera_picture", NEBO_cameraPicture())
        end
        if count > 0 and ids ~= "" and hz > 0 and t >= nextFocus then
            nextFocus = t + 1 / hz
            nebo.publish_camera("camera_focus", NEBO_cameraFocus(ids))
        end
        for _, line in ipairs(nebo.poll()) do print("nebo: " .. line) end
    end
    nebo.stop()
    print("stopped")
    return
end

print(NEBO_rescan())
print(NEBO_cameraPicture())
print(NEBO_cameraFocus("16777473,1000000001,16777999,42"))
aim120.dead = true
print(NEBO_cameraFocus("16777473,1000000001"))
