-- F-14B(U) data cartridge: the default cartridge and the editor's definitions, made by DCS's own
-- DTC code (CoreMods\aircraft\F14\DTC\F-14BU_DTC.lua) so nothing is typed in by hand. Run with
-- DCS's Lua from the DCS folder:
--   cd "E:\DCS World" && bin\luae.exe <repo>\tools\DtcDefs\tomcat.lua <out dir>
-- Writes <out dir>\F-14BU.default.json (a new cartridge as DCS's init() makes it) and
-- F-14BU.defs.json (the CMDS tables, the threat list, and the dialog's limits: flight plans,
-- points, lines, JDAM stations and the GBU-31 launch zone table, TIS callsigns). The limits are
-- top-level "local" constants of DCS's file: they are made global before it runs, so they are
-- read from it as they are. Rerun after a DCS update that changes the module.
local out = (arg[1] or "."):gsub("[\\/]$", "") .. "\\"
local FILE = "./CoreMods/aircraft/F14/DTC/F-14BU_DTC.lua"

local C = dofile((arg[0]:gsub("[^\\/]+$", "")) .. "common.lua")

-- ---- The dialog's environment (no map: sea level; positions as given) -----------------------
onAction = function(_, action) if action == "getSelectedRowNum" then return -1 end end
for _, name in ipairs({ "addWaypoint", "removeWaypoint", "updateWaypoint", "selectWaypointById", "setMapPosition",
        "setAddDTCWaypointState", "setEditDTCWaypointState", "setupDescriptorCallbacks", "setPanMapState" }) do
    _G[name] = function() end
end
getAltitude = function() return 0 end
convertMetersToLatLon = function(x, y) return 0, 0 end
convertLatLonToMeters = function() return 0, 0 end
getCoords_LatLonStandard = function() return { lat = { d = "N", g = 0, m = 0, s = 0 }, lon = { d = "E", g = 0, m = 0, s = 0 } } end

-- ---- DCS's file with its top-level constants global ---------------------------------------------
local f = assert(io.open(FILE, "rb"))
local src = f:read("*a")
f:close()
local names = {}
src = ("\n" .. src):gsub("\nlocal (%u[%u%d_]+)(%s*=)", function(name, eq)
    names[#names + 1] = name
    return "\n" .. name .. eq
end)
assert(loadstring(src, "@" .. FILE))()

-- ---- The default cartridge: DCS's init() ----------------------------------------------------------
init()
C.write(out .. "F-14BU.default.json", { type = data.type, name = "", data = data })

-- ---- Definitions ----------------------------------------------------------------------------------
local defs = { module = "F-14BU", constants = {} }
for _, name in ipairs(names) do
    local v = C.dataOnly(_G[name])
    if v ~= nil then defs.constants[name] = v end
end
for _, name in ipairs({ "CMDS", "CMDS_programs_name", "CMDS_AUTO_PROGRAM_NAMES", "CMDS_THREAT_CATEGORIES",
        "CMDS_THRESHOLD_NAMES", "CMDS_KNOWN_THREATS", "TIS", "TIS_CALLSIGN_LEN" }) do
    defs[name] = C.dataOnly(_G[name])
end
C.write(out .. "F-14BU.defs.json", defs)
