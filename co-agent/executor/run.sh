#!/bin/sh
# SPDX-License-Identifier: AGPL-3.0-or-later
# Started by /agent/ping.php (setsid nohup). Node and the CLIs come from the user's home.
export HOME="${HOME:-$(cd "$(dirname "$0")/../.." && pwd)}"
# Freedom F2: ~/co-agent/bin/node is the Node the setup wizard found on this hosting; the CloudLinux path stays the fallback
export PATH="$HOME/co-agent/bin:/opt/alt/alt-nodejs24/root/usr/bin:$HOME/co-agent/cli/node_modules/.bin:/usr/bin:/bin"
# PHP-spawned processes have a 4 GB virtual memory limit; the default Wasm guard regions
# (used by undici's fetch) do not fit and every fetch fails with "Out of memory".
export NODE_OPTIONS="--disable-wasm-trap-handler"
cd "$(dirname "$0")"
mkdir -p logs
exec node agent.mjs
