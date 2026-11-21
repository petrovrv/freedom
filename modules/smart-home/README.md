# Smart home: quick Zigbee pairing

An owner-only header button opens joining for 300 seconds without starting an AI job.
A rotating radar and countdown show progress; successful device interview turns the
button green and closes joining. Click during search to stop. Timeout, unavailable
worker and command failure have separate states.

The homehub 0.2.0 tool runs the pairing worker on the owner PC (Bridge module service).
It uses the existing SSH key to read MQTT on the home hub and publishes only permit_join.
The CO host credential stays in the OS keyring. The PC must be online.
No HA, Zigbee2MQTT, web core or Bridge restart is required by the implementation.

Installation: check and sign this module, then use freedom_modules.py install
modules/smart-home --enable. The existing homehub tool must be updated to 0.2.0 first.
The placeholder private home-heating module is left untouched.
Disabling this module removes its button and stops its worker; already-issued
coordinator permissions expire by themselves in at most 254 seconds.

Protocol: https://www.zigbee2mqtt.io/guide/usage/mqtt_topics_and_messages.html
A coordinator lease is at most 254 seconds, renewed only for the time remaining in
the original five-minute window. No unlimited joining. Correlated MQTT acknowledgements
confirm commands. Only a non-retained successful device_interview of a device seen
joining/interviewing in this session counts as success.

Validation: homehub/test_pairing.py (8 tests), tests/server.php (6 checks with stub
atomic writer), headless browser idle/start/countdown/success, independent code review.
No real device pairing has been triggered during development.