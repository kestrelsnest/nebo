-- The hook's config rules, outside DCS: a BOM is stripped, a broken file is reported and kept.
-- usage: "E:\DCS World\bin\luae.exe" config_load.lua
local function load(text)
    text = text:gsub("^\239\187\191", "")
    local chunk, err = loadstring(text, "=NEBO.lua")
    if not chunk then return nil, err end
    local env = {}
    setfenv(chunk, env)
    chunk()
    return env
end
local ok = true
local env = load('\239\187\191password = "x"\ndiscord_roles = { "1231243609249022052" }\n')
print("BOM file loads:", env and env.password == "x" and env.discord_roles[1] == "1231243609249022052")
ok = ok and env ~= nil
local broken, err = load('password = "x\n')
print("broken file reported, not loaded:", broken == nil, err)
ok = ok and broken == nil
print(ok and "PASS" or "FAIL")
