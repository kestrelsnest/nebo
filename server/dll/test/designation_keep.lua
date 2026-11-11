-- The server hook's HAD memory outside DCS: which designations refine a kept emitter and which
-- start a new one, the plain pod point, the cap per pilot.
-- usage: "E:\DCS World\bin\luae.exe" designation_keep.lua <repo>\lua\NEBO-hook.lua
local src = assert(io.open(arg[1], "rb")):read("*a")
local block = src:match("(local DESIGNATION_EMITTERS.-)local function designationsTick")
local chunk = assert(loadstring(block .. "\nreturn designationKeep"))
local keep = chunk()
local function j(lat, lon, major) return string.format('{"lat":%.6f,"lon":%.6f%s,"track":"POINT"}', lat, lon, major and string.format(',"had_major_m":%d', major) or "") end
local e = { emitters = {}, nextEmitter = 0 }
local pass = true
local function expect(name, ok) print(string.format("%-62s %s", name, ok and "PASS" or "FAIL")) pass = pass and ok end
expect("first HAD point: emitter 1", keep(e, j(42.5000, 41.6000, 6000), 0) == 1)
expect("refined 800 m away (inside half its 6 km ellipse): still 1", keep(e, j(42.5070, 41.6000, 3000), 5) == 1)
expect("another SAM 20 km away: emitter 2", keep(e, j(42.6800, 41.6000, 5000), 10) == 2)
expect("back to the first (1.6 km off, 2.5 km ellipse): 1 again", keep(e, j(42.4950, 41.6100, 2500), 15) == 1 and e.currentEmitter == 1)
expect("a plain pod point: kept as current, no emitter", keep(e, j(42.3, 41.2, nil), 20) == nil and e.currentEmitter == nil and e.emitters[1] and e.emitters[2])
for k = 1, 25 do keep(e, j(43 + k * 0.1, 42, 2000), 30 + k) end
local n = 0 for _ in pairs(e.emitters) do n = n + 1 end
expect("3 km away with small ellipses: a second SAM", (function() local r = keep(e, j(42.5070 + 0.027, 41.6000, 1000), 25) return r == e.nextEmitter and r > 2 end)())
expect("never more than 20 kept per pilot", n == 20)
print(pass and "ALL PASS" or "SOME FAILED")
