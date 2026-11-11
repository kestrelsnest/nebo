-- Shared by the per-aircraft DTC scripts: what the DTC dialog's environment gives its scripts,
-- and JSON written as DCS writes cartridges. Loaded with dofile(<script dir> .. "common.lua").
local quietPrint = print
print = function() end                      -- the scripts print progress lines
onAction = function() end                   -- dialog calls: never made by the default functions
getCurrentTerrainID = function() return "" end
function recursiveCopyTable(dest, source)   -- as MissionEditor/modules/me_utilities.lua
    local function copy(d, s, seen)
        for k, v in pairs(s) do
            if type(v) == "table" then
                if seen[v] == nil then
                    d[k] = d[k] or {}
                    seen[v] = d[k]
                    copy(d[k], v, seen)
                else
                    d[k] = seen[v]
                end
            else
                d[k] = v
            end
        end
    end
    copy(dest, source, {})
end

-- ---- JSON as DCS writes cartridges (net.lua2json): 1..n tables are arrays, empty ones [] ----
local function isArray(t)
    local n = 0
    for k in pairs(t) do
        if type(k) ~= "number" or k < 1 or k % 1 ~= 0 then return false end
        n = n + 1
    end
    for i = 1, n do if t[i] == nil then return false end end
    return true
end

local function encode(v, out)
    local t = type(v)
    if t == "table" then
        if isArray(v) then
            out[#out + 1] = "["
            for i = 1, #v do
                if i > 1 then out[#out + 1] = "," end
                encode(v[i], out)
            end
            out[#out + 1] = "]"
        else
            local keys = {}
            for k in pairs(v) do keys[#keys + 1] = k end
            table.sort(keys, function(a, b) return tostring(a) < tostring(b) end)
            out[#out + 1] = "{"
            for i, k in ipairs(keys) do
                if i > 1 then out[#out + 1] = "," end
                out[#out + 1] = string.format("%q:", tostring(k)):gsub("\\\n", "\\n")
                encode(v[k], out)
            end
            out[#out + 1] = "}"
        end
    elseif t == "string" then
        out[#out + 1] = (string.format("%q", v):gsub("\\\n", "\\n"))
    elseif t == "number" then
        out[#out + 1] = (v % 1 == 0 and v > -1e15 and v < 1e15) and string.format("%d", v) or string.format("%.17g", v)
    elseif t == "boolean" then
        out[#out + 1] = tostring(v)
    else
        out[#out + 1] = "null"  -- functions are left out by the callers
    end
end

local function write(path, value)
    local parts = {}
    encode(value, parts)
    local f = assert(io.open(path, "wb"))
    f:write(table.concat(parts))
    f:close()
    quietPrint("wrote " .. path)
end

-- Plain data only (no functions), for the definitions file.
local function dataOnly(v, seen)
    seen = seen or {}
    if type(v) == "function" then return nil end
    if type(v) ~= "table" then return v end
    if seen[v] then return nil end
    seen[v] = true
    local r = {}
    for k, x in pairs(v) do
        local c = dataOnly(x, seen)
        if c ~= nil then r[k] = c end
    end
    seen[v] = nil
    return r
end

-- Every data table a _defs-style file defines, run in its own environment.
local function defsOf(path)
    local env = setmetatable({}, { __index = _G })
    local chunk = assert(loadfile(path))
    setfenv(chunk, env)
    chunk()
    local t = {}
    for k, v in pairs(env) do
        local c = dataOnly(v)
        if c ~= nil then t[k] = c end
    end
    return t
end

return { write = write, dataOnly = dataOnly, defsOf = defsOf, print = quietPrint }
