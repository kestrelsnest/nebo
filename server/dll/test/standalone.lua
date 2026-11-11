-- Runs nebo.dll outside DCS with DCS's own interpreter, publishing fake pictures.
-- usage: "E:\DCS World\bin\luae.exe" standalone.lua <dir with nebo.dll> [seconds]
package.cpath = arg[1] .. "\\?.dll;" .. package.cpath
local nebo = require("nebo")
local seconds = tonumber(arg[2]) or 15
print("nebo " .. nebo.version())
print("start:", nebo.start(10399, "bluepw", "redpw"))

local nextPublish, deadline, n = 0, os.clock() + seconds, 0
while os.clock() < deadline do
    if os.clock() >= nextPublish then
        n = n + 1
        nebo.publish("blue", string.format('{"type":"picture","coalition":"blue","time":%d,"units":[{"name":"B%d"}]}', n, n))
        nebo.publish("red", string.format('{"type":"picture","coalition":"red","time":%d,"units":[{"name":"R%d"}]}', n, n))
        nextPublish = os.clock() + 1
    end
    for _, line in ipairs(nebo.poll()) do print("nebo: " .. line) end
end
nebo.stop()
print("stopped")
