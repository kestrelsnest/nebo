-- NEBO — pilot designations (F-16C, F/A-18C). Runs in DCS's EXPORT environment on a PILOT's PC:
-- add this line to Saved Games\DCS\Scripts\Export.lua (keep the lines already there):
--   pcall(function() dofile(lfs.writedir()..[[Scripts\NEBO-Export.lua]]) end)
--
-- About once a second it reads the cockpit displays DCS offers export scripts (list_indication,
-- the same text DCS-BIOS reads) and, while the targeting pod page shows a point, writes it to
-- Saved Games\DCS\NEBO\designation.txt: the pod's coordinates, elevation, track and range,
-- and with an emitter designated on the HAD page, the HTS error ellipse and its grade; in the
-- F/A-18C, the ATFLIR page's coordinates while the pod has a target. The
-- NEBO DTC hook sends it to your server's controllers (they choose to see it). Nothing is
-- sent from here. IC-safe: a Saved Games script; no game file is changed. Works with the server's
-- own-ship export switched off.
-- Chains LuaExportAfterNextFrame: the export scripts before it still run.

local INTERVAL_S = 1
local DEVICES = 30      -- list_indication(0 … DEVICES-1): the F-16's MFDs are 4 and 5, the Hornet's DDIs 2 and 3
local REFRESH_S = 3     -- an unchanged designation is rewritten this often (the hook sends fresh ones only)

local lfs = require("lfs")
local dir = lfs.writedir() .. "NEBO\\"
local path, tmp = dir .. "designation.txt", dir .. "designation.tmp"

NeboExport = {}  -- global: the offline test reaches the parser through it
local M = NeboExport

-- A list_indication text as element name -> value lines (elements without text are left out).
function M.fields(text)
    local out = {}
    for chunk in (text .. "\n-"):gmatch("(.-)\n%-%-%-%-%-%-%-%-%-%-+") do
        local name, values = nil, {}
        for line in (chunk .. "\n"):gmatch("([^\n]*)\n") do
            local l = line:match("^%s*(.-)%s*$")
            if not name then
                if l ~= "" then name = l end
            elseif l ~= "" and l ~= "children are {" and l ~= "}" then
                values[#values + 1] = l
            end
        end
        if name and #values > 0 and not out[name] then out[name] = values end
    end
    return out
end

-- "42 34.540" -> 42.5757 (degrees and decimal minutes; the page shows no hemisphere letter)
local function ddm(s)
    local d, m = tostring(s or ""):match("^[NSEW]?%s*(%d+)%s+(%d+%.?%d*)$")
    if not d then return nil end
    return tonumber(d) + tonumber(m) / 60
end

-- "3.5NM" / "4030FT" -> metres
local function distance(s)
    local n, unit = tostring(s or ""):match("^%s*([%d%.]+)%s*(%a+)")
    n = tonumber(n)
    if not n then return nil end
    if unit == "NM" then return n * 1852 end
    if unit == "FT" then return n * 0.3048 end
    return nil
end

-- F/A-18C: the ATFLIR page's coordinates datablock ("N 42:30.63", "E 41:39.04", "ELEV 48"), sent
-- only while the pod has a target (designated or tracked: the page then shows "22.1 TGT"), so a
-- pod slewing over the sky or the ground is not. Hemisphere letters are dropped like the F-16's
-- page has none: the server checks unsigned degrees and the client signs them from the pilot.
local function flir(text)
    local f = M.fields(text)
    local range = tonumber(tostring((f.MPD_FLIR_TGT_Range_A or {})[1] or ""):match("^%s*([%d%.]+)%s*TGT"))
    if not range then return nil end
    local function dm(hemi)
        local d, m = text:match("\n%s*[" .. hemi .. "]%s*(%d+)[:%s]+(%d+%.?%d*)%s*\n")
        return d and tonumber(d) + tonumber(m) / 60
    end
    local lat, lon = dm("NS"), dm("EW")
    if not lat or not lon then return nil end
    local mode = tostring((f.MPD_FLIR_TrackMode_A or {})[1] or "")
    if mode:find("Symbol_", 1, true) then mode = "" end
    return {
        lat = lat, lon = lon,
        alt_ft = tonumber(text:match("\n%s*ELEV%s+(%-?%d+)%s*\n")),
        track = ("FLIR " .. mode):gsub("[^%w ]", ""):gsub("%s+$", ""):sub(1, 16),
        range_nm = range,
    }
end

-- The designation shown on the displays (a table of the texts by display number), or nil.
function M.parse(displays)
    local tgp, had, pod
    for _, text in pairs(displays) do
        if not tgp and text:find("TGP_coordinates_lat_lon", 1, true) then tgp = M.fields(text) end
        if not had and text:find("\nMajorAxis\n", 1, true) then had = M.fields(text) end
        if not pod and text:find("FLIR_coords_root_A", 1, true) then pod = text end
    end
    if not tgp then return pod and flir(pod) or nil end
    local ll = tgp.TGP_coordinates_lat_lon
    local lat, lon = ll and ddm(ll[1]), ll and ddm(ll[2])
    if not lat or not lon then return nil end
    local d = {
        lat = lat, lon = lon,
        alt_ft = tonumber((tgp.TGP_coordinates_altitude or {})[1]),
        track = table.concat(tgp.TGP_TrackStatus or {}, " "):gsub("[^%w ]", ""):sub(1, 16),
        range_nm = tonumber(tostring((tgp.TGP_Range or {})[1] or ""):match("([%d%.]+)")),
    }
    if had then
        d.had_major_m = distance((had.MajorAxis or {})[1])
        d.had_minor_m = distance((had.MinorAxis or {})[1])
        d.had_pgm = tonumber(tostring((had.PGMs or {})[1] or ""):match("(%d)"))
        d.had_brg = tonumber(tostring((had.BearingTarget or {})[1] or ""):match("^(%d+)"))
        if not d.had_major_m or not d.had_minor_m then
            d.had_major_m, d.had_minor_m, d.had_pgm, d.had_brg = nil, nil, nil, nil
        end
    end
    return d
end

local KEYS = { "lat", "lon", "alt_ft", "track", "range_nm", "had_major_m", "had_minor_m", "had_pgm", "had_brg" }

-- "key=value" lines; the designation's own text (without the time) tells whether it changed.
function M.serialize(d)
    local lines = {}
    for _, k in ipairs(KEYS) do
        local v = d[k]
        local fmt = (k == "lat" or k == "lon") and "%.6f" or (k == "had_pgm" or k == "had_brg") and "%.0f" or "%.1f"
        if type(v) == "number" then lines[#lines + 1] = k .. "=" .. string.format(fmt, v)
        elseif v ~= nil and v ~= "" then lines[#lines + 1] = k .. "=" .. tostring(v) end
    end
    return table.concat(lines, "\n")
end

local nextAt, lastText, lastWrite, seq = 0, nil, 0, 0

local function write(body)
    local f = io.open(tmp, "wb")
    if not f then return end
    f:write(body)
    f:close()
    os.remove(path)
    os.rename(tmp, path)
end

function M.tick()
    local now = os.clock()
    if now < nextAt then return end
    nextAt = now + INTERVAL_S
    local displays = {}
    for i = 0, DEVICES - 1 do
        local ok, s = pcall(list_indication, i)
        if ok and type(s) == "string" and s ~= "" then displays[i] = s end
    end
    local d = M.parse(displays)
    if not d then return end
    local text = M.serialize(d)
    if text == lastText and now - lastWrite < REFRESH_S then return end
    if text ~= lastText then seq = seq + 1 end
    lastText, lastWrite = text, now
    write(string.format("time=%d\nseq=%d\n%s\n", os.time(), seq, text))
end

if type(list_indication) == "function" then  -- in DCS (the offline test loads only the parser)
    lfs.mkdir(dir)
    local prevAfter = LuaExportAfterNextFrame
    function LuaExportAfterNextFrame()
        if prevAfter then pcall(prevAfter) end
        pcall(M.tick)
    end
end
