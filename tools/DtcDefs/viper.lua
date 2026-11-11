-- F-16C data cartridge: the default cartridge and the editor's definitions, made by DCS's own
-- DTC code (CoreMods\aircraft\F-16C\DTC) so nothing is typed in by hand. Run with DCS's Lua from
-- the DCS folder (the DTC files load their parts by "./CoreMods/..." paths):
--   cd "E:\DCS World" && bin\luae.exe <repo>\tools\DtcDefs\viper.lua <out dir>
-- Writes <out dir>\F-16C_50.default.json (a complete empty cartridge, as DCS's DTC manager saves a
-- new one, with the RWR / CMDS tables the avionics read compiled the same way) and
-- F-16C_50.defs.json (every value table of the _defs files, the steerpoint types and the threat
-- base: limits, enums, threat ring types, CMDS programs, RWR / ROE lists). Rerun after a DCS
-- update that changes the module.
local out = (arg[1] or "."):gsub("[\\/]$", "") .. "\\"
local ROOT = "./CoreMods/aircraft/F-16C/DTC/"

local C = dofile((arg[0]:gsub("[^\\/]+$", "")) .. "common.lua")

-- ---- More of the dialog's environment the Viper's scripts call ------------------------------
onAction = function(_, action)              -- a fresh dialog: no row selected, the .dlg's "state"s
    if action == "getSelectedRowNum" then return -1 end
    if action == "getState" then return true end  -- all its checkboxes (the mirror ones) start ticked
end
getCurrentTerrainBounds = function() return 0, 0 end
isDTCVisible = function() return false end
showWarningWindow = function() end
updateManager_Add = function() end
removeAllWaypoints = function() end
waypointsByGroup = {}                       -- the map's point groups
-- Map and widget calls of the dialog (no map here: nothing placed, sea level, standard day).
for _, name in ipairs({ "addWaypoint", "removeWaypoint", "updateWaypoint", "selectWaypointById", "setMapPosition",
        "setAddDTCWaypointState", "setEditDTCWaypointState", "setupDescriptorCallbacks", "getMouseCursorColumnRow" }) do
    _G[name] = function() end
end
getAltitude = function() return 0 end
getTime = function() return 0 end
getTemperatureAndPressureAtPoint = function() return 15, 101325 end
getAircraftPosition = function() return 0, 0 end

-- ---- The default cartridge: DCS's init() without the dialog --------------------------------
dofile("Scripts/Database/wsTypes.lua")      -- the wsType_* constants of the threat base
dofile(ROOT .. "F-16C_50_DTC.lua")          -- `data` skeleton and the fill*ByDefaultValues functions
for _, f in ipairs({ "defs.lua", "MPD/CMDS.lua", "MPD/Points.lua", "MPD/NAV_PTS.lua", "MPD/DEST.lua",
        "MPD/GEO_LINES.lua", "MPD/THREAT_PTS.lua", "MPD/ROE.lua", "MPD/NAV_Routes.lua", "MPD/VIPVRP.lua",
        "COMM/COMM1.lua", "COMM/COMM2.lua", "COMM/COMM_common.lua", "ELINT/RWR/RWR.lua", "COLR/COLR.lua" }) do
    dofile(ROOT .. f)                       -- as init() does
end

-- init(): everything after its dialog wiring.
init_NAV_Routes()
init_VIPVRP()
fillNAV_PTSByDefaultValues()
fillDESTByDefaultValues()
fillGEO_LINESByDefaultValues()
fillTHREAT_PTSByDefaultValues()
fillROEByDefaultValues()
fillVIPVRPByDefaultValues()
fill_CMDS_ByDefaultValues()
fill_COMM_ByDefaultValues()
fill_RWR_ByDefaultValues()
fill_COLR_ByDefaultValues()
-- What the avionics read, compiled as the dialog does on every change.
makeCMDSTable(threats_base, data.MPD.CMDS.CMDSPrograms)
update_RWR_Compiled_Table()

C.write(out .. "F-16C_50.default.json", { type = "F-16C_50", name = "", data = data })

-- ---- Definitions -----------------------------------------------------------------------------
local defs = { module = "F-16C_50" }
for _, f in ipairs({ "MPD/CMDS_defs.lua", "ELINT/RWR/RWR_defs.lua", "COMM/COMM1_defs.lua", "COMM/COMM2_defs.lua",
        "MPD/ROE_defs.lua", "MPD/THREAT_PTS_defs.lua", "COLR/COLR_defs.lua" }) do
    defs[f:match("([^/]+)%.lua$")] = C.defsOf(ROOT .. f)
end
defs.NAV_PTS_Types = C.dataOnly(NAV_PTS_Types)
defs.threats_base = C.dataOnly(threats_base)   -- the list CMDS / RWR / ROE tables are built from
C.write(out .. "F-16C_50.defs.json", defs)
