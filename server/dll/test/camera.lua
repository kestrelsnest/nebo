-- Camera login outside DCS: nebo.dll on port 10399, controller password "ctrlpw". No camera
-- password for the first 3 s, "campw" after; 127.0.0.1 sits in a cockpit from 14 s (cameras
-- closed, controllers kept: set_flying stays empty, like allow_flying_controllers = true).
-- Publishes a blue picture and a camera_picture every second, and camera_focus at the asked rate
-- echoing the asked ids. camera_test.py logs in meanwhile and checks what each login receives.
-- usage: "E:\DCS World\bin\luae.exe" camera.lua <dir with nebo.dll>
package.cpath = arg[1] .. "\\?.dll;" .. package.cpath
local nebo = require("nebo")
io.stdout:setvbuf("no")
print("nebo " .. nebo.version())
nebo.set_auth("ctrlpw", "", "", "", "", "", "", "", "")
nebo.set_camera("", 20)
print("start:", nebo.start(10399, "", ""))

local t0 = os.clock()
local n, nextPicture, nextFocus, cameraOn, cockpit, last = 0, 0, 0, false, false, ""
while os.clock() - t0 < 20 do
    local t = os.clock() - t0
    if not cameraOn and t >= 3 then
        cameraOn = true
        nebo.set_camera("campw", 20)
        print(string.format("%.1f s: camera password on", t))
    end
    if not cockpit and t >= 14 then
        cockpit = true
        nebo.set_cockpits({ "127.0.0.1" })
        print(string.format("%.1f s: 127.0.0.1 in a cockpit", t))
    end
    if t >= nextPicture then
        n = n + 1
        nebo.publish("blue", string.format('{"type":"picture","coalition":"blue","time":%d,"units":[]}', n))
        nebo.publish_camera("camera_picture", string.format('{"type":"camera_picture","version":1,"time":%d,"units":[],"weapons":[],"events":[]}', n))
        nextPicture = t + 1
    end
    local count, logins, ids, hz = nebo.camera()
    local state = string.format("camera: %d %d [%s] %d", count, logins, ids, hz)
    if state ~= last then
        print(string.format("%.1f s: %s", t, state))
        last = state
    end
    if ids ~= "" and hz > 0 and t >= nextFocus then
        nextFocus = t + 1 / hz
        nebo.publish_camera("camera_focus", string.format('{"type":"camera_focus","time":%.3f,"ids":"%s","objects":[]}', t, ids))
    end
    for _, line in ipairs(nebo.poll()) do print("nebo: " .. line) end
end
nebo.stop()
print("stopped")
