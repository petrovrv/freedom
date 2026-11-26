# Smart home

The Home app (ext/1 shell, SPEC §3.6a): a responsive room dashboard at
/x/smart-home/home, a tile in the launcher, with the Zigbee pairing button on the page.
The tray shows a small chip only while pairing runs and for a minute after its result. Sockets and lights use explicit on/off actions; thermostat targets
respect device and installation limits. Sensors are read-only. No AI job is started.

The owner has access by default. To share the dashboard, set smartHome.members in
install.json to an explicit comma-separated list of environment IDs and reinstall.
Pairing remains owner-only. Other authenticated users cannot read or control the home.

The homehub 0.3.0 tool provides home-worker, supervised by Bridge on the owner PC.
It reads Home Assistant through its REST API and uses the existing SSH connection
for Zigbee MQTT. Credentials stay in the PC keyring. The PC must be online.

Local configuration: %LOCALAPPDATA%/AIWorkbench/homehub/dashboard.json (override
HOMEHUB_DASHBOARD_CONFIG). Contains ha {url,keyring:[service,account]} and entities:
an object keyed by entity ID, with name, room, actions, optional note and temperature
limits. Only explicitly configured devices are exposed; never upload all HA attributes.
Optional plan: {floors:[{id,name,w,h,outline:[[x,y]...],rooms:[{name,room,x,y,w,h}],
lines:[{kind:door|window|wall,x1,y1,x2,y2}]}]} in any unit, y down. The page then opens on a
floor plan: a room whose `room` matches the room of entities is coloured by its temperature
sensor (or thermostat), shows humidity, target, presence and an open window, and has a 💡 button
per light/socket (same command path as the cards); a click on the room opens its cards in the list.
The worker (homehub 0.3.2+) sends the plan only when the server copy differs (planHash); both
sides keep only numbers in range and short labels (8 floors, 80 rooms, 300 lines, 64 KB).
The configuration contains no credentials and is not part of the public module.

Commands expire after 20 seconds. Server claim is persisted before delivery; a lost
response or worker crash is reported as uncertain rather than replayed. HA acceptance
and the device's reported state are shown separately. Stale states disable controls.
Manual thermostat changes may be replaced by existing HA automation; the module does
not modify schedules.

Pairing opens a 300-second window (254-second finite coordinator lease, renewed only
for the remaining time). A successful non-retained interview from this session turns
the button (and the tray chip) green and closes joining. Click while searching to stop.

Install: check/sign this folder, update homehub, then freedom_modules.py install
modules/smart-home --enable. Disable the module to remove UI and stop its worker.
Already-issued Zigbee leases expire by themselves in at most 254 seconds.

Validation: homehub/test_home_control.py and test_pairing.py (16 tests);
tests/dashboard.php (16 checks); headless browser rendering, filters, edit preservation,
command payload, narrow-screen layout and stale-state disabling; independent review.
Live verification reads states only; physical commands require a user action.

API references:
- https://developers.home-assistant.io/docs/api/rest/
- https://www.zigbee2mqtt.io/guide/usage/mqtt_topics_and_messages.html
