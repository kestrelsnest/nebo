#pragma once
// Discord login for controllers: the client gets a one-time code from the Discord app on its
// PC (RPC AUTHORIZE); the server swaps it for the user's identity and roles in the guild.
// Blocking HTTPS calls (WinHTTP): run them off the network thread.
#include <string>
#include <vector>

namespace nebo {

struct DiscordConfig {
    std::string clientId, secret, guild;
    std::vector<std::string> roles;      // any of these lets a controller in; empty = any guild member
    std::vector<std::string> blueRoles;  // empty = every admitted controller may pick blue
    std::vector<std::string> redRoles;   // empty = every admitted controller may pick red
    bool enabled() const { return !clientId.empty() && !secret.empty() && !guild.empty(); }
};

struct DiscordUser {
    bool ok = false;
    bool transient = false;  // Discord unreachable or failing: not the user's fault
    std::string error;       // short reason for the log
    std::string id, name, accessToken;
    std::vector<std::string> roles;
};

// Swaps the code for an access token, then reads the user and their roles in the guild.
DiscordUser discordLogin(const DiscordConfig& cfg, const std::string& code);
// Reads the roles again with the token from discordLogin (re-check while connected).
DiscordUser discordRoles(const DiscordConfig& cfg, const std::string& accessToken);

bool hasAny(const std::vector<std::string>& have, const std::vector<std::string>& wanted);
// "a,b, c" -> { "a", "b", "c" }
std::vector<std::string> splitList(const std::string& csv);

}  // namespace nebo
