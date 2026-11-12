#!/bin/bash
# SPDX-License-Identifier: AGPL-3.0-or-later
# CO Resident @@VERSION@@ for macOS — installer served by mac.php (one-time link from the CO offer).
# Installs into ~/Applications/CO Resident without administrator rights, signs the binary locally (ad-hoc,
# Apple Silicon runs only signed code), registers a LaunchAgent and opens the Google sign-in.
# An existing installation is replaced only after the new copy is unpacked, signed and starts; if setup fails,
# the previous copy is put back and started again.
set -euo pipefail

say() { printf '\033[1m%s\033[0m\n' "$*"; }
fail() { printf '\033[31merror:\033[0m %s\n' "$*" >&2; exit 1; }

[ "$(uname -s)" = "Darwin" ] || fail "this installer is for macOS"
[ "$(uname -m)" = "arm64" ] || fail "CO Resident needs a Mac with Apple Silicon (M1 or newer)"
major="$(sw_vers -productVersion | cut -d. -f1)"
[ "${major:-0}" -ge 14 ] || fail "macOS 14 Sonoma or newer is needed (this Mac: $(sw_vers -productVersion))"

DIR="$HOME/Applications/CO Resident"
NEW="$DIR.new"
OLD="$DIR.old"
LABEL="org.freedom-workbench.co.resident"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP" "$NEW"' EXIT

say "Downloading CO Resident @@VERSION@@…"
curl -fsSL '@@BIN_URL@@' -o "$TMP/co.tgz" || fail "download failed (the link works once and for 30 minutes; get a new one in CO)"
echo "@@SHA256@@  $TMP/co.tgz" | shasum -a 256 -c - >/dev/null || fail "the download is damaged (checksum mismatch)"

# Stage and check the new copy next to the old one.
rm -rf "$NEW"
mkdir -p "$NEW"
tar -xzf "$TMP/co.tgz" -C "$NEW"
xattr -dr com.apple.quarantine "$NEW" 2>/dev/null || true
codesign --force --sign - "$NEW/co-resident" >/dev/null 2>&1 || fail "could not sign the program locally (codesign)"
chmod 755 "$NEW/co-resident"
"$NEW/co-resident" version >/dev/null || fail "the new program does not start on this Mac"

# Put the previous state back (used for any failure from here on): stop whatever runs, restore the old copy, start it.
had_old=0; moved_old=0; moved_new=0
restore() {
  echo "Installation did not finish; putting the previous version back." >&2
  launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true   # setup may have started the new agent
  for _ in 1 2 3 4 5; do launchctl print "gui/$(id -u)/$LABEL" >/dev/null 2>&1 || break; sleep 1; done
  [ "$moved_new" = 1 ] && rm -rf "$DIR"
  [ "$moved_old" = 1 ] && mv "$OLD" "$DIR"
  if [ "$had_old" = 1 ]; then
    if [ -f "$PLIST" ] && launchctl bootstrap "gui/$(id -u)" "$PLIST" 2>/dev/null && launchctl print "gui/$(id -u)/$LABEL" >/dev/null 2>&1; then
      echo "The previous version runs again." >&2
    else
      echo "The previous version is in $DIR but did not start; log out and in, or run the install command again." >&2
    fi
  else
    rm -f "$PLIST"
  fi
  exit 1
}

# Swap: stop the old one, keep it as .old until setup succeeds.
if [ -d "$DIR" ]; then
  had_old=1
  launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
  rm -rf "$OLD"
  mv "$DIR" "$OLD" || restore
  moved_old=1
fi
mv "$NEW" "$DIR" || restore
moved_new=1

say "Installed in $DIR"
"$DIR/co-resident" setup --env '@@ENV@@' || restore
rm -rf "$OLD"
curl -fsS '@@DONE_URL@@' >/dev/null 2>&1 || true   # CO stops offering the install on this account
echo
say "Useful commands:"
echo "  \"$DIR/co-resident\" status        state, pairing code, folders"
echo "  \"$DIR/co-resident\" shell off     forbid commands on this Mac (shell on: allow again, each one is asked)"
echo "  \"$DIR/co-resident\" uninstall --purge   remove it"
