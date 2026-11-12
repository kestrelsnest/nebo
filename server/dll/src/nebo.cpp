// Lua 5.1 module "nebo", loaded by Scripts\Hooks\NEBO-hook.lua via require('nebo').
#include <cstring>
#include <string>
#include <vector>

#include <windows.h>
#include <bcrypt.h>

#include "lua51.h"
#include "net_server.h"
#include "terrain.h"

namespace {

constexpr char kVersion[] = "0.17.2";  //0.3: controller commands; 0.4: coalition channels (LSO feed); 0.5: analytics ids; 0.6: F10 mark commands; 0.7: carrier lights; 0.8: identify, shared threats, nctr; 0.9: controllers do not fly; 0.10: login 2 (Discord, password proof, side choice); 0.11: no rwr channel; 0.12: pilot sync (dtc_request); 0.13: pushed cartridges (dtc_push); 0.14: one pushed cartridge per player and aircraft type; 0.14.1: AEGIS 4 s scan; 0.14.2: client only; 0.14.3: pilot hook, several servers; 0.15: pilot designations (designation on dtc_request, designations channel); 0.15.1: nebo.clients() (idle picture rate); 0.15.2: hook only (launch marks); 0.16: camera login (camera_password, camera channels, camera_set_focus); 0.17: controllers, flights, claim/handoff, callsign, owner colour, area_set (f10: areas on the pilots' F10 map), message_side, chat channel; 0.17.1: hook only (Foothold found late); 0.17.2: client only (client log)
nebo::NetServer g_server;
nebo::TerrainGrid g_terrain;

// nebo.start(port, blue_password, red_password) -> true | false, error
int l_start(lua_State* L) {
    lua_Integer port = luaL_checkinteger(L, 1);
    size_t blueLen = 0, redLen = 0;
    const char* blue = luaL_optlstring(L, 2, "", &blueLen);
    const char* red = luaL_optlstring(L, 3, "", &redLen);
    if (port <= 0 || port > 65535) {
        lua_pushboolean(L, 0);
        lua_pushstring(L, "port out of range");
        return 2;
    }
    std::string error;
    if (!g_server.start(static_cast<uint16_t>(port), std::string(blue, blueLen), std::string(red, redLen), error)) {
        lua_pushboolean(L, 0);
        lua_pushstring(L, error.c_str());
        return 2;
    }
    lua_pushboolean(L, 1);
    return 1;
}

// nebo.set_auth(password, blue_password, red_password, discord_client_id, discord_secret, guild,
//              roles_csv, blue_roles_csv, red_roles_csv): login 2, before nebo.start. Every non-empty
// password is accepted as the server password; Discord is on when id, secret and guild are set.
int l_set_auth(lua_State* L) {
    auto arg = [L](int i) {
        size_t n = 0;
        const char* s = luaL_optlstring(L, i, "", &n);
        return std::string(s, n);
    };
    nebo::DiscordConfig d;
    d.clientId = arg(4);
    d.secret = arg(5);
    d.guild = arg(6);
    d.roles = nebo::splitList(arg(7));
    d.blueRoles = nebo::splitList(arg(8));
    d.redRoles = nebo::splitList(arg(9));
    g_server.setAuth({arg(1), arg(2), arg(3)}, std::move(d));
    return 0;
}

// nebo.publish("blue" | "red", json) -> true | false
int l_publish(lua_State* L) {
    size_t sideLen = 0, jsonLen = 0;
    const char* side = luaL_checklstring(L, 1, &sideLen);
    const char* json = luaL_checklstring(L, 2, &jsonLen);
    nebo::Coalition c;
    if (std::strcmp(side, "blue") == 0) {
        c = nebo::Blue;
    } else if (std::strcmp(side, "red") == 0) {
        c = nebo::Red;
    } else {
        lua_pushboolean(L, 0);
        return 1;
    }
    g_server.publish(c, std::string(json, jsonLen));
    lua_pushboolean(L, 1);
    return 1;
}

// nebo.publish_side(side, channel, json): a coalition's own channel (the fast LSO feed)
int l_publish_side(lua_State* L) {
    size_t sideLen = 0, nameLen = 0, jsonLen = 0;
    const char* side = luaL_checklstring(L, 1, &sideLen);
    const char* name = luaL_checklstring(L, 2, &nameLen);
    const char* json = luaL_checklstring(L, 3, &jsonLen);
    nebo::Coalition c;
    if (std::strcmp(side, "blue") == 0) c = nebo::Blue;
    else if (std::strcmp(side, "red") == 0) c = nebo::Red;
    else return 0;
    g_server.publishSide(c, std::string(name, nameLen), std::string(json, jsonLen));
    return 0;
}

// nebo.publish_all(channel, json): message for every coalition (theatre, ...)
int l_publish_all(lua_State* L) {
    size_t nameLen = 0, jsonLen = 0;
    const char* name = luaL_checklstring(L, 1, &nameLen);
    const char* json = luaL_checklstring(L, 2, &jsonLen);
    g_server.publishAll(std::string(name, nameLen), std::string(json, jsonLen));
    return 0;
}

// nebo.terrain_begin(id, x0, z0, step, nx, nz, cache_path) -> true when loaded from cache
int l_terrain_begin(lua_State* L) {
    size_t idLen = 0, pathLen = 0;
    const char* id = luaL_checklstring(L, 1, &idLen);
    double x0 = static_cast<double>(luaL_checkinteger(L, 2));
    double z0 = static_cast<double>(luaL_checkinteger(L, 3));
    double step = static_cast<double>(luaL_checkinteger(L, 4));
    lua_Integer nx = luaL_checkinteger(L, 5);
    lua_Integer nz = luaL_checkinteger(L, 6);
    const char* path = luaL_checklstring(L, 7, &pathLen);
    if (nx <= 0 || nz <= 0 || nx > 8192 || nz > 8192 || step <= 0) {
        lua_pushboolean(L, 0);
        return 1;
    }
    bool cached = g_terrain.begin(std::string(id, idLen), x0, z0, step, static_cast<int>(nx), static_cast<int>(nz),
                                  std::string(path, pathLen));
    if (cached) g_server.publishAll("terrain", g_terrain.toJson());
    lua_pushboolean(L, cached ? 1 : 0);
    return 1;
}

// nebo.terrain_row(i, heights_csv, surface_digits) -> true when the row was accepted
int l_terrain_row(lua_State* L) {
    lua_Integer i = luaL_checkinteger(L, 1);
    size_t csvLen = 0, surfLen = 0;
    const char* csv = luaL_checklstring(L, 2, &csvLen);
    const char* surf = luaL_checklstring(L, 3, &surfLen);
    lua_pushboolean(L, g_terrain.setRow(static_cast<int>(i), csv, surf, surfLen) ? 1 : 0);
    return 1;
}

// nebo.terrain_finish() -> true when the grid was complete and published (cache written too)
int l_terrain_finish(lua_State* L) {
    if (!g_terrain.complete()) {
        lua_pushboolean(L, 0);
        return 1;
    }
    g_terrain.saveCache();
    g_server.publishAll("terrain", g_terrain.toJson());
    lua_pushboolean(L, 1);
    return 1;
}

// nebo.poll() -> { log lines }
int l_poll(lua_State* L) {
    auto lines = g_server.drainLog();
    lua_createtable(L, static_cast<int>(lines.size()), 0);
    for (size_t i = 0; i < lines.size(); ++i) {
        lua_pushlstring(L, lines[i].data(), lines[i].size());
        lua_rawseti(L, -2, static_cast<int>(i + 1));
    }
    return 1;
}

// nebo.commands() -> { { client, coalition, address, id, name, unit, text, seconds }, ... }
// Commands already checked by the network thread (whitelist, limits, rate).
int l_commands(lua_State* L) {
    auto commands = g_server.drainCommands();
    lua_createtable(L, static_cast<int>(commands.size()), 0);
    for (size_t i = 0; i < commands.size(); ++i) {
        const auto& c = commands[i];
        lua_createtable(L, 0, 8);
        lua_pushinteger(L, static_cast<lua_Integer>(c.client));
        lua_setfield(L, -2, "client");
        lua_pushstring(L, c.coalition == nebo::Blue ? "blue" : "red");
        lua_setfield(L, -2, "coalition");
        lua_pushlstring(L, c.address.data(), c.address.size());
        lua_setfield(L, -2, "address");
        lua_pushinteger(L, static_cast<lua_Integer>(c.id));
        lua_setfield(L, -2, "id");
        lua_pushlstring(L, c.name.data(), c.name.size());
        lua_setfield(L, -2, "name");
        lua_pushinteger(L, static_cast<lua_Integer>(c.unit));
        lua_setfield(L, -2, "unit");
        lua_pushlstring(L, c.text.data(), c.text.size());
        lua_setfield(L, -2, "text");
        lua_pushinteger(L, c.seconds);
        lua_setfield(L, -2, "seconds");
        lua_pushinteger(L, static_cast<lua_Integer>(c.x));
        lua_setfield(L, -2, "x");
        lua_pushinteger(L, static_cast<lua_Integer>(c.z));
        lua_setfield(L, -2, "z");
        lua_pushlstring(L, c.threat.data(), c.threat.size());
        lua_setfield(L, -2, "threat");
        lua_pushinteger(L, static_cast<lua_Integer>(c.mark));
        lua_setfield(L, -2, "mark");
        lua_pushinteger(L, c.mode);
        lua_setfield(L, -2, "mode");
        lua_pushinteger(L, static_cast<lua_Integer>(c.x2));
        lua_setfield(L, -2, "x2");
        lua_pushinteger(L, static_cast<lua_Integer>(c.z2));
        lua_setfield(L, -2, "z2");
        lua_createtable(L, static_cast<int>(c.units.size()), 0);  // dtc_push recipients
        for (size_t u = 0; u < c.units.size(); ++u) {
            lua_pushlstring(L, c.units[u].data(), c.units[u].size());
            lua_rawseti(L, -2, static_cast<int>(u + 1));
        }
        lua_setfield(L, -2, "units");
        lua_pushlstring(L, c.blob.data(), c.blob.size());           // dtc_push cartridge
        lua_setfield(L, -2, "cartridge");
        lua_pushlstring(L, c.position.data(), c.position.size());   // controller_info
        lua_setfield(L, -2, "position");
        lua_pushlstring(L, c.color.data(), c.color.size());      // controller_info
        lua_setfield(L, -2, "color");
        lua_pushlstring(L, c.area.data(), c.area.size());        // area_set
        lua_setfield(L, -2, "area");
        lua_pushlstring(L, c.f10.data(), c.f10.size());          // area_set
        lua_setfield(L, -2, "f10");
        lua_pushlstring(L, c.audience.data(), c.audience.size());  // message_side
        lua_setfield(L, -2, "audience");
        lua_pushinteger(L, static_cast<lua_Integer>(c.khz));        // controller_info, handoff
        lua_setfield(L, -2, "khz");
        lua_pushinteger(L, static_cast<lua_Integer>(c.to));         // handoff
        lua_setfield(L, -2, "to");
        lua_pushinteger(L, static_cast<lua_Integer>(c.handoff));    // handoff_answer
        lua_setfield(L, -2, "handoff");
        lua_pushboolean(L, c.accept ? 1 : 0);                       // handoff_answer
        lua_setfield(L, -2, "accept");
        lua_rawseti(L, -2, static_cast<int>(i + 1));
    }
    return 1;
}

// nebo.controllers() -> { {client=, coalition="blue"|"red", user=, callsign=}, ... }: the
// logged-in controllers (not cameras), as of the network thread's last loop.
int l_controllers(lua_State* L) {
    auto list = g_server.controllerList();
    lua_createtable(L, static_cast<int>(list.size()), 0);
    for (size_t i = 0; i < list.size(); ++i) {
        const auto& c = list[i];
        lua_createtable(L, 0, 4);
        lua_pushinteger(L, static_cast<lua_Integer>(c.client));
        lua_setfield(L, -2, "client");
        lua_pushstring(L, c.coalition == nebo::Blue ? "blue" : "red");
        lua_setfield(L, -2, "coalition");
        lua_pushlstring(L, c.user.data(), c.user.size());
        lua_setfield(L, -2, "user");
        lua_pushlstring(L, c.callsign.data(), c.callsign.size());
        lua_setfield(L, -2, "callsign");
        lua_rawseti(L, -2, static_cast<int>(i + 1));
    }
    return 1;
}

// nebo.command_result(client, id, ok, error_or_nil)
int l_command_result(lua_State* L) {
    auto client = static_cast<uint64_t>(luaL_checkinteger(L, 1));
    auto id = static_cast<int64_t>(luaL_checkinteger(L, 2));
    bool ok = lua_toboolean(L, 3) != 0;
    size_t len = 0;
    const char* error = luaL_optlstring(L, 4, "", &len);
    g_server.commandResult(client, id, ok, std::string(error, len));
    return 0;
}

// nebo.set_flying({ "1.2.3.4", ... }): addresses of players in a cockpit (controllers don't fly)
int l_set_flying(lua_State* L) {
    luaL_checktype(L, 1, LUA_TTABLE);
    std::vector<std::string> addresses;
    for (int i = 1; addresses.size() < 1000; ++i) {
        lua_rawgeti(L, 1, i);
        if (lua_isnil(L, -1)) {
            lua_pop(L, 1);
            break;
        }
        size_t len = 0;
        const char* s = lua_tolstring(L, -1, &len);
        if (s && len > 0 && len < 64) addresses.emplace_back(s, len);
        lua_pop(L, 1);
    }
    g_server.setFlying(std::move(addresses));
    return 0;
}

// nebo.set_cockpits({ "1.2.3.4", ... }): every address in a cockpit, whatever allow_flying_controllers
// says (a camera login is always refused from a cockpit).
int l_set_cockpits(lua_State* L) {
    luaL_checktype(L, 1, LUA_TTABLE);
    std::vector<std::string> addresses;
    for (int i = 1; addresses.size() < 1000; ++i) {
        lua_rawgeti(L, 1, i);
        if (lua_isnil(L, -1)) {
            lua_pop(L, 1);
            break;
        }
        size_t len = 0;
        const char* s = lua_tolstring(L, -1, &len);
        if (s && len > 0 && len < 64) addresses.emplace_back(s, len);
        lua_pop(L, 1);
    }
    g_server.setCockpits(std::move(addresses));
    return 0;
}

// nebo.set_camera(password, max_focus_hz): "" turns camera logins off; hz is clamped to 5..20.
int l_set_camera(lua_State* L) {
    size_t len = 0;
    const char* pw = luaL_checklstring(L, 1, &len);
    g_server.setCamera(std::string(pw, len), static_cast<int>(luaL_checkinteger(L, 2)));
    return 0;
}

// nebo.publish_camera(channel, json): a message only camera logins receive (latest wins).
int l_publish_camera(lua_State* L) {
    size_t nameLen = 0, len = 0;
    const char* name = luaL_checklstring(L, 1, &nameLen);
    const char* json = luaL_checklstring(L, 2, &len);
    g_server.publishCamera(std::string(name, nameLen), std::string(json, len));
    return 0;
}

// nebo.camera() -> cameras, logins, focus ids ("12,34" or ""), focus hz (0 = none asked).
int l_camera(lua_State* L) {
    int hz = 0;
    std::string ids = g_server.cameraFocus(hz);
    lua_pushinteger(L, g_server.cameras());
    lua_pushinteger(L, static_cast<lua_Integer>(g_server.cameraLogins()));
    lua_pushlstring(L, ids.data(), ids.size());
    lua_pushinteger(L, hz);
    return 4;
}

// nebo.set_pilots({ "1.2.3.4=blue=<DCS player id>=<player key>", ... }): players on a side (pilot sync)
int l_set_pilots(lua_State* L) {
    luaL_checktype(L, 1, LUA_TTABLE);
    std::multimap<std::string, nebo::PilotSlot> pilots;
    for (int i = 1; pilots.size() < 1000; ++i) {
        lua_rawgeti(L, 1, i);
        if (lua_isnil(L, -1)) {
            lua_pop(L, 1);
            break;
        }
        size_t len = 0;
        const char* s = lua_tolstring(L, -1, &len);
        std::string entry = s && len < 120 ? std::string(s, len) : std::string();
        lua_pop(L, 1);
        std::vector<std::string> f;
        for (size_t start = 0;;) {
            size_t eq = entry.find('=', start);
            f.push_back(entry.substr(start, eq == std::string::npos ? std::string::npos : eq - start));
            if (eq == std::string::npos) break;
            start = eq + 1;
        }
        if (f.size() != 4 || f[0].empty() || f[2].empty() || f[3].empty() || (f[1] != "blue" && f[1] != "red")) continue;
        pilots.emplace(f[0], nebo::PilotSlot{f[1] == "blue" ? nebo::Blue : nebo::Red, f[2], f[3]});
    }
    g_server.setPilots(std::move(pilots));
    return 0;
}

// nebo.set_pushed(player_key, version, from, cartridge_json[, aircraft_type]): the latest cartridge
// of that aircraft type pushed to a player ("" removes it; no type: removes all of the player's);
// nebo.clear_pushed(): forget every push (a new mission).
int l_set_pushed(lua_State* L) {
    size_t keyLen = 0, fromLen = 0, jsonLen = 0, typeLen = 0;
    const char* key = luaL_checklstring(L, 1, &keyLen);
    lua_Integer version = luaL_checkinteger(L, 2);
    const char* from = luaL_checklstring(L, 3, &fromLen);
    const char* json = luaL_checklstring(L, 4, &jsonLen);
    const char* type = luaL_optlstring(L, 5, "", &typeLen);
    g_server.setPushed(std::string(key, keyLen), std::string(type, typeLen), version, std::string(from, fromLen), std::string(json, jsonLen));
    return 0;
}

int l_clear_pushed(lua_State*) {
    g_server.clearPushed();
    return 0;
}

// nebo.designations() -> { { coalition, player, json }, ... }: pilots' designations since the last
// call, oldest first (already checked and rebuilt by the network thread).
int l_designations(lua_State* L) {
    auto list = g_server.drainDesignations();
    lua_createtable(L, static_cast<int>(list.size()), 0);
    for (size_t i = 0; i < list.size(); ++i) {
        const auto& d = list[i];
        lua_createtable(L, 0, 3);
        lua_pushstring(L, d.side == nebo::Blue ? "blue" : "red");
        lua_setfield(L, -2, "coalition");
        lua_pushlstring(L, d.player.data(), d.player.size());
        lua_setfield(L, -2, "player");
        lua_pushlstring(L, d.json.data(), d.json.size());
        lua_setfield(L, -2, "json");
        lua_rawseti(L, -2, static_cast<int>(i + 1));
    }
    return 1;
}

// nebo.clients() -> blue, red, blueLogins, redLogins: logged-in controllers per coalition and
// each coalition's login count since start (a new login: build that side's picture at once).
int l_clients(lua_State* L) {
    lua_pushinteger(L, g_server.controllers(nebo::Blue));
    lua_pushinteger(L, g_server.controllers(nebo::Red));
    lua_pushinteger(L, static_cast<lua_Integer>(g_server.logins(nebo::Blue)));
    lua_pushinteger(L, static_cast<lua_Integer>(g_server.logins(nebo::Red)));
    return 4;
}

int l_stop(lua_State*) {
    g_server.stop();
    return 0;
}

int l_version(lua_State* L) {
    lua_pushstring(L, kVersion);
    return 1;
}

std::string toHex(const unsigned char* data, size_t n) {
    static const char digits[] = "0123456789abcdef";
    std::string out;
    out.reserve(n * 2);
    for (size_t i = 0; i < n; ++i) {
        out += digits[data[i] >> 4];
        out += digits[data[i] & 15];
    }
    return out;
}

// nebo.random_hex(bytes) -> hex string from the system's CSPRNG (the analytics key)
int l_random_hex(lua_State* L) {
    lua_Integer n = luaL_checkinteger(L, 1);
    if (n <= 0 || n > 64) return 0;
    std::vector<unsigned char> buf(static_cast<size_t>(n));
    if (!BCRYPT_SUCCESS(BCryptGenRandom(nullptr, buf.data(), static_cast<ULONG>(buf.size()), BCRYPT_USE_SYSTEM_PREFERRED_RNG))) return 0;
    std::string hex = toHex(buf.data(), buf.size());
    lua_pushlstring(L, hex.data(), hex.size());
    return 1;
}

// nebo.hmac_hex(key, text) -> HMAC-SHA256(key, text) as hex: a player's stable id from their
// UCID that cannot be turned back into the UCID without the server's key.
int l_hmac_hex(lua_State* L) {
    size_t keyLen = 0, textLen = 0;
    const char* key = luaL_checklstring(L, 1, &keyLen);
    const char* text = luaL_checklstring(L, 2, &textLen);
    BCRYPT_ALG_HANDLE alg = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    unsigned char digest[32];
    bool ok = BCRYPT_SUCCESS(BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, BCRYPT_ALG_HANDLE_HMAC_FLAG)) &&
              BCRYPT_SUCCESS(BCryptCreateHash(alg, &hash, nullptr, 0, (PUCHAR)key, static_cast<ULONG>(keyLen), 0)) &&
              BCRYPT_SUCCESS(BCryptHashData(hash, (PUCHAR)text, static_cast<ULONG>(textLen), 0)) &&
              BCRYPT_SUCCESS(BCryptFinishHash(hash, digest, sizeof(digest), 0));
    if (hash) BCryptDestroyHash(hash);
    if (alg) BCryptCloseAlgorithmProvider(alg, 0);
    if (!ok) return 0;
    std::string hex = toHex(digest, sizeof(digest));
    lua_pushlstring(L, hex.data(), hex.size());
    return 1;
}

const luaL_Reg kFunctions[] = {
    {"start", l_start},
    {"set_auth", l_set_auth},
    {"publish", l_publish},
    {"publish_all", l_publish_all},
    {"publish_side", l_publish_side},
    {"terrain_begin", l_terrain_begin},
    {"terrain_row", l_terrain_row},
    {"terrain_finish", l_terrain_finish},
    {"poll", l_poll},
    {"commands", l_commands},
    {"command_result", l_command_result},
    {"set_flying", l_set_flying},
    {"set_pushed", l_set_pushed},
    {"clear_pushed", l_clear_pushed},
    {"designations", l_designations},
    {"clients", l_clients},
    {"controllers", l_controllers},
    {"set_pilots", l_set_pilots},
    {"set_cockpits", l_set_cockpits},
    {"set_camera", l_set_camera},
    {"publish_camera", l_publish_camera},
    {"camera", l_camera},
    {"stop", l_stop},
    {"version", l_version},
    {"random_hex", l_random_hex},
    {"hmac_hex", l_hmac_hex},
    {nullptr, nullptr},
};

}  // namespace

extern "C" __declspec(dllexport) int luaopen_nebo(lua_State* L) {
    luaL_register(L, "nebo", kFunctions);
    return 1;
}
