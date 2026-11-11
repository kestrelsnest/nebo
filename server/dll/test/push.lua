-- Pushed cartridges outside DCS: nebo.dll on port 10399, password from NEBO_TEST_PASSWORD, one
-- pilot (127.0.0.1, DCS player id 1001, key "keyA"). A dtc_push command is stored the way the
-- server hook does it (nebo.set_pushed); runs 60 s, log lines printed.
-- usage: "E:\DCS World\bin\luae.exe" push.lua <dir with nebo.dll>
package.cpath = arg[1] .. "\\?.dll;" .. package.cpath
local nebo = require("nebo")
io.stdout:setvbuf("no")
print("nebo " .. nebo.version())
nebo.set_auth(os.getenv("NEBO_TEST_PASSWORD") or "", "", "", "", "", "", "", "", "")
print("start:", nebo.start(10399, "", ""))
nebo.set_pilots({ "127.0.0.1=blue=1001=keyA", "bad entry", "127.0.0.1=blue" })
local version = 100
local t0 = os.clock()
while os.clock() - t0 < 60 do
    for _, cmd in ipairs(nebo.commands()) do
        if cmd.name == "dtc_push" then
            version = version + 1
            for _, u in ipairs(cmd.units) do
                -- As the server hook: one cartridge per aircraft type (its top-level "type", written last).
                local ctype = cmd.cartridge:match('"type"%s*:%s*"([^"]*)"%s*}%s*$') or ""
                if u == "1001" then nebo.set_pushed("keyA", version, "Tester", cmd.cartridge, ctype) end
            end
            nebo.command_result(cmd.client, cmd.id, true, "stored " .. #cmd.cartridge .. " bytes for " .. table.concat(cmd.units, ","))
        else
            nebo.command_result(cmd.client, cmd.id, false, "not in this test")
        end
    end
    -- Pilots' designations come back on a side channel, as the server hook passes them on.
    for _, d in ipairs(nebo.designations()) do
        nebo.publish_side(d.coalition, "designations", string.format('{"type":"designation_test","player":"%s","d":%s}', d.player, d.json))
    end
    for _, line in ipairs(nebo.poll()) do print("nebo: " .. line) end
end
nebo.stop()
