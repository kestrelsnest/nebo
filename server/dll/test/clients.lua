-- nebo.clients() outside DCS: nebo.dll on port 10399 with the server password NEBO_TEST_PASSWORD,
-- printing "clients: blue red blueLogins redLogins" whenever they change, for 20 s. clients_test.py
-- logs controllers in and out meanwhile and checks the lines.
-- usage: "E:\DCS World\bin\luae.exe" clients.lua <dir with nebo.dll>
package.cpath = arg[1] .. "\\?.dll;" .. package.cpath
local nebo = require("nebo")
io.stdout:setvbuf("no")
nebo.set_auth(os.getenv("NEBO_TEST_PASSWORD") or "", "", "", "", "", "", "", "", "")
print("start:", nebo.start(10399, "", ""))
local t0, last = os.clock(), ""
while os.clock() - t0 < 20 do
    local b, r, bl, rl = nebo.clients()
    local now = string.format("clients: %d %d %d %d", b, r, bl, rl)
    if now ~= last then
        print(now)
        last = now
    end
    for _ in ipairs(nebo.poll()) do end
end
nebo.stop()
local b, r = nebo.clients()
print(string.format("after stop: %d %d", b, r))
