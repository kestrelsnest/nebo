-- Terrain delivery outside DCS: nebo.dll on port 10398, controller password "ctrlpw", a terrain
-- grid loaded from a cache file (arg 2: a terrain_<id>_<step>.bin and its header values in args
-- 3-8), a theatre on its channel and a blue picture every second. terrain_test.py logs in meanwhile.
-- usage: "E:\DCS World\bin\luae.exe" terrain.lua <dir with nebo.dll> <cache> <id> <x0> <z0> <step> <nx> <nz>
package.cpath = arg[1] .. "\\?.dll;" .. package.cpath
local nebo = require("nebo")
io.stdout:setvbuf("no")
print("nebo " .. nebo.version())
nebo.set_auth("ctrlpw", "", "", "", "", "", "", "", "")
print("start:", nebo.start(10398, "", ""))
print("terrain from cache:", nebo.terrain_begin(arg[3], tonumber(arg[4]), tonumber(arg[5]), tonumber(arg[6]),
    tonumber(arg[7]), tonumber(arg[8]), arg[2]))
nebo.publish_all("theatre", '{"type":"theatre","id":"' .. arg[3] .. '"}')

local t0 = os.clock()
local n, nextPicture = 0, 0
while os.clock() - t0 < 25 do
    local t = os.clock() - t0
    if t >= nextPicture then
        n = n + 1
        nebo.publish("blue", string.format('{"type":"picture","coalition":"blue","time":%d,"units":[]}', n))
        nextPicture = t + 1
    end
    for _, line in ipairs(nebo.poll()) do print("nebo: " .. line) end
end
nebo.stop()
print("stopped")
