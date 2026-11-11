-- NEBO (NEBO) server settings: Saved Games\<server>\Config\NEBO.lua. On the first start
-- the server makes NEBO.lua from NEBO.example.lua with a random password for each side; change
-- them here if you like (restart the server after a change). The password a controller logs
-- in with decides their coalition. An empty password keeps that side's logins closed.

-- The server password every controller's client proves knowing (never sent over the network).
-- With Discord on (below) it is checked after the Discord role. Controllers pick their side.
password = ""

-- Older clients (before 0.10) log in with one of these and get that side; with Discord on they
-- are refused (they would skip the Discord check). Either one also works as the server password.
blue_password = ""   -- the password blue controllers log in with
red_password = ""    -- the one for red, different from blue's

-- Discord login (optional): controllers click Authorize in their Discord app; the server checks
-- that they are in your Discord server with one of the roles. Make an application at
-- https://discord.com/developers/applications (OAuth2: add the redirect http://127.0.0.1), then:
discord_client_id = ""   -- the Application ID
discord_secret = ""      -- OAuth2 Client Secret (keep it on the server only)
discord_guild = ""       -- your Discord server's ID (right-click it, Copy Server ID)
discord_roles = {}       -- role IDs that may control, e.g. { "1564336254865375374" }; {} = any member
discord_blue_roles = {}  -- role IDs that may pick blue; {} = everyone admitted
discord_red_roles = {}   -- role IDs that may pick red; {} = everyone admitted

port = 10309          -- TCP port controllers connect to (open it in the firewall)
interval = 1.0        -- seconds between pictures (1 is right for most servers)
idle_interval = 5     -- seconds between a side's pictures while none of its controllers is
                      -- logged in (a login gets a picture at once); 0 = always every interval
terrain_step = 500    -- metres between terrain samples for maps and line of sight (sampled once, cached)

-- Enemy ground units (SAMs, vehicles) on the controllers' scopes when own radars detect them:
-- "auto" = only in missions dated enemy_ground_year or later (modern AWACS/JSTARS era), true =
-- always, false = never. Without them controllers find SAMs by RWR triangulation with pilots.
enemy_ground = "auto"
enemy_ground_year = 2012

-- Identification of radar contacts: "auto" = a contact shows its type once DCS identifies it;
-- "manual" = never: controllers identify contacts themselves (NCTR, pilots' calls, right-click →
-- Identify as…), shared with the coalition's other controllers.
identification = "auto"

-- NCTR (non-cooperative target recognition) by your AI radars on request: JEM, HRRP, ISAR and
-- rotor modulation only in missions dated this year or later (size class always).
nctr_year = 1985

-- Radar scan model: each own radar refreshes its contacts at its own scan rate (an E-3 every
-- 10 s, a ship's radar every 5-6 s, a SAM search radar every 1-5 s, a fighter's every 4 s); a
-- contact held by several radars is refreshed by each in turn. false = every contact every second.
radar_scan = true

-- Controllers don't fly: a controller login from the address (IP) of a player sitting in a
-- cockpit is refused, and an open one is closed when its address takes a cockpit. Players
-- behind one router share an address: set true if your controllers and pilots do.
allow_flying_controllers = false

-- NEBO Camera (cinematic shots): a camera login with this password sees every unit of both
-- sides and the missiles in flight. "" = off. Always refused from an address in a cockpit
-- (allow_flying_controllers does not apply), never exported to the website. camera_interval:
-- seconds between full camera pictures (built only while a camera is logged in);
-- camera_focus_hz: the most focus messages a second (the filmed units, at most 4 per camera).
camera_password = ""
camera_interval = 1
camera_focus_hz = 20

-- Pilot sync (data cartridges): controllers push cartridges (F/A-18C, F-16C, F-14B(U)) from the
-- client's DTC tab to pilots with the NEBO DTC hook (NEBO-DTC.lua in their Saved
-- Games\DCS\Scripts\Hooks, your server's address set in it). Nothing is written until a push; a
-- pilot gets only their own side's. false switches it off.
dtc = true

-- Analytics and website export: local files in Saved Games\<server>\NEBO (nothing is sent anywhere).
analytics = true      -- NEBO\analytics\*.jsonl: every mission session's events (players as keyed ids + names)
export = true         -- NEBO\export\: state.json (zones, missions, credits, scores) + events.jsonl for a website
