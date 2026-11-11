#pragma once
// TCP server for NEBO controllers. Runs on its own thread; the DCS sim thread only
// calls publish()/drainLog(), which take a mutex briefly and never block on I/O.
#include <atomic>
#include <cstdint>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "discord.h"

namespace nebo {

enum Coalition { Blue = 0, Red = 1, CoalitionCount = 2 };

// A controller's command, checked on the network thread (whitelisted name, field limits, rate)
// and handed to the sim thread. Fields not used by a command stay empty/zero.
struct Command {
    uint64_t client = 0;        // serial of the sending connection, for the result
    Coalition coalition = Blue; // decided by the controller's password at login
    std::string address;        // for the audit log
    int64_t id = 0;             // the client's own id, echoed in the result
    std::string name;           // "message_unit", "mark_add", "mark_remove", "carrier_lights", "identify", "threat_add", "threat_remove"
    int64_t unit = 0;           // DCS unit id
    std::string text;           // message text, mark text, or the identify label
    int seconds = 0;
    int64_t x = 0, z = 0;       // DCS map metres (mark_add, threat_add; nctr: one corner)
    int64_t x2 = 0, z2 = 0;     // nctr: the other corner of the area
    std::string threat;         // threat type name, e.g. "SA-6" (mark_add, may be empty)
    int64_t mark = 0;           // F10 mark id (mark_remove), shared threat id (threat_remove)
    int mode = 0;               // carrier lighting mode, -2 off .. 2 recovery (carrier_lights)
    std::vector<std::string> units;  // dtc_push: recipient unit ids (slot ids)
    std::string blob;           // dtc_push: the cartridge, one raw JSON object
};

// A player on a side (pilot sync): side, DCS player id (the same on the server and every client)
// and a per-session player key. A slot is not needed: with dynamic slots the cartridge is loaded
// in the spawn dialog, before the player has one.
struct PilotSlot {
    Coalition side = Blue;
    std::string player, key;
};

class NetServer {
public:
    ~NetServer();

    // Empty password disables logins for that coalition.
    bool start(uint16_t port, std::string bluePassword, std::string redPassword, std::string& error);
    // Login 2 (before start): the server password(s) a controller proves knowing (HMAC of a
    // challenge, never sent), and Discord (when configured) checked before the password.
    void setAuth(std::vector<std::string> passwords, DiscordConfig discord);
    void stop();
    bool running() const { return running_; }

    // json: one complete JSON object without a trailing newline.
    void publish(Coalition c, std::string json);
    // Message for every coalition (theatre, terrain). Each authenticated client receives the
    // latest version of each channel once, before its next picture.
    void publishAll(const std::string& channel, std::string json);
    // Message for one coalition only on its own channel (e.g. the fast LSO feed): latest version
    // per channel, sent after that coalition's picture so it never holds the picture back.
    void publishSide(Coalition c, const std::string& channel, std::string json);
    std::vector<std::string> drainLog();
    // Commands waiting for the sim thread (oldest first).
    std::vector<Command> drainCommands();
    // Answers a command; dropped if that client has gone. On success a non-empty error
    // string is sent as the result's "value" instead (e.g. the new mark's id).
    void commandResult(uint64_t client, int64_t id, bool ok, const std::string& error);
    // Addresses (IPv4, no port) of players sitting in a cockpit: controller logins from them are
    // refused and open controller connections from them closed ("controllers don't fly").
    void setFlying(std::vector<std::string> addresses);
    // Pilot sync (data cartridges pushed by controllers): the players on a side by address (IPv4,
    // no port; several players may share one), and the latest cartridge pushed to each player
    // key and aircraft type. A connection whose first line is {"type":"dtc_request","version":2,
    // "player":"<DCS player id>","have":n} from that player's address gets the oldest of their
    // cartridges newer than n (the pilot asks again with that version, and gets the next), else
    // dtc_none, and is closed.
    void setPilots(std::multimap<std::string, PilotSlot> pilots);
    void setPushed(const std::string& key, const std::string& type, int64_t version, std::string from, std::string json);
    void clearPushed();
    // Pilots' designations (a "designation" object in their dtc_request, checked and rebuilt by
    // the network thread), oldest first.
    struct Designation {
        Coalition side = Blue;
        std::string player;  // DCS player id
        std::string json;    // {"lat":…,"lon":…,…,"track":"…"}
    };
    std::vector<Designation> drainDesignations();

    // NEBO Camera (docs/PROTOCOL.md "camera"): a login with its own password (empty = off) that
    // sees both sides; its hello carries "camera":true and skips Discord. Refused, and closed later,
    // from any address sitting in a cockpit (setCockpits), whatever allow_flying_controllers says.
    void setCamera(std::string password, int maxFocusHz);
    void setCockpits(std::vector<std::string> addresses);
    // Message for camera logins only (camera_picture, camera_focus): latest version per channel.
    void publishCamera(const std::string& channel, std::string json);
    // Logged-in cameras, camera logins since start, and the objects they want on the fast feed:
    // the union of every camera's camera_set_focus ids (comma-separated) and the highest rate asked.
    int cameras() const { return cameras_.load(); }
    uint64_t cameraLogins() const { return cameraLogins_.load(); }
    std::string cameraFocus(int& hz);

    // Logged-in controllers per coalition, and how many logins each coalition has had since
    // start (the hook builds a picture at once for a new login; counts alone miss one leaving
    // while another joins). Updated by the network thread every loop.
    int controllers(Coalition c) const { return authed_[c].load(); }
    uint64_t logins(Coalition c) const { return logins_[c].load(); }

private:
    void run();
    void log(std::string line);

    std::atomic<int> authed_[CoalitionCount] = {};
    std::atomic<int> cameras_{0};
    std::atomic<uint64_t> cameraLogins_{0};
    std::string cameraPassword_;
    int cameraMaxHz_ = 20;
    std::mutex cameraMutex_;
    std::string cameraFocusIds_;  // under cameraMutex_
    int cameraFocusHz_ = 0;
    std::vector<std::string> cockpits_;  // under flyingMutex_
    std::atomic<uint64_t> logins_[CoalitionCount] = {};

    std::mutex commandMutex_;
    std::vector<Command> commands_;
    std::vector<std::pair<uint64_t, std::string>> results_;  // client serial, JSON line

    std::atomic<bool> running_{false};
    std::atomic<bool> stopRequested_{false};
    std::thread thread_;
    uintptr_t listenSocket_ = ~uintptr_t(0);
    std::string passwords_[CoalitionCount];
    std::vector<std::string> authPasswords_;
    DiscordConfig discord_;

    // Discord calls run on their own threads; results come back through this queue.
    struct DiscordJob {
        uint64_t serial = 0;
        bool recheck = false;
        DiscordUser user;
    };
    struct DiscordQueue {
        std::mutex mutex;
        std::vector<DiscordJob> done;
    };
    std::shared_ptr<DiscordQueue> discordQueue_ = std::make_shared<DiscordQueue>();

    std::mutex pictureMutex_;
    std::string pictures_[CoalitionCount];  // newline-terminated
    uint64_t pictureVersion_[CoalitionCount] = {};

    struct Channel {
        std::shared_ptr<const std::string> data;  // newline-terminated
        uint64_t version = 0;
        int side = -1;  // -1: every coalition; -2: camera logins only; else only that coalition (sent after the picture)
    };
    std::map<std::string, Channel> channels_;

    std::mutex flyingMutex_;
    std::vector<std::string> flying_;

    std::mutex cartridgeMutex_;
    std::multimap<std::string, PilotSlot> pilots_;
    struct Pushed {
        int64_t version = 0;
        std::string from;                         // the controller, for the pilot's log
        std::shared_ptr<const std::string> json;  // the cartridge object
    };
    std::map<std::string, std::map<std::string, Pushed>> pushed_;  // player key -> aircraft type -> latest push
    std::vector<Designation> designations_;                        // under cartridgeMutex_
    // The answer to a dtc_request line from an address (network thread only: rate per address).
    std::string cartridgeReply(const std::string& address, const std::string& line);
    std::map<std::string, int64_t> cartridgeAsked_;  // host -> steady clock seconds

    std::mutex logMutex_;
    std::vector<std::string> logLines_;
};

}  // namespace nebo
