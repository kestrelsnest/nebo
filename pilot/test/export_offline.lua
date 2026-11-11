-- The pilot export script's parser on real F-16 display texts (fixtures recorded in DCS 2026-10-30).
-- usage: luae.exe export_offline.lua <repo>\pilot\NEBO-Export.lua <repo>\pilot\test\fixtures\
local exportFile, fixtures = arg[1], arg[2]:gsub("[\\/]$", "") .. "\\"
package.loaded["lfs"] = { writedir = function() return "" end, mkdir = function() end }
assert(loadfile(exportFile))()
local M = NeboExport

local function read(name) local f = assert(io.open(fixtures .. name, "rb")) local s = f:read("*a") f:close() return s end
local tgp, had, hadPlain = read("f16_tgp.txt"), read("f16_had_designated.txt"), read("f16_had_plain.txt")

local pass = true
local function expect(name, ok, info) print(string.format("%-60s %s %s", name, ok and "PASS" or "FAIL", info or "")) pass = pass and ok end
local function near(a, b, eps) return type(a) == "number" and math.abs(a - b) < (eps or 1e-6) end

-- TGP page alone: the pod's point
local d = M.parse({ [5] = tgp })
expect("TGP: latitude 42 34.042", d and near(d.lat, 42 + 34.042 / 60), d and tostring(d.lat))
expect("TGP: longitude 41 37.730", d and near(d.lon, 41 + 37.730 / 60), d and tostring(d.lon))
expect("TGP: elevation 51 ft", d and d.alt_ft == 51)
expect("TGP: track AREA, range 20 NM", d and d.track == "AREA" and d.range_nm == 20, d and (d.track .. " " .. tostring(d.range_nm)))
expect("TGP alone: no HTS ellipse", d and d.had_major_m == nil)

-- TGP + HAD with an emitter designated: the ellipse and its grade
d = M.parse({ [4] = had, [5] = tgp })
expect("HAD: major axis 3.5 NM in metres", d and near(d.had_major_m, 3.5 * 1852, 0.01), d and tostring(d.had_major_m))
expect("HAD: minor axis 4030 ft in metres", d and near(d.had_minor_m, 4030 * 0.3048, 0.01), d and tostring(d.had_minor_m))
expect("HAD: PGM 2, bearing 103", d and d.had_pgm == 2 and d.had_brg == 103)

-- HAD page without a designation adds nothing; no TGP page, no designation
d = M.parse({ [4] = hadPlain, [5] = tgp })
expect("HAD without a designated emitter: no ellipse", d and d.had_major_m == nil)
expect("HAD page alone: nothing to send", M.parse({ [4] = had }) == nil)
expect("no displays: nothing", M.parse({}) == nil)

-- The file format
local text = M.serialize(M.parse({ [4] = had, [5] = tgp }))
expect("serialised lines", text:find("^lat=42.567367\nlon=41.628833\nalt_ft=51.0\ntrack=AREA\nrange_nm=20.0\nhad_major_m=6482.0\nhad_minor_m=1228.3\nhad_pgm=2\nhad_brg=103$") ~= nil, "\n" .. text)

-- F/A-18C ATFLIR page (right DDI, recorded 2026-11-01): sent only while the pod has a target
local scene, inr, sky = read("f18_flir_scene.txt"), read("f18_flir_inr.txt"), read("f18_flir_sky.txt")
d = M.parse({ [3] = scene })
expect("ATFLIR: N 42:30.63 E 41:39.04", d and near(d.lat, 42 + 30.63 / 60) and near(d.lon, 41 + 39.04 / 60), d and (tostring(d.lat) .. " " .. tostring(d.lon)))
expect("ATFLIR: ELEV 48 ft, 22.1 NM", d and d.alt_ft == 48 and d.range_nm == 22.1, d and (tostring(d.alt_ft) .. " " .. tostring(d.range_nm)))
expect("ATFLIR: track FLIR SCENE, no HTS", d and d.track == "FLIR SCENE" and d.had_major_m == nil, d and d.track)
expect("ATFLIR slewing (no TGT range): nothing", M.parse({ [3] = inr }) == nil)
expect("ATFLIR at the sky: nothing", M.parse({ [3] = sky }) == nil)
expect("ATFLIR next to an F-16 TGP page: the TGP wins", (M.parse({ [3] = scene, [5] = tgp }) or {}).track == "AREA")

print(pass and "ALL PASS" or "SOME FAILED")
