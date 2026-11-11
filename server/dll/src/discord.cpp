#include "discord.h"

#include <cctype>

#include <windows.h>
#include <winhttp.h>

#include "json_lite.h"

namespace nebo {
namespace {

std::wstring widen(const std::string& s) {
    if (s.empty()) return {};
    int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
    std::wstring w(static_cast<size_t>(n), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), w.data(), n);
    return w;
}

std::string formEncode(const std::string& s) {
    static const char hex[] = "0123456789ABCDEF";
    std::string out;
    for (unsigned char c : s) {
        if (std::isalnum(c) || c == '-' || c == '_' || c == '.' || c == '~') out += static_cast<char>(c);
        else { out += '%'; out += hex[c >> 4]; out += hex[c & 15]; }
    }
    return out;
}

// One HTTPS request to discord.com; status 0 when it never got an answer.
int request(const wchar_t* method, const std::string& path, const std::string& bearer, const std::string& form, std::string& body) {
    body.clear();
    int status = 0;
    HINTERNET session = WinHttpOpen(L"NEBO-NEBO (https://github.com/kestrelsnest, 0.10)", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
                                    WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!session) return 0;
    WinHttpSetTimeouts(session, 5000, 5000, 10000, 10000);
    HINTERNET connect = WinHttpConnect(session, L"discord.com", INTERNET_DEFAULT_HTTPS_PORT, 0);
    HINTERNET req = connect ? WinHttpOpenRequest(connect, method, widen("/api/v10" + path).c_str(), nullptr, WINHTTP_NO_REFERER,
                                                 WINHTTP_DEFAULT_ACCEPT_TYPES, WINHTTP_FLAG_SECURE) : nullptr;
    if (req) {
        std::wstring headers;
        if (!bearer.empty()) headers += L"Authorization: Bearer " + widen(bearer) + L"\r\n";
        if (!form.empty()) headers += L"Content-Type: application/x-www-form-urlencoded\r\n";
        BOOL sent = WinHttpSendRequest(req, headers.empty() ? WINHTTP_NO_ADDITIONAL_HEADERS : headers.c_str(),
                                       headers.empty() ? 0 : static_cast<DWORD>(-1L),
                                       form.empty() ? WINHTTP_NO_REQUEST_DATA : const_cast<char*>(form.data()),
                                       static_cast<DWORD>(form.size()), static_cast<DWORD>(form.size()), 0);
        if (sent && WinHttpReceiveResponse(req, nullptr)) {
            DWORD code = 0, size = sizeof(code);
            WinHttpQueryHeaders(req, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_HEADER_NAME_BY_INDEX, &code, &size,
                                WINHTTP_NO_HEADER_INDEX);
            status = static_cast<int>(code);
            DWORD avail = 0;
            while (WinHttpQueryDataAvailable(req, &avail) && avail > 0 && body.size() < 256 * 1024) {
                std::string chunk(avail, '\0');
                DWORD read = 0;
                if (!WinHttpReadData(req, chunk.data(), avail, &read) || read == 0) break;
                body.append(chunk.data(), read);
            }
        }
        WinHttpCloseHandle(req);
    }
    if (connect) WinHttpCloseHandle(connect);
    WinHttpCloseHandle(session);
    return status;
}

DiscordUser failed(int status, const std::string& what) {
    DiscordUser u;
    u.transient = status == 0 || status == 429 || status >= 500;
    u.error = what + (status == 0 ? ": Discord unreachable" : ": HTTP " + std::to_string(status));
    return u;
}

void readMember(const DiscordConfig& cfg, DiscordUser& u) {
    std::string body;
    int status = request(L"GET", "/users/@me/guilds/" + cfg.guild + "/member", u.accessToken, "", body);
    if (status == 404) {  // not in the guild
        u.ok = false;
        u.error = "not a member of the guild";
        return;
    }
    if (status != 200 || !extractJsonStringArray(body, "roles", u.roles)) {
        u = failed(status, "guild member");
        return;
    }
    u.ok = true;
}

}  // namespace

DiscordUser discordLogin(const DiscordConfig& cfg, const std::string& code) {
    std::string body;
    std::string form = "client_id=" + formEncode(cfg.clientId) + "&client_secret=" + formEncode(cfg.secret) +
                       "&grant_type=authorization_code&code=" + formEncode(code);
    int status = request(L"POST", "/oauth2/token", "", form, body);
    DiscordUser u;
    if (status != 200 || !extractJsonString(body, "access_token", u.accessToken)) {
        auto f = failed(status, "code exchange");
        f.transient = f.transient && status != 400 && status != 401;
        return f;
    }
    status = request(L"GET", "/users/@me", u.accessToken, "", body);
    // The user object starts with its own "id"; nested objects (avatar decoration…) come later.
    if (status != 200 || !extractJsonString(body, "id", u.id)) return failed(status, "user");
    std::string global;
    extractJsonString(body, "username", u.name);
    if (extractJsonString(body, "global_name", global) && !global.empty()) u.name = global + " (" + u.name + ")";
    readMember(cfg, u);
    return u;
}

DiscordUser discordRoles(const DiscordConfig& cfg, const std::string& accessToken) {
    DiscordUser u;
    u.accessToken = accessToken;
    readMember(cfg, u);
    return u;
}

bool hasAny(const std::vector<std::string>& have, const std::vector<std::string>& wanted) {
    for (const auto& w : wanted)
        for (const auto& h : have)
            if (h == w) return true;
    return false;
}

std::vector<std::string> splitList(const std::string& csv) {
    std::vector<std::string> out;
    std::string cur;
    for (char c : csv + ",") {
        if (c == ',') {
            if (!cur.empty()) out.push_back(cur);
            cur.clear();
        } else if (c != ' ' && c != '\t') {
            cur += c;
        }
    }
    return out;
}

}  // namespace nebo
