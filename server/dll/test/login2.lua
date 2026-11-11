-- Login 2 outside DCS: nebo.dll on port 10399 with a server password and Discord settings from
-- the environment (NEBO_TEST_PASSWORD, NEBO_DISCORD_ID, NEBO_DISCORD_SECRET, NEBO_DISCORD_GUILD,
-- NEBO_DISCORD_ROLES), publishing fake pictures for 90 s; log lines printed.
-- usage: "E:\DCS World\bin\luae.exe" login2.lua <dir with nebo.dll>
package.cpath = arg[1] .. "\\?.dll;" .. package.cpath
local nebo = require("nebo")
io.stdout:setvbuf("no")
local env = os.getenv
print("nebo " .. nebo.version())
nebo.set_auth(env("NEBO_TEST_PASSWORD") or "", "", "", env("NEBO_DISCORD_ID") or "", env("NEBO_DISCORD_SECRET") or "",
    env("NEBO_DISCORD_GUILD") or "", env("NEBO_DISCORD_ROLES") or "", "", "")
print("start:", nebo.start(10399, "", ""))
local t0, n, nextPublish = os.clock(), 0, 0
while os.clock() - t0 < 90 do
    local t = os.clock() - t0
    if t >= nextPublish then
        n = n + 1
        for _, side in ipairs({ "blue", "red" }) do
            nebo.publish(side, string.format('{"type":"picture","coalition":"%s","time":%d,"units":[]}', side, n))
        end
        nextPublish = t + 1
    end
    for _, line in ipairs(nebo.poll()) do print("nebo: " .. line) end
end
nebo.stop()
