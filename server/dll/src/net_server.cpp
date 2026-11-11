#define FD_SETSIZE 128
#include "net_server.h"
#include "json_lite.h"

#include <winsock2.h>
#include <ws2tcpip.h>

#include <algorithm>
#include <cctype>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <deque>
#include <list>

#include <bcrypt.h>

namespace nebo {
namespace {

constexpr size_t kMaxClients = 100;
constexpr size_t kMaxPreAuthInput = 4096;
constexpr size_t kMaxInput = 2 * 1024 * 1024;  // a logged-in controller's line (dtc_push carries a cartridge)
constexpr size_t kMaxCartridgeBytes = 1024 * 1024;
constexpr size_t kMaxPushUnits = 50;
constexpr auto kAuthTimeout = std::chrono::seconds(120);  // time to click Authorize in Discord
constexpr auto kRoleCheck = std::chrono::minutes(5);
constexpr char kAuthFail[] = "{\"type\":\"auth_fail\"}\n";
constexpr char kAuthFlying[] = "{\"type\":\"auth_fail\",\"reason\":\"flying\"}\n";
constexpr char kClosedFlying[] = "{\"type\":\"closed\",\"reason\":\"flying\"}\n";

std::string failJson(const char* reason) { return std::string("{\"type\":\"auth_fail\",\"reason\":\"") + reason + "\"}\n"; }
const char* const kCoalitionNames[CoalitionCount] = {"blue", "red"};

// Command limits: a controller cannot flood the sim thread or players' screens.
constexpr size_t kMaxQueuedCommands = 100;
constexpr size_t kMaxTextBytes = 1500;
constexpr size_t kMaxMarkBytes = 200;
constexpr int kBurst = 5;                          // commands at once
constexpr int kPerMinute = 30;                     // commands per minute

// Pending: first line (hello, or an old client's password). Login 2: AwaitDiscord (code) ->
// DiscordBusy (checked off-thread) -> AwaitProof (password proof) -> AwaitSide -> Authed.
enum class ClientState { Pending, AwaitDiscord, DiscordBusy, AwaitProof, AwaitSide, Authed, Closing };

struct Client {
    SOCKET socket;
    std::string address;
    uint64_t serial = 0;
    std::string in;
    std::string out;
    std::string results;  // command results waiting to be sent (before pictures)
    ClientState state = ClientState::Pending;
    Coalition coalition = Blue;
    uint64_t sentVersion = 0;
    std::map<std::string, uint64_t> sentChannels;
    std::chrono::steady_clock::time_point connectedAt = std::chrono::steady_clock::now();
    std::deque<std::chrono::steady_clock::time_point> recentCommands;
    std::string nonce;                  // login 2 challenge
    std::string user, userId, token;    // Discord name, id, access token (role re-checks)
    int sides = 0;                      // sides it may pick: 1 blue, 2 red
    std::chrono::steady_clock::time_point nextRoleCheck;
    bool roleCheckBusy = false;
    bool discordDown = false;           // logged once while Discord fails
    bool cameraLogin = false;           // hello said "camera":true (camera password, no Discord)
    bool camera = false;                // logged in as a camera: both sides, camera channels only
    std::vector<int64_t> focus;         // camera_set_focus ids (at most 4)
    int focusHz = 10;
    std::deque<std::chrono::steady_clock::time_point> recentFocus;

    std::string who() const { return user.empty() ? address : address + " " + user + " [" + userId + "]"; }
};

std::string randomHex(size_t bytes) {
    std::vector<unsigned char> buf(bytes);
    BCryptGenRandom(nullptr, buf.data(), static_cast<ULONG>(buf.size()), BCRYPT_USE_SYSTEM_PREFERRED_RNG);
    static const char digits[] = "0123456789abcdef";
    std::string out;
    for (unsigned char b : buf) { out += digits[b >> 4]; out += digits[b & 15]; }
    return out;
}

// HMAC-SHA256(key, text) as lower-case hex (the password proof).
std::string hmacHex(const std::string& key, const std::string& text) {
    BCRYPT_ALG_HANDLE alg = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    unsigned char digest[32];
    bool ok = BCRYPT_SUCCESS(BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, BCRYPT_ALG_HANDLE_HMAC_FLAG)) &&
              BCRYPT_SUCCESS(BCryptCreateHash(alg, &hash, nullptr, 0, (PUCHAR)key.data(), static_cast<ULONG>(key.size()), 0)) &&
              BCRYPT_SUCCESS(BCryptHashData(hash, (PUCHAR)text.data(), static_cast<ULONG>(text.size()), 0)) &&
              BCRYPT_SUCCESS(BCryptFinishHash(hash, digest, sizeof(digest), 0));
    if (hash) BCryptDestroyHash(hash);
    if (alg) BCryptCloseAlgorithmProvider(alg, 0);
    if (!ok) return {};
    static const char digits[] = "0123456789abcdef";
    std::string out;
    for (unsigned char b : digest) { out += digits[b >> 4]; out += digits[b & 15]; }
    return out;
}

std::string sidesJson(int sides) {
    std::string out = "[";
    if (sides & 1) out += "\"blue\"";
    if (sides & 2) out += std::string(sides & 1 ? "," : "") + "\"red\"";
    return out + "]";
}

}  // namespace

std::string jsonEscape(const std::string& s) {
    std::string out;
    for (unsigned char c : s) {
        if (c == '"' || c == '\\') { out += '\\'; out += static_cast<char>(c); }
        else if (c < 0x20) { char buf[8]; snprintf(buf, sizeof(buf), "\\u%04x", c); out += buf; }
        else out += static_cast<char>(c);
    }
    return out;
}

namespace {

std::string resultJson(int64_t id, bool ok, const std::string& error) {
    return "{\"type\":\"command_result\",\"id\":" + std::to_string(id) + ",\"ok\":" + (ok ? "true" : "false") +
           (ok ? (error.empty() ? std::string() : ",\"value\":\"" + jsonEscape(error) + "\"")
               : ",\"error\":\"" + jsonEscape(error) + "\"") + "}\n";
}

bool equalConstantTime(const std::string& a, const std::string& b) {
    if (a.size() != b.size()) return false;
    unsigned char diff = 0;
    for (size_t i = 0; i < a.size(); ++i) diff |= static_cast<unsigned char>(a[i] ^ b[i]);
    return diff == 0;
}

}  // namespace

void appendUtf8(std::string& out, unsigned cp) {
    if (cp < 0x80) {
        out += static_cast<char>(cp);
    } else if (cp < 0x800) {
        out += static_cast<char>(0xC0 | (cp >> 6));
        out += static_cast<char>(0x80 | (cp & 0x3F));
    } else {
        out += static_cast<char>(0xE0 | (cp >> 12));
        out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
        out += static_cast<char>(0x80 | (cp & 0x3F));
    }
}

// Where the value of "key" starts in a flat JSON object (after the colon), or npos. A "key"
// that appears as a value (as in {"type":"proof","proof":…}) is skipped: a key has a colon.
size_t valueOf(const std::string& json, const char* key) {
    const std::string needle = std::string("\"") + key + "\"";
    for (size_t p = json.find(needle); p != std::string::npos; p = json.find(needle, p + 1)) {
        size_t q = p + needle.size();
        while (q < json.size() && (json[q] == ' ' || json[q] == '\t')) ++q;
        if (q < json.size() && json[q] == ':') {
            ++q;
            while (q < json.size() && (json[q] == ' ' || json[q] == '\t')) ++q;
            return q;
        }
    }
    return std::string::npos;
}

// Reads the string value of "key" from a flat JSON object. Enough for the auth message;
// not a general parser.
bool extractJsonString(const std::string& json, const char* key, std::string& value) {
    size_t p = valueOf(json, key);
    if (p >= json.size() || json[p] != '"') return false;
    ++p;
    value.clear();
    while (p < json.size()) {
        char ch = json[p++];
        if (ch == '"') return true;
        if (ch != '\\') { value += ch; continue; }
        if (p >= json.size()) return false;
        char esc = json[p++];
        switch (esc) {
            case '"': case '\\': case '/': value += esc; break;
            case 'n': value += '\n'; break;
            case 't': value += '\t'; break;
            case 'r': value += '\r'; break;
            case 'b': value += '\b'; break;
            case 'f': value += '\f'; break;
            case 'u': {
                // Parsed by hand: an exception on the network thread would take DCS down.
                if (p + 4 > json.size()) return false;
                unsigned cp = 0;
                for (int i = 0; i < 4; ++i) {
                    char h = json[p++];
                    cp <<= 4;
                    if (h >= '0' && h <= '9') cp |= h - '0';
                    else if (h >= 'a' && h <= 'f') cp |= h - 'a' + 10;
                    else if (h >= 'A' && h <= 'F') cp |= h - 'A' + 10;
                    else return false;
                }
                appendUtf8(value, cp);
                break;
            }
            default: return false;
        }
    }
    return false;
}

// Reads the integer value of "key" from a flat JSON object (no exponent, no fraction).
bool extractJsonInteger(const std::string& json, const char* key, int64_t& value) {
    size_t p = valueOf(json, key);
    if (p >= json.size()) return false;
    bool negative = p < json.size() && json[p] == '-';
    if (negative) ++p;
    if (p >= json.size() || json[p] < '0' || json[p] > '9') return false;
    int64_t v = 0;
    int digits = 0;
    while (p < json.size() && json[p] >= '0' && json[p] <= '9') {
        if (++digits > 15) return false;  // out of any range we accept
        v = v * 10 + (json[p++] - '0');
    }
    value = negative ? -v : v;
    return true;
}

bool extractJsonStringArray(const std::string& json, const char* key, std::vector<std::string>& values) {
    size_t p = valueOf(json, key);
    if (p >= json.size() || json[p] != '[') return false;
    size_t end = json.find(']', p);
    if (end == std::string::npos) return false;
    values.clear();
    std::string inner = json.substr(p + 1, end - p - 1);
    for (size_t q = inner.find('"'); q != std::string::npos; q = inner.find('"', q)) {
        size_t close = inner.find('"', q + 1);
        if (close == std::string::npos) return false;
        values.push_back(inner.substr(q + 1, close - q - 1));
        q = close + 1;
    }
    return true;
}

// Reads a plain decimal number ("-12.345") of "key" from a flat JSON object: by hand, no exponent
// (no exception on the network thread, no locale).
// The integers of the array value of the first "key" ([1,2,3]), at most max of them.
bool extractJsonIntArray(const std::string& json, const char* key, std::vector<int64_t>& values, size_t max) {
    const std::string needle = std::string("\"") + key + "\"";
    size_t p = json.find(needle);
    if (p == std::string::npos) return false;
    p = json.find_first_not_of(" \t", p + needle.size());
    if (p == std::string::npos || json[p] != ':') return false;
    p = json.find_first_not_of(" \t", p + 1);
    if (p == std::string::npos || json[p] != '[') return false;
    const size_t end = json.find(']', p);
    if (end == std::string::npos) return false;
    values.clear();
    size_t i = p + 1;
    while (i < end) {
        while (i < end && (json[i] == ' ' || json[i] == ',' || json[i] == '\t')) ++i;
        if (i >= end) break;
        size_t j = i;
        const bool negative = json[j] == '-';
        if (negative) ++j;
        int64_t v = 0;
        size_t digits = 0;
        while (j < end && json[j] >= '0' && json[j] <= '9' && digits < 18) { v = v * 10 + (json[j] - '0'); ++j; ++digits; }
        if (digits == 0 || (j < end && json[j] != ',' && json[j] != ' ' && json[j] != '\t')) return false;
        if (values.size() >= max) return false;
        values.push_back(negative ? -v : v);
        i = j;
    }
    return true;
}

// True when the hello line asks for a camera login ("camera":true).
bool isCameraHello(const std::string& line) {
    size_t p = line.find("\"camera\"");
    if (p == std::string::npos) return false;
    p = line.find_first_not_of(" \t", p + 8);
    if (p == std::string::npos || line[p] != ':') return false;
    p = line.find_first_not_of(" \t", p + 1);
    return p != std::string::npos && line.compare(p, 4, "true") == 0;
}

bool extractJsonNumber(const std::string& json, const char* key, double& value) {
    size_t p = valueOf(json, key);
    if (p >= json.size()) return false;
    bool negative = json[p] == '-';
    if (negative) ++p;
    if (p >= json.size() || json[p] < '0' || json[p] > '9') return false;
    double v = 0;
    int digits = 0;
    while (p < json.size() && json[p] >= '0' && json[p] <= '9') {
        if (++digits > 12) return false;
        v = v * 10 + (json[p++] - '0');
    }
    if (p < json.size() && json[p] == '.') {
        ++p;
        double scale = 0.1;
        for (int f = 0; p < json.size() && json[p] >= '0' && json[p] <= '9'; ++f) {
            if (f < 9) v += (json[p] - '0') * scale;
            scale /= 10;
            ++p;
        }
    }
    value = negative ? -v : v;
    return true;
}

namespace {

// A pilot's designation (NEBO-Export.lua via the DTC hook) rebuilt from its known fields,
// each range-checked: nothing a pilot sends reaches the controllers as it came. Empty when the
// position is missing or out of range.
std::string cleanDesignation(const std::string& d) {
    double lat = 0, lon = 0;
    if (!extractJsonNumber(d, "lat", lat) || !extractJsonNumber(d, "lon", lon)) return {};
    if (lat < 0 || lat > 90 || lon < 0 || lon > 180) return {};  // the pod page shows no hemisphere: unsigned
    std::string out;
    char buf[64];
    snprintf(buf, sizeof buf, "{\"lat\":%.6f,\"lon\":%.6f", lat, lon);
    out += buf;
    struct Field { const char* key; double lo, hi; const char* fmt; };
    static const Field fields[] = {
        {"alt_ft", -2000, 60000, ",\"alt_ft\":%.0f"},
        {"range_nm", 0, 200, ",\"range_nm\":%.1f"},
        {"had_major_m", 1, 500000, ",\"had_major_m\":%.0f"},
        {"had_minor_m", 1, 500000, ",\"had_minor_m\":%.0f"},
        {"had_pgm", 0, 9, ",\"had_pgm\":%.0f"},
        {"had_brg", 0, 360, ",\"had_brg\":%.0f"},
    };
    for (const auto& f : fields) {
        double v = 0;
        if (extractJsonNumber(d, f.key, v) && v >= f.lo && v <= f.hi) {
            snprintf(buf, sizeof buf, f.fmt, v);
            out += buf;
        }
    }
    std::string track, safe;
    extractJsonString(d, "track", track);
    for (char c : track)
        if (safe.size() < 16 && ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == ' ')) safe += c;
    out += ",\"track\":\"" + safe + "\"}";
    return out;
}

// Checks a command line from an authenticated client. Returns an empty string and fills cmd,
// or the reason it is refused.
// The raw JSON object that is the value of "key" (braces matched outside strings), or false.
bool extractJsonObject(const std::string& json, const char* key, std::string& value) {
    size_t p = valueOf(json, key);
    if (p >= json.size() || json[p] != '{') return false;
    int depth = 0;
    bool inString = false;
    for (size_t i = p; i < json.size(); ++i) {
        char c = json[i];
        if (inString) {
            if (c == '\\') ++i;
            else if (c == '"') inString = false;
        } else if (c == '"') {
            inString = true;
        } else if (c == '{') {
            ++depth;
        } else if (c == '}' && --depth == 0) {
            value = json.substr(p, i - p + 1);
            return true;
        }
    }
    return false;
}

std::string parseCommand(const std::string& line, Command& cmd) {
    if (!extractJsonInteger(line, "id", cmd.id)) return "missing id";
    if (!extractJsonString(line, "name", cmd.name)) return "missing name";
    if (cmd.name == "dtc_push") {
        // Recipients first and the cartridge last in the line: the cartridge has keys of its own.
        if (!extractJsonStringArray(line, "units", cmd.units) || cmd.units.empty()) return "units: the recipients' DCS player ids";
        if (cmd.units.size() > kMaxPushUnits) return "at most 50 recipients";
        for (const auto& u : cmd.units)
            if (u.empty() || u.size() > 20 || u.find_first_not_of("0123456789_") != std::string::npos) return "units: DCS player ids";
        if (!extractJsonObject(line, "cartridge", cmd.blob)) return "cartridge missing";
        if (cmd.blob.size() > kMaxCartridgeBytes) return "cartridge larger than 1 MB";
        return {};
    }
    if (cmd.name == "message_unit") {
        int64_t seconds = 0;
        if (!extractJsonInteger(line, "unit", cmd.unit) || cmd.unit <= 0) return "unit: a DCS unit id";
        if (!extractJsonString(line, "text", cmd.text) || cmd.text.empty()) return "text missing";
        if (cmd.text.size() > kMaxTextBytes) return "text longer than 1500 bytes";
        for (char& ch : cmd.text) {
            if (ch == '\r' || ch == '\t') ch = ' ';
            else if (static_cast<unsigned char>(ch) < 0x20 && ch != '\n') return "text has control characters";
        }
        if (!extractJsonInteger(line, "seconds", seconds) || seconds < 5 || seconds > 120) return "seconds: 5 to 120";
        cmd.seconds = static_cast<int>(seconds);
        return {};
    }
    if (cmd.name == "mark_add") {
        // An F10 mark for the controller's coalition: a short single-line text at a map point.
        if (!extractJsonInteger(line, "x", cmd.x) || !extractJsonInteger(line, "z", cmd.z) ||
            cmd.x < -2000000 || cmd.x > 2000000 || cmd.z < -2000000 || cmd.z > 2000000)
            return "x, z: DCS map metres";
        if (!extractJsonString(line, "text", cmd.text) || cmd.text.empty()) return "text missing";
        if (cmd.text.size() > kMaxMarkBytes) return "text longer than 200 bytes";
        for (char ch : cmd.text)
            if (static_cast<unsigned char>(ch) < 0x20) return "text has control characters";
        extractJsonString(line, "threat", cmd.threat);  // optional
        if (cmd.threat.size() > 24) return "threat: at most 24 characters";
        for (char ch : cmd.threat)
            if (!std::isalnum(static_cast<unsigned char>(ch)) && ch != '-' && ch != ' ' && ch != '/')
                return "threat: letters, digits, '-', '/' and spaces only";
        return {};
    }
    if (cmd.name == "mark_remove") {
        if (!extractJsonInteger(line, "mark", cmd.mark) || cmd.mark <= 0) return "mark: an F10 mark id";
        return {};
    }
    if (cmd.name == "carrier_lights") {
        // A carrier's lighting mode, as the Mission Editor's SET CARRIER LIGHTING MODE:
        // -2 off, -1 auto, 0 navigation, 1 launch, 2 recovery.
        int64_t mode = 0;
        if (!extractJsonInteger(line, "unit", cmd.unit) || cmd.unit <= 0) return "unit: a DCS unit id";
        if (!extractJsonInteger(line, "mode", mode) || mode < -2 || mode > 2) return "mode: -2 to 2";
        cmd.mode = static_cast<int>(mode);
        return {};
    }
    if (cmd.name == "threat_add") {
        // A threat the controller placed, shared with the coalition's controllers (not an F10
        // mark): a threat type at a map point. The hook answers with its id.
        if (!extractJsonInteger(line, "x", cmd.x) || !extractJsonInteger(line, "z", cmd.z) ||
            cmd.x < -2000000 || cmd.x > 2000000 || cmd.z < -2000000 || cmd.z > 2000000)
            return "x, z: DCS map metres";
        if (!extractJsonString(line, "threat", cmd.threat) || cmd.threat.empty()) return "threat missing";
        if (cmd.threat.size() > 24) return "threat: at most 24 characters";
        for (char ch : cmd.threat)
            if (!std::isalnum(static_cast<unsigned char>(ch)) && ch != '-' && ch != ' ' && ch != '/')
                return "threat: letters, digits, '-', '/' and spaces only";
        return {};
    }
    if (cmd.name == "nctr") {
        // NCTR on the contacts in an area (two corners, DCS metres, at most 400 km a side) with
        // the methods named in "methods" ("jem,hrrp,isar,size,rotor"), kept in cmd.text.
        auto inMap = [](int64_t v) { return v >= -2000000 && v <= 2000000; };
        if (!extractJsonInteger(line, "x", cmd.x) || !extractJsonInteger(line, "z", cmd.z) ||
            !extractJsonInteger(line, "x2", cmd.x2) || !extractJsonInteger(line, "z2", cmd.z2) ||
            !inMap(cmd.x) || !inMap(cmd.z) || !inMap(cmd.x2) || !inMap(cmd.z2))
            return "x, z, x2, z2: DCS map metres";
        if (std::llabs(cmd.x2 - cmd.x) > 400000 || std::llabs(cmd.z2 - cmd.z) > 400000) return "area: at most 400 km a side";
        if (!extractJsonString(line, "methods", cmd.text) || cmd.text.empty()) return "methods missing";
        if (cmd.text.size() > 40) return "methods: at most 40 characters";
        for (char ch : cmd.text)
            if (!(ch >= 'a' && ch <= 'z') && ch != ',') return "methods: lower-case names separated by commas";
        return {};
    }
    if (cmd.name == "threat_remove") {
        if (!extractJsonInteger(line, "threat_id", cmd.mark) || cmd.mark <= 0) return "threat_id: a shared threat id";
        return {};
    }
    if (cmd.name == "identify") {
        // An identity for an enemy contact, shared with the coalition's controllers: an RWR
        // label or type name ("29", "SA-6", "Su-34"); empty clears it. Kept in cmd.text.
        if (!extractJsonInteger(line, "unit", cmd.unit) || cmd.unit <= 0) return "unit: a DCS unit id";
        if (!extractJsonString(line, "label", cmd.text)) return "label missing";
        if (cmd.text.size() > 24) return "label: at most 24 characters";
        for (char ch : cmd.text)
            if (!std::isalnum(static_cast<unsigned char>(ch)) && ch != '-' && ch != ' ' && ch != '/')
                return "label: letters, digits, '-', '/' and spaces only";
        return {};
    }
    return "unknown command";
}

// "1.2.3.4:5678" -> "1.2.3.4"
std::string hostOf(const std::string& address) {
    auto colon = address.rfind(':');
    return colon == std::string::npos ? address : address.substr(0, colon);
}

bool isFlying(const std::vector<std::string>& flying, const std::string& address) {
    const std::string host = hostOf(address);
    for (const auto& f : flying)
        if (f == host) return true;
    return false;
}

std::string peerAddress(const sockaddr_in& addr) {
    char buf[INET_ADDRSTRLEN] = {};
    inet_ntop(AF_INET, &addr.sin_addr, buf, sizeof(buf));
    return std::string(buf) + ":" + std::to_string(ntohs(addr.sin_port));
}

}  // namespace

NetServer::~NetServer() { stop(); }

bool NetServer::start(uint16_t port, std::string bluePassword, std::string redPassword, std::string& error) {
    if (running_) return true;

    WSADATA wsa;
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) {
        error = "WSAStartup failed";
        return false;
    }
    SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s == INVALID_SOCKET) {
        error = "socket() failed: " + std::to_string(WSAGetLastError());
        WSACleanup();
        return false;
    }
    // A restarted DCS must not be able to share the port with a stale process.
    BOOL exclusive = TRUE;
    setsockopt(s, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, reinterpret_cast<const char*>(&exclusive), sizeof(exclusive));

    sockaddr_in addr{};
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_ANY);
    addr.sin_port = htons(port);
    if (bind(s, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) == SOCKET_ERROR ||
        listen(s, SOMAXCONN) == SOCKET_ERROR) {
        error = "bind/listen on port " + std::to_string(port) + " failed: " + std::to_string(WSAGetLastError());
        closesocket(s);
        WSACleanup();
        return false;
    }
    u_long nonBlocking = 1;
    ioctlsocket(s, FIONBIO, &nonBlocking);

    listenSocket_ = s;
    passwords_[Blue] = std::move(bluePassword);
    passwords_[Red] = std::move(redPassword);
    stopRequested_ = false;
    running_ = true;
    thread_ = std::thread(&NetServer::run, this);
    log("listening on port " + std::to_string(port));
    return true;
}

void NetServer::setAuth(std::vector<std::string> passwords, DiscordConfig discord) {
    authPasswords_.clear();
    for (auto& p : passwords)
        if (!p.empty()) authPasswords_.push_back(std::move(p));
    discord_ = std::move(discord);
}

void NetServer::setFlying(std::vector<std::string> addresses) {
    std::lock_guard lock(flyingMutex_);
    flying_ = std::move(addresses);
}

void NetServer::setCamera(std::string password, int maxFocusHz) {
    std::lock_guard lock(cameraMutex_);
    cameraPassword_ = std::move(password);
    cameraMaxHz_ = std::clamp(maxFocusHz, 5, 20);
}

void NetServer::setCockpits(std::vector<std::string> addresses) {
    std::lock_guard lock(flyingMutex_);
    cockpits_ = std::move(addresses);
}

void NetServer::publishCamera(const std::string& channel, std::string json) {
    json += '\n';
    auto data = std::make_shared<const std::string>(std::move(json));
    std::lock_guard lock(pictureMutex_);
    auto& ch = channels_[channel];
    ch.data = std::move(data);
    ch.side = -2;
    ++ch.version;
}

std::string NetServer::cameraFocus(int& hz) {
    std::lock_guard lock(cameraMutex_);
    hz = cameraFocusHz_;
    return cameraFocusIds_;
}

void NetServer::setPilots(std::multimap<std::string, PilotSlot> pilots) {
    std::lock_guard lock(cartridgeMutex_);
    pilots_ = std::move(pilots);
}

void NetServer::setPushed(const std::string& key, const std::string& type, int64_t version, std::string from, std::string json) {
    std::lock_guard lock(cartridgeMutex_);
    if (json.empty()) {
        if (type.empty()) pushed_.erase(key);
        else if (auto p = pushed_.find(key); p != pushed_.end()) p->second.erase(type);
        return;
    }
    pushed_[key][type] = Pushed{version, std::move(from), std::make_shared<const std::string>(std::move(json))};
}

void NetServer::clearPushed() {
    std::lock_guard lock(cartridgeMutex_);
    pushed_.clear();
}

std::vector<NetServer::Designation> NetServer::drainDesignations() {
    std::lock_guard lock(cartridgeMutex_);
    std::vector<Designation> out;
    out.swap(designations_);
    return out;
}

// Pilot sync: no password. The address must belong to the player the request names, on a side
// right now (slot or not: dynamic slots load the cartridge before spawning); the answer is the oldest of the cartridges
// (one per aircraft type) controllers pushed to that player that is newer than the one the pilot has: the hook asks again
// with that version and gets the next. One request per address every 3 s (the hook asks every 10 s).
std::string NetServer::cartridgeReply(const std::string& address, const std::string& line) {
    auto fail = [](const char* reason) { return std::string("{\"type\":\"dtc_fail\",\"reason\":\"") + reason + "\"}\n"; };
    int64_t version = 0, have = 0;
    std::string player;
    if (!extractJsonInteger(line, "version", version) || version < 2) return fail("update");  // a 0.12 hook
    extractJsonInteger(line, "have", have);
    extractJsonString(line, "player", player);
    const std::string host = hostOf(address);
    const int64_t now = std::chrono::duration_cast<std::chrono::seconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
    auto asked = cartridgeAsked_.find(host);
    if (asked != cartridgeAsked_.end() && now - asked->second < 3) return fail("too_soon");
    cartridgeAsked_[host] = now;
    if (cartridgeAsked_.size() > 1000) cartridgeAsked_.clear();

    std::lock_guard lock(cartridgeMutex_);
    const PilotSlot* slot = nullptr;
    for (auto [it, end] = pilots_.equal_range(host); it != end; ++it)
        if (it->second.player == player) slot = &it->second;
    if (!slot) return fail("not_on_a_side");
    // A designation riding on the ask (F-16 targeting pod / HAD): for the sim thread, which
    // passes it to this side's controllers. Bounded: a stalled sim thread cannot grow it.
    std::string designation;
    if (extractJsonObject(line, "designation", designation)) {
        std::string clean = cleanDesignation(designation);
        if (!clean.empty() && designations_.size() < 200) designations_.push_back({slot->side, player, std::move(clean)});
    }
    const Pushed* next = nullptr;
    if (auto p = pushed_.find(slot->key); p != pushed_.end())
        for (const auto& [type, push] : p->second)
            if (push.version > have && (!next || push.version < next->version)) next = &push;
    if (!next) return "{\"type\":\"dtc_none\"}\n";
    log("cartridge " + std::to_string(next->version) + " sent to " + address + " (player " + player + ", " +
        std::to_string(next->json->size()) + " bytes, from " + next->from + ")");
    return "{\"type\":\"dtc_push\",\"version\":" + std::to_string(next->version) + ",\"from\":\"" +
           jsonEscape(next->from) + "\",\"cartridge\":" + *next->json + "}\n";
}

void NetServer::stop() {
    if (!running_) return;
    stopRequested_ = true;
    if (thread_.joinable()) thread_.join();
    closesocket(static_cast<SOCKET>(listenSocket_));
    listenSocket_ = ~uintptr_t(0);
    WSACleanup();
    running_ = false;
}

void NetServer::publish(Coalition c, std::string json) {
    json += '\n';
    std::lock_guard lock(pictureMutex_);
    pictures_[c] = std::move(json);
    ++pictureVersion_[c];
}

void NetServer::publishAll(const std::string& channel, std::string json) {
    json += '\n';
    auto data = std::make_shared<const std::string>(std::move(json));
    std::lock_guard lock(pictureMutex_);
    auto& ch = channels_[channel];
    ch.data = std::move(data);
    ++ch.version;
}

void NetServer::publishSide(Coalition c, const std::string& channel, std::string json) {
    json += '\n';
    auto data = std::make_shared<const std::string>(std::move(json));
    std::lock_guard lock(pictureMutex_);
    auto& ch = channels_[channel + "@" + kCoalitionNames[c]];
    ch.data = std::move(data);
    ch.side = c;
    ++ch.version;
}

std::vector<std::string> NetServer::drainLog() {
    std::lock_guard lock(logMutex_);
    std::vector<std::string> lines;
    lines.swap(logLines_);
    return lines;
}

std::vector<Command> NetServer::drainCommands() {
    std::lock_guard lock(commandMutex_);
    std::vector<Command> out;
    out.swap(commands_);
    return out;
}

void NetServer::commandResult(uint64_t client, int64_t id, bool ok, const std::string& error) {
    std::lock_guard lock(commandMutex_);
    if (results_.size() < 1000) results_.emplace_back(client, resultJson(id, ok, error));
}

void NetServer::log(std::string line) {
    std::lock_guard lock(logMutex_);
    if (logLines_.size() < 1000) logLines_.push_back(std::move(line));
}

void NetServer::run() {
    const SOCKET listener = static_cast<SOCKET>(listenSocket_);
    std::list<Client> clients;
    std::string latest[CoalitionCount];
    uint64_t latestVersion[CoalitionCount] = {};
    std::map<std::string, Channel> channels;
    uint64_t nextSerial = 1;

    while (!stopRequested_) {
        {
            std::lock_guard lock(pictureMutex_);
            for (int c = 0; c < CoalitionCount; ++c) {
                if (latestVersion[c] != pictureVersion_[c]) {
                    latest[c] = pictures_[c];
                    latestVersion[c] = pictureVersion_[c];
                }
            }
            channels = channels_;  // copies shared pointers only
        }
        std::vector<std::string> flying, cockpits;
        {
            std::lock_guard lock(flyingMutex_);
            flying = flying_;
            cockpits = cockpits_;
        }
        std::string cameraPassword;
        int cameraMaxHz;
        {
            std::lock_guard lock(cameraMutex_);
            cameraPassword = cameraPassword_;
            cameraMaxHz = cameraMaxHz_;
        }
        // Discord answers (logins and role re-checks) from their threads.
        {
            std::vector<DiscordJob> done;
            {
                std::lock_guard lock(discordQueue_->mutex);
                done.swap(discordQueue_->done);
            }
            for (auto& job : done) {
                auto it = std::find_if(clients.begin(), clients.end(), [&](const Client& c) { return c.serial == job.serial; });
                if (it == clients.end()) continue;
                Client& cl = *it;
                const auto& u = job.user;
                const bool admitted = u.ok && (discord_.roles.empty() || hasAny(u.roles, discord_.roles));
                const int sides = (discord_.blueRoles.empty() || hasAny(u.roles, discord_.blueRoles) ? 1 : 0) |
                                  (discord_.redRoles.empty() || hasAny(u.roles, discord_.redRoles) ? 2 : 0);
                if (!job.recheck) {
                    if (cl.state != ClientState::DiscordBusy) continue;
                    if (!u.ok) {
                        cl.out += failJson(u.transient ? "discord_down" : "discord");
                        cl.state = ClientState::Closing;
                        log("Discord login failed from " + cl.address + ": " + u.error);
                    } else if (!admitted || sides == 0) {
                        cl.out += failJson("role");
                        cl.state = ClientState::Closing;
                        log("login refused from " + cl.address + " " + u.name + " [" + u.id + "]: no controller role on Discord (" + std::to_string(u.roles.size()) + " roles there: " + [&] { std::string r; for (const auto& x : u.roles) r += x + " "; return r; }() + "; " + std::to_string(discord_.roles.size()) + " access roles set)");
                    } else {
                        cl.user = u.name;
                        cl.userId = u.id;
                        cl.token = u.accessToken;
                        cl.sides = sides;
                        cl.nextRoleCheck = std::chrono::steady_clock::now() + kRoleCheck;
                        cl.out += "{\"type\":\"auth_stage\",\"stage\":\"password\",\"user\":\"" + jsonEscape(u.name) + "\"}\n";
                        cl.state = ClientState::AwaitProof;
                        log("Discord login ok from " + cl.who());
                    }
                } else {
                    cl.roleCheckBusy = false;
                    cl.nextRoleCheck = std::chrono::steady_clock::now() + kRoleCheck;
                    if (cl.state != ClientState::Authed) continue;
                    if (!u.ok && u.transient) {
                        if (!cl.discordDown) log("role check for " + cl.who() + " failed (" + u.error + "): kept connected");
                        cl.discordDown = true;
                        continue;
                    }
                    cl.discordDown = false;
                    if (!admitted || !(sides & (1 << cl.coalition))) {
                        cl.out += "{\"type\":\"closed\",\"reason\":\"role\"}\n";
                        cl.state = ClientState::Closing;
                        log("controller closed: " + cl.who() + " (" + kCoalitionNames[cl.coalition] + ") lost the controller role on Discord");
                    }
                }
            }
            for (auto& cl : clients) {
                if (cl.state != ClientState::Authed || cl.token.empty() || cl.roleCheckBusy ||
                    std::chrono::steady_clock::now() < cl.nextRoleCheck) continue;
                cl.roleCheckBusy = true;
                auto queue = discordQueue_;
                std::thread([queue, cfg = discord_, token = cl.token, serial = cl.serial] {
                    DiscordJob job{serial, true, discordRoles(cfg, token)};
                    std::lock_guard lock(queue->mutex);
                    queue->done.push_back(std::move(job));
                }).detach();
            }
        }

        // A controller whose address took a cockpit since logging in is closed.
        // A camera is closed by any cockpit at its address (allow_flying_controllers does not apply).
        for (auto& cl : clients) {
            if (cl.state == ClientState::Authed && cl.camera && isFlying(cockpits, cl.address)) {
                cl.out += kClosedFlying;
                cl.state = ClientState::Closing;
                log("camera closed: " + cl.address + " is in a cockpit");
            } else if (cl.state == ClientState::Authed && !cl.camera && isFlying(flying, cl.address)) {
                cl.out += kClosedFlying;
                cl.state = ClientState::Closing;
                log(std::string("controller closed: ") + cl.address + " (" + kCoalitionNames[cl.coalition] + ") is in a cockpit");
            }
        }
        {
            // Results from the sim thread go to the client that sent the command, if still here.
            std::vector<std::pair<uint64_t, std::string>> results;
            {
                std::lock_guard lock(commandMutex_);
                results.swap(results_);
            }
            for (auto& [serial, json] : results)
                for (auto& cl : clients)
                    if (cl.serial == serial && cl.state == ClientState::Authed) cl.results += json;
        }

        // Queue the next message only once the previous one is fully sent: a slow client
        // skips intermediate pictures instead of growing an unbounded buffer. Command
        // results go first, then channel messages (theatre, terrain), each version once.
        for (auto& cl : clients) {
            if (cl.state != ClientState::Authed || !cl.out.empty()) continue;
            if (!cl.results.empty()) {
                cl.out.swap(cl.results);
                continue;
            }
            if (cl.camera) {
                // A camera: public channels (terrain, airfields...), then its own (camera_focus
                // before camera_picture, std::map order). Never a coalition picture or channel.
                for (int pass = -1; pass >= -2 && cl.out.empty(); --pass) {
                    for (const auto& [name, ch] : channels) {
                        if (ch.side != pass) continue;
                        auto& sent = cl.sentChannels[name];
                        if (sent != ch.version && ch.data) {
                            cl.out = *ch.data;
                            sent = ch.version;
                            break;
                        }
                    }
                }
                continue;
            }
            for (const auto& [name, ch] : channels) {
                if (ch.side != -1) continue;  // coalition channels go after the picture, camera ones never
                auto& sent = cl.sentChannels[name];
                if (sent != ch.version && ch.data) {
                    cl.out = *ch.data;
                    sent = ch.version;
                    break;
                }
            }
            if (cl.out.empty() && cl.sentVersion != latestVersion[cl.coalition]) {
                cl.out = latest[cl.coalition];
                cl.sentVersion = latestVersion[cl.coalition];
            }
            if (cl.out.empty()) {
                for (const auto& [name, ch] : channels) {
                    if (ch.side != static_cast<int>(cl.coalition)) continue;
                    auto& sent = cl.sentChannels[name];
                    if (sent != ch.version && ch.data) {
                        cl.out = *ch.data;
                        sent = ch.version;
                        break;
                    }
                }
            }
        }

        fd_set readSet, writeSet;
        FD_ZERO(&readSet);
        FD_ZERO(&writeSet);
        FD_SET(listener, &readSet);
        for (auto& cl : clients) {
            FD_SET(cl.socket, &readSet);
            if (!cl.out.empty()) FD_SET(cl.socket, &writeSet);
        }
        // A camera's focus feed (10-20 Hz) must not wait for the 100 ms tick: wake every 5 ms then.
        const bool anyCamera = std::any_of(clients.begin(), clients.end(), [](const Client& c) { return c.camera; });
        timeval timeout{0, (anyCamera ? 5 : 100) * 1000};
        if (select(0, &readSet, &writeSet, nullptr, &timeout) == SOCKET_ERROR) {
            log("select failed: " + std::to_string(WSAGetLastError()));
            std::this_thread::sleep_for(std::chrono::milliseconds(100));
            continue;
        }

        if (FD_ISSET(listener, &readSet)) {
            sockaddr_in addr{};
            int len = sizeof(addr);
            SOCKET s = accept(listener, reinterpret_cast<sockaddr*>(&addr), &len);
            if (s != INVALID_SOCKET) {
                if (clients.size() >= kMaxClients) {
                    closesocket(s);
                    log("rejected " + peerAddress(addr) + ": too many clients");
                } else {
                    u_long nonBlocking = 1;
                    ioctlsocket(s, FIONBIO, &nonBlocking);
                    BOOL noDelay = TRUE;
                    setsockopt(s, IPPROTO_TCP, TCP_NODELAY, reinterpret_cast<const char*>(&noDelay), sizeof(noDelay));
                    clients.push_back(Client{s, peerAddress(addr), nextSerial++});
                }
            }
        }

        const auto now = std::chrono::steady_clock::now();
        for (auto it = clients.begin(); it != clients.end();) {
            Client& cl = *it;
            bool drop = false;

            if (FD_ISSET(cl.socket, &readSet)) {
                char buf[4096];
                int n = recv(cl.socket, buf, sizeof(buf), 0);
                if (n <= 0) {
                    drop = true;
                } else if (cl.state != ClientState::Closing) {
                    cl.in.append(buf, n);
                }
            }

            // Pilot sync: a cartridge request instead of a login (answered once, then closed).
            if (!drop && cl.state == ClientState::Pending) {
                size_t nl = cl.in.find('\n');
                std::string type;
                if (nl != std::string::npos && extractJsonString(cl.in.substr(0, nl), "type", type) && type == "dtc_request") {
                    std::string request = cl.in.substr(0, nl);
                    cl.in.erase(0, nl + 1);
                    cl.out = cartridgeReply(cl.address, request);
                    cl.state = ClientState::Closing;
                }
            }

            const bool loggingIn = cl.state != ClientState::Authed && cl.state != ClientState::Closing;
            size_t nlAuth = loggingIn ? cl.in.find('\n') : std::string::npos;
            std::string authLine = nlAuth == std::string::npos ? std::string() : cl.in.substr(0, nlAuth);
            std::string authType;
            const bool login2 = !drop && loggingIn && nlAuth != std::string::npos &&
                                (cl.state != ClientState::Pending || (extractJsonString(authLine, "type", authType) && authType == "hello"));
            if (login2) {
                cl.in.erase(0, nlAuth + 1);
                extractJsonString(authLine, "type", authType);
                if (cl.state == ClientState::Pending && isCameraHello(authLine)) {
                    // NEBO Camera: its own password, no Discord, no controller side.
                    cl.nonce = randomHex(16);
                    cl.cameraLogin = true;
                    cl.out += "{\"type\":\"hello\",\"version\":2,\"nonce\":\"" + cl.nonce + "\"}\n";
                    cl.state = ClientState::AwaitProof;
                    if (cameraPassword.empty()) {
                        cl.out += failJson("closed");
                        cl.state = ClientState::Closing;
                        log("camera login refused from " + cl.address + ": no camera_password on this server");
                    }
                } else if (cl.cameraLogin && cl.state == ClientState::AwaitProof && authType == "proof") {
                    std::string proof;
                    extractJsonString(authLine, "proof", proof);
                    if (proof.empty() || !equalConstantTime(proof, hmacHex(cameraPassword, cl.nonce))) {
                        cl.out += failJson("password");
                        cl.state = ClientState::Closing;
                        log("camera auth failed from " + cl.address + ": wrong password");
                    } else if (isFlying(cockpits, cl.address)) {
                        cl.out += kAuthFlying;
                        cl.state = ClientState::Closing;
                        log("camera login refused from " + cl.address + ": that address is in a cockpit");
                    } else {
                        cl.out += "{\"type\":\"auth_ok\",\"version\":2,\"user\":null,\"sides\":[\"camera\"]}\n";
                        cl.state = ClientState::AwaitSide;
                    }
                } else if (cl.cameraLogin && cl.state == ClientState::AwaitSide && authType == "side") {
                    std::string side;
                    extractJsonString(authLine, "coalition", side);
                    if (side != "camera") {
                        cl.out += failJson("side");
                        cl.state = ClientState::Closing;
                    } else {
                        cl.camera = true;
                        cl.out += "{\"type\":\"side_ok\",\"coalition\":\"camera\"}\n";
                        cl.state = ClientState::Authed;
                        ++cameraLogins_;
                        log("camera connected from " + cl.address);
                    }
                } else if (cl.cameraLogin) {
                    cl.out += failJson("protocol");
                    cl.state = ClientState::Closing;
                } else if (cl.state == ClientState::Pending) {
                    cl.nonce = randomHex(16);
                    cl.out += "{\"type\":\"hello\",\"version\":2,\"nonce\":\"" + cl.nonce + "\"" +
                              (discord_.enabled() ? ",\"discord\":\"" + jsonEscape(discord_.clientId) + "\"" : std::string()) + "}\n";
                    cl.state = discord_.enabled() ? ClientState::AwaitDiscord : ClientState::AwaitProof;
                    if (authPasswords_.empty() && !discord_.enabled()) {
                        cl.out += failJson("closed");
                        cl.state = ClientState::Closing;
                    }
                } else if (cl.state == ClientState::AwaitDiscord && authType == "discord") {
                    std::string code;
                    if (!extractJsonString(authLine, "code", code) || code.empty() || code.size() > 200) {
                        cl.out += failJson("discord");
                        cl.state = ClientState::Closing;
                    } else {
                        cl.state = ClientState::DiscordBusy;
                        auto queue = discordQueue_;
                        std::thread([queue, cfg = discord_, code, serial = cl.serial] {
                            DiscordJob job{serial, false, discordLogin(cfg, code)};
                            std::lock_guard lock(queue->mutex);
                            queue->done.push_back(std::move(job));
                        }).detach();
                    }
                } else if (cl.state == ClientState::AwaitProof && authType == "proof") {
                    std::string proof;
                    bool match = false;
                    extractJsonString(authLine, "proof", proof);
                    for (const auto& pw : authPasswords_)
                        if (!proof.empty() && equalConstantTime(proof, hmacHex(pw, cl.nonce))) match = true;
                    if (authPasswords_.empty() && discord_.enabled()) match = true;  // Discord alone
                    if (!discord_.enabled()) cl.sides = 3;
                    if (!match) {
                        cl.out += failJson("password");
                        cl.state = ClientState::Closing;
                        log("auth failed from " + cl.who() + ": wrong password");
                    } else if (isFlying(flying, cl.address)) {
                        cl.out += kAuthFlying;
                        cl.state = ClientState::Closing;
                        log("login refused from " + cl.who() + ": that address is in a cockpit");
                    } else {
                        cl.out += "{\"type\":\"auth_ok\",\"version\":2,\"user\":" +
                                  (cl.user.empty() ? std::string("null") : "\"" + jsonEscape(cl.user) + "\"") +
                                  ",\"sides\":" + sidesJson(cl.sides) + "}\n";
                        cl.state = ClientState::AwaitSide;
                    }
                } else if (cl.state == ClientState::AwaitSide && authType == "side") {
                    std::string side;
                    extractJsonString(authLine, "coalition", side);
                    int c = side == "blue" ? Blue : side == "red" ? Red : -1;
                    if (c < 0 || !(cl.sides & (1 << c))) {
                        cl.out += failJson("side");
                        cl.state = ClientState::Closing;
                    } else {
                        cl.coalition = static_cast<Coalition>(c);
                        cl.out += std::string("{\"type\":\"side_ok\",\"coalition\":\"") + kCoalitionNames[c] + "\"}\n";
                        cl.state = ClientState::Authed;
                        ++logins_[c];
                        log(std::string("controller connected from ") + cl.who() + " (" + kCoalitionNames[c] + ")");
                    }
                } else {
                    cl.out += failJson("protocol");
                    cl.state = ClientState::Closing;
                }
            } else if (!drop && loggingIn && cl.state != ClientState::Pending) {
                if (cl.in.size() > kMaxPreAuthInput || now - cl.connectedAt > kAuthTimeout) {
                    log("dropped " + cl.address + ": login not finished");
                    drop = true;
                }
            }
            if (!drop && cl.state == ClientState::Pending) {
                size_t nl = cl.in.find('\n');
                if (nl != std::string::npos && discord_.enabled()) {
                    // An old client (password only) would skip the Discord check.
                    cl.in.erase(0, nl + 1);
                    cl.out = failJson("update");
                    cl.state = ClientState::Closing;
                    log("refused " + cl.address + ": old client (Discord login needed)");
                } else if (nl != std::string::npos) {
                    std::string password;
                    int coalition = -1;
                    if (extractJsonString(cl.in.substr(0, nl), "password", password)) {
                        for (int c = 0; c < CoalitionCount; ++c) {
                            if (!passwords_[c].empty() && equalConstantTime(password, passwords_[c])) coalition = c;
                        }
                    }
                    cl.in.erase(0, nl + 1);
                    if (coalition < 0) {
                        cl.out = kAuthFail;
                        cl.state = ClientState::Closing;
                        log("auth failed from " + cl.address);
                    } else if (isFlying(flying, cl.address)) {
                        cl.out = kAuthFlying;
                        cl.state = ClientState::Closing;
                        log(std::string("login refused from ") + cl.address + " (" + kCoalitionNames[coalition] + "): that address is in a cockpit");
                    } else {
                        cl.coalition = static_cast<Coalition>(coalition);
                        cl.out = std::string("{\"type\":\"auth_ok\",\"coalition\":\"") + kCoalitionNames[coalition] + "\"}\n";
                        cl.state = ClientState::Authed;
                        ++logins_[coalition];
                        log(std::string("controller connected from ") + cl.address + " (" + kCoalitionNames[coalition] + ")");
                    }
                } else if (cl.in.size() > kMaxPreAuthInput || now - cl.connectedAt > kAuthTimeout) {
                    log("dropped " + cl.address + ": no auth");
                    drop = true;
                }
            }
            // After login a client may send commands, one JSON object per line.
            if (!drop && cl.state == ClientState::Authed && cl.camera) {
                // A camera sends only camera_set_focus: at most 4 ids, a rate clamped to
                // 5..camera_focus_hz, at most 10 requests a second (more are ignored).
                size_t nl;
                while ((nl = cl.in.find('\n')) != std::string::npos) {
                    std::string line = cl.in.substr(0, nl);
                    cl.in.erase(0, nl + 1);
                    std::string type;
                    if (!extractJsonString(line, "type", type) || type != "camera_set_focus") continue;
                    while (!cl.recentFocus.empty() && now - cl.recentFocus.front() > std::chrono::seconds(1)) cl.recentFocus.pop_front();
                    if (cl.recentFocus.size() >= 10) continue;
                    cl.recentFocus.push_back(now);
                    std::vector<int64_t> ids;
                    if (!extractJsonIntArray(line, "ids", ids, 4)) continue;
                    int64_t hz = 10;
                    extractJsonInteger(line, "hz", hz);
                    cl.focus = std::move(ids);
                    cl.focusHz = static_cast<int>(std::clamp<int64_t>(hz, 5, cameraMaxHz));
                }
                if (cl.in.size() > kMaxPreAuthInput) cl.in.clear();
            } else if (!drop && cl.state == ClientState::Authed) {
                size_t nl;
                while ((nl = cl.in.find('\n')) != std::string::npos) {
                    std::string line = cl.in.substr(0, nl);
                    cl.in.erase(0, nl + 1);
                    std::string type;
                    if (!extractJsonString(line, "type", type) || type != "command") continue;
                    Command cmd;
                    std::string refused = parseCommand(line, cmd);
                    // Rate: at most kBurst within a second and kPerMinute within a minute.
                    while (!cl.recentCommands.empty() && now - cl.recentCommands.front() > std::chrono::minutes(1))
                        cl.recentCommands.pop_front();
                    if (refused.empty()) {
                        int lastSecond = 0;
                        for (auto t : cl.recentCommands) if (now - t < std::chrono::seconds(1)) ++lastSecond;
                        if (lastSecond >= kBurst || static_cast<int>(cl.recentCommands.size()) >= kPerMinute) refused = "too many commands, wait a moment";
                    }
                    if (refused.empty()) {
                        cmd.client = cl.serial;
                        cmd.coalition = cl.coalition;
                        cmd.address = cl.who();
                        std::lock_guard lock(commandMutex_);
                        if (commands_.size() >= kMaxQueuedCommands) refused = "server busy";
                        else commands_.push_back(std::move(cmd));
                    }
                    if (refused.empty()) {
                        cl.recentCommands.push_back(now);
                    } else {
                        cl.results += resultJson(cmd.id, false, refused);
                        log("command refused from " + cl.address + " (" + kCoalitionNames[cl.coalition] + "): " + refused);
                    }
                }
                if (cl.in.size() > kMaxInput) cl.in.clear();  // a line that never ends
            }

            // Sockets are non-blocking, so trying without waiting for writability is safe
            // and sends auth replies in the same pass.
            if (!drop && !cl.out.empty()) {
                int n = send(cl.socket, cl.out.data(), static_cast<int>(cl.out.size()), 0);
                if (n > 0) {
                    cl.out.erase(0, n);
                } else if (WSAGetLastError() != WSAEWOULDBLOCK) {
                    drop = true;
                }
            }
            if (cl.state == ClientState::Closing && cl.out.empty()) drop = true;

            if (drop) {
                if (cl.state == ClientState::Authed) log(std::string(cl.camera ? "camera" : "controller") + " disconnected: " + cl.who());
                closesocket(cl.socket);
                it = clients.erase(it);
            } else {
                ++it;
            }
        }

        int authed[CoalitionCount] = {};
        int cameras = 0, focusHz = 0;
        std::vector<int64_t> focusIds;
        for (const auto& cl : clients) {
            if (cl.state != ClientState::Authed) continue;
            if (!cl.camera) {
                ++authed[cl.coalition];
                continue;
            }
            ++cameras;
            for (auto id : cl.focus)
                if (focusIds.size() < 16 && std::find(focusIds.begin(), focusIds.end(), id) == focusIds.end()) focusIds.push_back(id);
            if (!cl.focus.empty()) focusHz = std::max(focusHz, cl.focusHz);
        }
        for (int c = 0; c < CoalitionCount; ++c) authed_[c] = authed[c];
        cameras_ = cameras;
        {
            std::string csv;
            for (auto id : focusIds) csv += (csv.empty() ? "" : ",") + std::to_string(id);
            std::lock_guard lock(cameraMutex_);
            cameraFocusIds_ = std::move(csv);
            cameraFocusHz_ = focusHz;
        }
    }

    for (auto& cl : clients) closesocket(cl.socket);
    for (auto& a : authed_) a = 0;
    cameras_ = 0;
}

}  // namespace nebo
