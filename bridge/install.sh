#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-or-later
# Installs Bridge on Linux for the current user (Freedom F3a, SPEC §4.2): Debian / Ubuntu, Fedora; x86_64 and aarch64.
# The Linux counterpart of bridge/install.ps1; the setup wizard calls it (python3 install/freedom_setup.py bridge).
#
#   bash bridge/install.sh [--binary FILE [--allow-unverified] | --build] [--config-from FILE] [--host-id ID]
#                          [--channel DIR] [--release-key BASE64] [--deps] [--keyring auto|session|headless]
#                          [--no-linger] [--no-start] [--python PY]
#   bash bridge/install.sh --uninstall          stop and remove the services (config, logs, keyring and channel stay)
#
# What it does, in this order:
#   1. --deps: packages through apt-get / dnf (sudo): git, python3 with keyring, libsecret tools, gnome-keyring,
#      D-Bus user session, ICU.
#   2. Secrets (SPEC principle 2, "secrets only in the keyring"): the Secret Service of the session (a desktop's
#      GNOME Keyring / KWallet) when it keeps a probe secret; else, on a server without a desktop, gnome-keyring as a
#      systemd user service (aiwb-keyring.service, D-Bus activation of org.freedesktop.secrets points at it), its
#      login keyring unlocked at start with a random password from ~/.config/aiwb-keyring/unlock (0600). Bridge,
#      secret-tool and Python keyring use it alike (service = aiwb/<name>, username = default).
#   3. config.json in ~/.local/share/AIWorkbenchBridge: copied from --config-from, else kept, else made from install.json
#      (install/aiwb_install.py bridge-config: agent CLIs without .exe, a local workbench root); --host-id sets hostId.
#   4. Bridge build: --binary (a release file BridgeAgent-<v>-linux-<arch>, checked against freedom-manifest.json next
#      to it), else such a file next to this source tree, else built here from the sources (.NET 8+ SDK, self-contained
#      single file).
#   5. Release key of this computer (ECDSA P-256, private part in the keyring as aiwb/bridge-release) signs the build
#      into the local update channel (default ~/.local/share/AIWorkbenchBridge/updates) - the channel format of the
#      Windows launcher. --release-key BASE64: trust another computer's key instead (its channel feeds this one).
#   6. The launcher (a copy of the build, --launcher) in ~/.local/lib/aiworkbench-bridge: starts the newest signed
#      version, drains and switches to a newer one, rolls back a version that dies in probation.
#   7. systemd user service aiworkbench-bridge.service (Restart=always) + loginctl enable-linger, so it runs from boot
#      without anybody logged in. Why a user service and not a system one: Bridge runs the agent CLIs (claude, codex)
#      with their own sign-in in the home of the account that signed in, as the logon task of Windows and the
#      LaunchAgent of macOS do; no root process. For a dedicated account: sudo useradd -m aiwb, then run this script as
#      that user (sudo -iu aiwb bash .../install.sh); linger is switched on through sudo when needed.
# Environment for a rehearsal next to a real install: AIWB_BRIDGE_APP (launcher folder), AIWB_BRIDGE_UNIT (unit name).
set -euo pipefail

SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP="${AIWB_BRIDGE_APP:-$HOME/.local/lib/aiworkbench-bridge}"
DATA="${XDG_DATA_HOME:-$HOME/.local/share}"
STATE="$DATA/AIWorkbenchBridge"
CFG="$STATE/config.json"
CONF="${XDG_CONFIG_HOME:-$HOME/.config}"
UNITDIR="$CONF/systemd/user"
UNIT="${AIWB_BRIDGE_UNIT:-aiworkbench-bridge.service}"
KUNIT="aiwb-keyring.service"
UNLOCK="$CONF/aiwb-keyring/unlock"

BINARY="" BUILD=0 CONFIG_FROM="" HOST_ID="" CHANNEL="" TRUST_KEY="" DEPS=0 KEYRING=auto LINGER=1 START=1
PY="" UNVERIFIED=0 UNINSTALL=0

say() { printf '%s\n' "$*"; }
die() { printf 'install.sh: %s\n' "$*" >&2; exit 1; }

while [ $# -gt 0 ]; do
    case "$1" in
        --binary) BINARY="${2:?--binary FILE}"; shift ;;
        --allow-unverified) UNVERIFIED=1 ;;
        --build) BUILD=1 ;;
        --config-from) CONFIG_FROM="${2:?--config-from FILE}"; shift ;;
        --host-id) HOST_ID="${2:?--host-id ID}"; shift ;;
        --channel) CHANNEL="${2:?--channel DIR}"; shift ;;
        --release-key) TRUST_KEY="${2:?--release-key BASE64}"; shift ;;
        --deps) DEPS=1 ;;
        --keyring) KEYRING="${2:?--keyring auto|session|headless}"; shift ;;
        --no-linger) LINGER=0 ;;
        --no-start) START=0 ;;
        --python) PY="${2:?--python PY}"; shift ;;
        --uninstall) UNINSTALL=1 ;;
        -h|--help) sed -n '3,36p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) die "unknown argument $1 (--help)" ;;
    esac
    shift
done

[ "$(uname -s)" = Linux ] || die "this installer is for Linux (Windows: bridge/install.ps1)"
[ "$(id -u)" != 0 ] || die "run it as the account Bridge runs under, not as root (see --help: dedicated account)"
case "$(uname -m)" in
    x86_64|amd64) RID=linux-x64 ;;
    aarch64|arm64) RID=linux-arm64 ;;
    *) die "unsupported CPU $(uname -m) (x86_64 and aarch64 are supported)" ;;
esac
case "$KEYRING" in auto|session|headless) ;; *) die "--keyring auto|session|headless" ;; esac

as_root() {
    if [ "$(id -u)" = 0 ]; then "$@"
    elif command -v sudo >/dev/null 2>&1; then sudo "$@"
    else die "needs root for: $* (no sudo)"; fi
}

# ---------------------------------------------------------------- systemd user manager and its D-Bus

linger_on() { [ "$(loginctl show-user "$(id -un)" -p Linger --value 2>/dev/null || true)" = yes ]; }

user_manager() {
    export XDG_RUNTIME_DIR="/run/user/$(id -u)"   # never an inherited one (su without -, sudo): another user's
    if [ "$LINGER" = 1 ] && ! linger_on; then
        loginctl enable-linger "$(id -un)" 2>/dev/null || as_root loginctl enable-linger "$(id -un)" \
            || die "loginctl enable-linger failed (Bridge would stop at logout); --no-linger to skip"
        say "linger on: the user services start at boot without a login"
    fi
    for _ in $(seq 1 30); do
        [ -S "$XDG_RUNTIME_DIR/bus" ] && systemctl --user show-environment >/dev/null 2>&1 && break
        sleep 1
    done
    systemctl --user show-environment >/dev/null 2>&1 \
        || die "no systemd user manager for $(id -un) (log in once over ssh, or enable linger; inside 'sudo -u' use 'sudo -iu' or machinectl shell)"
    [ -S "$XDG_RUNTIME_DIR/bus" ] || die "no D-Bus user session at $XDG_RUNTIME_DIR/bus (Debian/Ubuntu: dbus-user-session; --deps)"
    export DBUS_SESSION_BUS_ADDRESS="unix:path=$XDG_RUNTIME_DIR/bus"
}

# ---------------------------------------------------------------- uninstall

if [ "$UNINSTALL" = 1 ]; then
    user_manager
    systemctl --user disable --now "$UNIT" 2>/dev/null || true
    rm -f "$UNITDIR/$UNIT"
    if [ -f "$UNITDIR/$KUNIT" ]; then
        systemctl --user disable --now "$KUNIT" 2>/dev/null || true
        rm -f "$UNITDIR/$KUNIT" "$DATA/dbus-1/services/org.freedesktop.secrets.service"
        systemctl --user unmask gnome-keyring-daemon.socket gnome-keyring-daemon.service >/dev/null 2>&1 || true
    fi
    systemctl --user daemon-reload
    say "removed: $UNIT${KUNIT:+ and $KUNIT}. Kept: $APP, $STATE (config, logs, channel), the keyring and $UNLOCK."
    exit 0
fi

# ---------------------------------------------------------------- 1. packages

if [ "$DEPS" = 1 ]; then
    . /etc/os-release
    case " ${ID:-} ${ID_LIKE:-} " in
        *" debian "*|*" ubuntu "*)
            as_root apt-get update -q
            icu="$(apt-cache pkgnames libicu 2>/dev/null | grep -E '^libicu[0-9]+$' | sort -V | tail -1)"
            as_root env DEBIAN_FRONTEND=noninteractive apt-get install -y -q --no-install-recommends ca-certificates git \
                python3 python3-keyring python3-secretstorage python3-cryptography python3-paramiko libsecret-tools \
                gnome-keyring dbus-user-session ${icu:-libicu-dev} ;;
        *" fedora "*)
            as_root dnf install -y -q git python3 python3-keyring python3-secretstorage python3-cryptography \
                python3-paramiko libsecret gnome-keyring libicu ;;
        *) die "--deps knows apt-get (Debian/Ubuntu) and dnf (Fedora); install by hand (RHEL: EPEL first): git python3 python3-keyring libsecret gnome-keyring ICU" ;;
    esac
fi

[ -n "$PY" ] || PY="$(command -v python3 || true)"
[ -n "$PY" ] || die "python3 is needed (--deps)"
command -v secret-tool >/dev/null 2>&1 || die "secret-tool is missing (Debian/Ubuntu: libsecret-tools, Fedora: libsecret; --deps)"

user_manager

# ---------------------------------------------------------------- 2. secrets

probe_keyring() {
    local v; v="probe-$$-$RANDOM"
    printf '%s' "$v" | timeout 20 secret-tool store --label="AI Workbench probe" service aiwb/setup-probe username default \
        >/dev/null 2>&1 || return 1
    [ "$(timeout 20 secret-tool lookup service aiwb/setup-probe 2>/dev/null)" = "$v" ] || return 1
    timeout 20 secret-tool clear service aiwb/setup-probe >/dev/null 2>&1 || true
}

headless_keyring() {
    local gkd; gkd="$(command -v gnome-keyring-daemon || true)"
    [ -n "$gkd" ] || die "gnome-keyring is missing (--deps): a server without a desktop keeps secrets in it"
    if [ ! -s "$UNLOCK" ] && [ -e "$DATA/keyrings/login.keyring" ]; then
        die "$DATA/keyrings/login.keyring exists with another password (a desktop keyring?): move it away, or put its
    password into $UNLOCK (0600) yourself, or use --keyring session"
    fi
    mkdir -p "$(dirname "$UNLOCK")" "$UNITDIR" "$DATA/dbus-1/services"
    chmod 700 "$(dirname "$UNLOCK")"
    if [ ! -s "$UNLOCK" ]; then
        (umask 077; head -c 32 /dev/urandom | base64 | tr -d '\n' > "$UNLOCK")
    fi
    chmod 600 "$UNLOCK"
    local before=""
    [ -f "$UNITDIR/$KUNIT" ] && before="$(cat "$UNITDIR/$KUNIT")"
    cat > "$UNITDIR/$KUNIT" <<EOF
# AI Workbench (bridge/install.sh): Secret Service on a computer without a desktop session.
[Unit]
Description=Secret Service for AI Workbench (gnome-keyring, unlocked at start)

[Service]
Type=dbus
BusName=org.freedesktop.secrets
ExecStart=$gkd --replace --foreground --unlock --components=secrets
StandardInput=file:$UNLOCK
Restart=on-failure
RestartSec=5

[Install]
WantedBy=default.target
EOF
    # A request for the Secret Service starts this unit, never a locked keyring without the unlock password.
    cat > "$DATA/dbus-1/services/org.freedesktop.secrets.service" <<EOF
[D-BUS Service]
Name=org.freedesktop.secrets
Exec=/bin/false
SystemdService=$KUNIT
EOF
    # The distribution's own keyring units (Ubuntu 24.04: gnome-keyring-daemon.socket/.service, meant for a desktop
    # session) would take the Secret Service over at boot with a locked keyring: off for this account.
    systemctl --user stop gnome-keyring-daemon.socket gnome-keyring-daemon.service >/dev/null 2>&1 || true
    systemctl --user mask gnome-keyring-daemon.socket gnome-keyring-daemon.service >/dev/null 2>&1 || true
    systemctl --user daemon-reload
    systemctl --user enable "$KUNIT" >/dev/null 2>&1
    if [ "$before" != "$(cat "$UNITDIR/$KUNIT")" ] || ! systemctl --user is-active --quiet "$KUNIT"; then
        systemctl --user restart "$KUNIT"   # not on a plain re-run: Bridge reads secrets from it while jobs run
    fi
    HEADLESS=1
}

HEADLESS=0
if [ -f "$UNITDIR/$KUNIT" ] && [ "$KEYRING" != session ]; then
    headless_keyring   # an earlier install: keep it current
elif [ "$KEYRING" = auto ] && ! probe_keyring && systemctl is-enabled --quiet display-manager.service 2>/dev/null; then
    # (not graphical.target as default: Ubuntu Server has it too; a display manager means desktop logins)
    die "no usable Secret Service, and this computer has a desktop: log in to the desktop once (its keyring), then run
    this again; or --keyring headless (a keyring of its own for this account, unlocked at boot, instead of the desktop's)"
elif [ "$KEYRING" = headless ] || { [ "$KEYRING" = auto ] && ! probe_keyring; }; then
    say "no usable Secret Service in this session: gnome-keyring as a user service ($KUNIT)"
    headless_keyring
fi
probe_keyring || die "the keyring does not keep secrets (secret-tool store/lookup failed); see journalctl --user -u $KUNIT"
say "keyring: $([ "$HEADLESS" = 1 ] && echo "gnome-keyring user service, unlocked from $UNLOCK" || echo "Secret Service of the session") - ok"

# ---------------------------------------------------------------- 3. config.json

mkdir -p "$STATE"
chmod 700 "$STATE"
if [ -n "$CONFIG_FROM" ]; then
    [ -f "$CONFIG_FROM" ] || die "no $CONFIG_FROM"
    if [ ! -f "$CFG" ]; then cp "$CONFIG_FROM" "$CFG"; say "config.json copied from $CONFIG_FROM"
    else say "config.json kept ($CFG exists; --config-from not used)"; fi
elif [ ! -f "$CFG" ]; then
    "$PY" "$SRC/install/aiwb_install.py" bridge-config "$CFG" --posix \
        || die "config.json from install.json failed (python3 install/freedom_setup.py init first)"
fi
# hostId (--host-id) and the state folder spelled out: the launcher (drain.request) and Bridge must use the same one,
# whatever XDG_DATA_HOME the shell or the service manager has
"$PY" - "$CFG" "$HOST_ID" "$STATE" <<'EOF'
import json, re, sys
p, host, state = sys.argv[1], sys.argv[2], sys.argv[3]
if host and not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_-]{0,39}', host):
    sys.exit('--host-id: 1-40 letters, digits, _ or -')
cfg = json.load(open(p, encoding='utf-8-sig'))
new = dict(cfg, localStateRoot=state, **({'hostId': host} if host else {}))
if new != cfg:
    open(p, 'w', encoding='utf-8').write(json.dumps(new, ensure_ascii=False, indent=2) + '\n')
    if host and cfg.get('hostId') != host:
        print('hostId =', host)
EOF
chmod 600 "$CFG"
ROOT="$("$PY" -c 'import json,sys; print(json.load(open(sys.argv[1], encoding="utf-8-sig")).get("driveRoot", ""))' "$CFG")"
case "$ROOT" in /*) mkdir -p "$ROOT" ;; esac

# ---------------------------------------------------------------- 4. the build

verify_release() {   # $1: a release file; the manifest of the Freedom maintainers must sit next to it
    "$PY" - "$SRC" "$1" <<'EOF'
import pathlib, sys
sys.path.insert(0, str(pathlib.Path(sys.argv[1]) / 'install'))
import freedom_setup
why = freedom_setup.release_check(pathlib.Path(sys.argv[2]))
print(why or 'ok')
sys.exit(1 if why else 0)
EOF
}

COMMIT=src
if [ -z "$BINARY" ] && [ "$BUILD" = 0 ]; then
    BINARY="$(ls -1 "$SRC"/../BridgeAgent-*-"$RID" 2>/dev/null | sort -V | tail -1 || true)"
    [ -z "$BINARY" ] || say "release build found: $BINARY"
fi
if [ -n "$BINARY" ]; then
    [ -f "$BINARY" ] || die "no $BINARY"
    if [ -f "$(dirname "$BINARY")/freedom-manifest.json" ]; then
        # the checked copy is the one that runs (the downloaded file may change after the check)
        CHECKED="$(mktemp -d)"
        trap 'rm -rf "$CHECKED"' EXIT
        cp "$BINARY" "$(dirname "$BINARY")/freedom-manifest.json" "$CHECKED/"
        BINARY="$CHECKED/$(basename "$BINARY")"
        WHY="$(verify_release "$BINARY")" || die "$BINARY not verified: $WHY"
        say "$(basename "$BINARY"): signed by the Freedom maintainers, hash matches"
        COMMIT=release
    elif [ "$UNVERIFIED" = 1 ]; then
        say "warning: $BINARY is not checked against a release manifest (--allow-unverified)"
        COMMIT=local
    else
        die "no freedom-manifest.json next to $BINARY (download it from the same release page, or --allow-unverified for a build of your own)"
    fi
    chmod u+x "$BINARY"
    BIN="$BINARY"
else
    DOTNET="$(command -v dotnet || true)"
    [ -z "$DOTNET" ] && [ -x "$HOME/.dotnet/dotnet" ] && DOTNET="$HOME/.dotnet/dotnet"
    if [ -z "$DOTNET" ] || ! "$DOTNET" --list-sdks 2>/dev/null | grep -Eq '^([89]|[1-9][0-9])\.'; then
        die "no Bridge build and no .NET SDK 8+ to make one. Either download BridgeAgent-<version>-$RID and
    freedom-manifest.json from the release page next to this folder, or install the SDK
    (Ubuntu: sudo apt install dotnet-sdk-8.0; Fedora: sudo dnf install dotnet-sdk-8.0; other: https://dot.net/install)"
    fi
    OUT="$HOME/.cache/aiwb-build/bridge-publish/$RID"
    rm -rf "$OUT"
    say "building Bridge from $SRC/bridge ($RID, self-contained single file)..."
    DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 "$DOTNET" publish "$SRC/bridge/net8/Bridge.Net8.csproj" -c Release \
        -f net8.0 -r "$RID" --self-contained true -p:PublishSingleFile=true -p:DebugType=none -o "$OUT" -v quiet -nologo \
        || die "dotnet publish failed"
    BIN="$OUT/BridgeAgent"
    if [ -d "$SRC/.git" ] && command -v git >/dev/null 2>&1; then
        COMMIT="$(git -C "$SRC" rev-parse --short HEAD 2>/dev/null || echo src)"
        [ -z "$(git -C "$SRC" status --porcelain -- bridge 2>/dev/null)" ] || COMMIT="$COMMIT-dirty"
    fi
fi
VER="$("$BIN" --version)" || die "$BIN does not run (missing ICU? --deps)"
[[ "$VER" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "$BIN --version printed '$VER'"
"$BIN" --validate-config --config "$CFG" >/dev/null 2>&1 \
    || die "Bridge $VER rejects $CFG: $(cat "$STATE/config-check.txt" 2>/dev/null)"
say "Bridge $VER ($RID) accepts $CFG"

# ---------------------------------------------------------------- 5. release key and channel

LJ="$APP/launcher.json"
if [ -z "$CHANNEL" ] && [ -f "$LJ" ]; then
    CHANNEL="$("$PY" -c 'import json,sys; print(json.load(open(sys.argv[1])).get("updatesDir") or "")' "$LJ")"
fi
CHANNEL="${CHANNEL:-$STATE/updates}"
mkdir -p "$CHANNEL"
OLD=""
[ -f "$LJ" ] && OLD="$("$PY" -c 'import json,sys; print(json.load(open(sys.argv[1])).get("releaseKey") or "")' "$LJ")"
OWN="$("$BIN" --release-key 2>/dev/null || true)"   # this computer's key, if it has one
if [ -n "$TRUST_KEY" ]; then
    [ -z "$OLD" ] || [ "$OLD" = "$TRUST_KEY" ] \
        || die "$LJ trusts another release key; not replaced (a rehearsal needs its own AIWB_BRIDGE_APP)"
    PUB="$TRUST_KEY"
    say "trusting the release key given (--release-key): releases come from its channel $CHANNEL"
elif [ -n "$OLD" ] && [ "$OLD" != "$OWN" ]; then
    # installed with --release-key: this computer never signs into that channel (its launchers would reject the build)
    PUB="$OLD"
    say "this computer trusts another computer's release key: nothing published; new versions come from $CHANNEL"
else
    PUB="$("$BIN" --release-key --create)" || die "release key: $PUB"
    SIG="$CHANNEL/BridgeAgent-$VER-$RID.sig.json"
    if [ ! -f "$SIG" ]; then
        "$BIN" --release-publish --channel "$CHANNEL" --file "$BIN" --version "$VER" --commit "$COMMIT" --rid "$RID" \
            || die "publishing to $CHANNEL failed"
    elif grep -q "\"sha256\":\"$(sha256sum "$BIN" | cut -d' ' -f1)\"" "$SIG"; then
        say "Bridge $VER ($RID) is already in the channel"
    else
        say "warning: the channel holds another build of Bridge $VER ($SIG); it stays and keeps running."
        say "         To run this build, raise BridgeVersion (bridge/BridgeAgent.cs) or take a newer release."
    fi
fi

# ---------------------------------------------------------------- 6. launcher

mkdir -p "$APP"
install -m 700 "$BIN" "$APP/BridgeAgent.new"
mv -f "$APP/BridgeAgent.new" "$APP/BridgeAgent"   # rename: a running launcher keeps its old file
"$PY" - "$LJ" "$CHANNEL" "$PUB" "$STATE" "$CFG" "$RID" <<'EOF'
import json, sys
p, ch, key, st, cfg, rid = sys.argv[1:]
open(p, 'w', encoding='utf-8').write(json.dumps({'updatesDir': ch, 'releaseKey': key, 'stateRoot': st,
                                                  'configPath': cfg, 'rid': rid}, indent=2) + '\n')
EOF
RUNNING=0
systemctl --user is-active --quiet "$UNIT" 2>/dev/null && RUNNING=1
if [ "$RUNNING" = 0 ]; then
    "$APP/BridgeAgent" --launcher --root "$APP" --check-once || die "launcher check failed (see $STATE/logs/launcher.log)"
    grep -q '"current":"[0-9]' "$APP/state.json" 2>/dev/null \
        || die "the launcher staged no Bridge release from $CHANNEL (see $STATE/logs/launcher.log)"
fi

# ---------------------------------------------------------------- 7. service

UPATH="$HOME/.local/bin:$HOME/.npm-global/bin:$HOME/.bun/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:/snap/bin"
mkdir -p "$UNITDIR"
{
    echo "# AI Workbench (bridge/install.sh): the Bridge launcher of this account."
    echo "[Unit]"
    echo "Description=AI Workbench Bridge"
    echo "After=network-online.target"
    if [ "$HEADLESS" = 1 ]; then echo "Wants=$KUNIT"; echo "After=$KUNIT"; fi
    echo
    echo "[Service]"
    echo "ExecStart=\"$APP/BridgeAgent\" --launcher --root \"$APP\""
    echo "Environment=\"PATH=$UPATH\""
    echo "Restart=always"
    echo "RestartSec=30"
    echo "TimeoutStopSec=30"
    echo
    echo "[Install]"
    echo "WantedBy=default.target"
} > "$UNITDIR/$UNIT"
systemctl --user daemon-reload
systemctl --user enable "$UNIT" >/dev/null 2>&1
say "service $UNIT installed ($UNITDIR)"

for cli in claude codex; do
    found="$(PATH="$UPATH" command -v "$cli" || true)"
    say "agent CLI $cli: ${found:-not found on the service PATH (install it and sign in yourself: python3 install/freedom_setup.py subs)}"
done

if [ "$START" = 0 ]; then
    say "not started (--no-start): systemctl --user start $UNIT"
    exit 0
fi
if [ "$RUNNING" = 1 ]; then
    if grep -q "\"current\":\"$VER\"" "$APP/state.json" 2>/dev/null; then
        say "Bridge $VER runs already ($UNIT active)"
    else
        say "the running launcher takes the newest signed version of $CHANNEL within 10 min (after the running jobs)"
    fi
    say "the launcher file was replaced; it runs after: systemctl --user restart $UNIT (stops running jobs)"
    exit 0
fi
LOG="$STATE/logs/$(date -u +%Y-%m-%d).log"
SIZE="$(stat -c %s "$LOG" 2>/dev/null || echo 0)"
systemctl --user start "$UNIT"
CUR="$(sed -n 's/.*"current":"\([0-9.]*\)".*/\1/p' "$APP/state.json")"
for _ in $(seq 1 40); do
    sleep 3
    [ "$LOG" = "$STATE/logs/$(date -u +%Y-%m-%d).log" ] || { LOG="$STATE/logs/$(date -u +%Y-%m-%d).log"; SIZE=0; }
    NEW="$(tail -c +"$((SIZE + 1))" "$LOG" 2>/dev/null | grep -F "v$CUR started" | tail -1 || true)"
    if [ -n "$NEW" ]; then
        say "Bridge $CUR started (hostId=${NEW##*hostId=})"
        exit 0
    fi
done
die "Bridge start not confirmed in 120 s: journalctl --user -u $UNIT, $STATE/logs (launcher.log and the newest day log)"
