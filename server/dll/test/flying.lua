-- "Controllers don't fly" outside DCS: 127.0.0.1 is in a cockpit for 0-4 s, free for 4-8 s, in a
-- cockpit again from 8 s. A controller from 127.0.0.1 is refused at first, logs in at 5 s and is
-- closed at 8 s (watch the nebo: lines).
-- usage: "E:\DCS World\bin\luae.exe" flying.lua <dir with nebo.dll>
package.cpath = arg[1] .. "\\?.dll;" .. package.cpath
local nebo = require("nebo")
print("nebo " .. nebo.version())
print("start:", nebo.start(10399, "bluepw", "redpw"))

local t0 = os.clock()
local phase, n, nextPublish = nil, 0, 0
while os.clock() - t0 < 12 do
    local t = os.clock() - t0
    local want = (t < 4 or t >= 8) and "flying" or "free"
    if want ~= phase then
        phase = want
        nebo.set_flying(want == "flying" and { "127.0.0.1" } or {})
        print(string.format("%.1f s: 127.0.0.1 %s", t, want))
    end
    if t >= nextPublish then
        n = n + 1
        nebo.publish("blue", string.format('{"type":"picture","coalition":"blue","time":%d,"units":[]}', n))
        nextPublish = t + 1
    end
    for _, line in ipairs(nebo.poll()) do print("nebo: " .. line) end
end
nebo.stop()
print("stopped")
