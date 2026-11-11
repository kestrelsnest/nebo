-- F/A-18C data cartridge: the default cartridge and the editor's definitions, made by DCS's own
-- DTC code (CoreMods\aircraft\FA-18C\DTC) so nothing is typed in by hand. Run with DCS's Lua from
-- the DCS folder (the DTC files load their parts by "./CoreMods/..." paths):
--   cd "E:\DCS World" && bin\luae.exe <repo>\tools\DtcDefs\hornet.lua <out dir>
-- Writes <out dir>\FA-18C_hornet.default.json (a complete empty cartridge, as DCS's DTC manager
-- saves a new one: every section's defaults and the tables the avionics read, compiled the same
-- way) and FA-18C_hornet.defs.json (every value table of the _defs files and the threat base:
-- limits, enums, threat ring types, CMDS programs, RWR / HARM threat lists). Rerun after a DCS
-- update that changes the module.
local out = (arg[1] or "."):gsub("[\\/]$", "") .. "\\"
local ROOT = "./CoreMods/aircraft/FA-18C/DTC/"

local C = dofile((arg[0]:gsub("[^\\/]+$", "")) .. "common.lua")

-- ---- The default cartridge: DCS's init() without the dialog --------------------------------
-- The wsType_* constants the threat base's wsTypes_*.lua build on (DCS has them loaded already).
dofile("Scripts/Database/wsTypes.lua")
dofile(ROOT .. "FA-18C_hornet_DTC.lua")     -- `data` skeleton and the initialize_* functions
dofile(ROOT .. "defs.lua")
dofile(ROOT .. "ALR67/CMDS.lua")            -- makeCMDSTable (loads the threat base)
dofile(ROOT .. "ALR67/RWR.lua")             -- update_RWR_Compiled_Table
dofile(ROOT .. "HARM/TOO_CLASS.lua")        -- populate defaults, compile_HARM_TOO_CLASS_Avionics

data.WYPT.NAV_PTS = {}
data.WYPT.NAV_ROUTE = { {}, {}, {} }
initialize_NAV_Settings(false)
initialize_CMDS(false)
initialize_RWR(false)
initialize_COMM1(false)
initialize_COMM2(false)
data.TCN = {}                               -- initialize_TACAN loads a TACAN_defs.lua DCS does not ship
initialize_SA_CAP_PTS(false)
initialize_SA_CORRIDORS(false)
initialize_SA_FAOR_FLOT(false)
initialize_SA_MEZ_THRTS(false)
initialize_SA_SETTINGS(false)
initialize_HARM_SETTINGS(false)
initialize_HARM_TOO_CLASS(false)
initialize_GPS_WYPT_1(false)
initialize_GPS_WYPT_2(false)
-- What the avionics read, compiled as the dialog does on every change and on save (returnData).
makeCMDSTable(threats_base, data.ALR67.CMDS.CMDS_Threat_table)
update_RWR_Compiled_Table()
compile_HARM_TOO_CLASS_Avionics()

C.write(out .. "FA-18C_hornet.default.json", { type = "FA-18C_hornet", name = "", data = data })

-- ---- Definitions: every value table of the _defs files, plus the threat base ---------------
local defs = { module = "FA-18C_hornet" }
for _, f in ipairs({ "ALR67/CMDS_defs.lua", "ALR67/RWR_defs.lua", "COMM/COMM1_defs.lua", "COMM/COMM2_defs.lua",
        "WYPT/NAV_SETTINGS_defs.lua", "SA/SETTINGS_defs.lua", "SA/MEZ_THRTS_defs.lua", "HARM/HARM_SETTINGS_defs.lua" }) do
    defs[f:match("([^/]+)%.lua$")] = C.defsOf(ROOT .. f)
end
defs.threats_base = C.dataOnly(threats_base)   -- the list CMDS / RWR / HARM tables are built from
C.write(out .. "FA-18C_hornet.defs.json", defs)
