#!/bin/sh
# SPDX-License-Identifier: AGPL-3.0-or-later
# Freedom B/C: the server tests of co-agent/tests inside the app container, against a THROWAWAY database of the db
# container (never the database of the install). The same sandbox as co-agent/tests/actions-test.py: per test a fresh
# database with db/schema.sql and the seeded members owner/sam/alex, and a box where co_agent_home() finds
# co-agent/secrets/db.json.
#   run-php-tests.sh WORK      WORK/repo = co-agent, co-web-auth, connectors, co-web/src/diagnose.cases.json of the tree;
#                              WORK/db.json = {"host","db","user","password"} of the throwaway database (made by
#                              freedom_docker.py verify --php-tests, dropped afterwards)
set -u
W=$1
R=$W/repo
DB=$(php -r '$j = json_decode(file_get_contents($argv[1]), true); echo $j["db"];' "$W/db.json")
php -r '$j = json_decode(file_get_contents($argv[1]), true); $e = fn($v) => "\"" . addcslashes((string)$v, "\\\"") . "\"";
  echo "[client]\nuser=" . $e($j["user"]) . "\npassword=" . $e($j["password"]) . "\nhost=" . $e($j["host"]) . "\n";' \
  "$W/db.json" > "$W/my.cnf"
chmod 600 "$W/my.cnf"
SQL="mariadb --defaults-extra-file=$W/my.cnf --default-character-set=utf8mb4"
pass=0
fail=0
failed=""
for t in "$R"/co-agent/tests/*.php; do
    name=$(basename "$t")
    case "$name" in
        home-test.php|failures-test.php)
            # no database: they run in the tree itself
            out=$(cd "$R/co-agent/tests" && php "$t" 2>&1); rc=$? ;;
        *)
            $SQL -e "DROP DATABASE IF EXISTS \`$DB\`; CREATE DATABASE \`$DB\` CHARACTER SET utf8mb4" \
              && $SQL "$DB" < "$R/co-agent/db/schema.sql" \
              && $SQL "$DB" -e "INSERT INTO members (env, email, name, role, site_agent, sort, updated_utc) VALUES
                   ('owner', 'owner@example.org', 'Олег', 'owner', 1, 0, UTC_TIMESTAMP(3)),
                   ('sam', 'sam@example.org', 'Сэм', 'member', 1, 1, UTC_TIMESTAMP(3)),
                   ('alex', 'alex@example.org', 'Алекс', 'member', 0, 2, UTC_TIMESTAMP(3))" \
              || { echo "FAIL $name: throwaway database"; fail=$((fail + 1)); failed="$failed $name"; continue; }
            box=$W/box-${name%.php}
            rm -rf "$box"
            mkdir -p "$box/d1/d2/web" "$box/d1/d2/tests" "$box/co-agent/secrets" "$box/co-agent/connectors/corporate" \
                     "$box/d1/co-web/src"
            cp -r "$R/co-agent/web/agent" "$box/d1/d2/web/agent"
            cp -r "$R/co-web-auth/auth" "$box/d1/d2/web/auth"
            cp "$t" "$box/d1/d2/tests/"
            [ -f "$R/connectors/corporate/manifest.json" ] && cp "$R/connectors/corporate/manifest.json" "$box/co-agent/connectors/corporate/"
            cp "$W/db.json" "$box/co-agent/secrets/db.json"
            chmod 600 "$box/co-agent/secrets/db.json"
            out=$(cd "$box/d1/d2/tests" && php "$box/d1/d2/tests/$name" 2>&1); rc=$?
            rm -rf "$box" ;;
    esac
    oks=$(printf '%s\n' "$out" | grep -c '^ok' || true)
    bad=$(printf '%s\n' "$out" | grep -c '^FAIL' || true)
    if [ "$rc" -eq 0 ] && [ "$bad" -eq 0 ]; then
        pass=$((pass + 1)); echo "ok   $name ($oks checks)"
    else
        fail=$((fail + 1)); failed="$failed $name"
        echo "FAIL $name rc=$rc ($oks ok, $bad failed)"
        printf '%s\n' "$out" | grep -v '^ok' | tail -15 | sed 's/^/     /'
    fi
done
for t in "$R"/co-agent/tests/*.mjs; do
    [ -f "$t" ] || continue
    name=$(basename "$t")
    out=$(cd "$R/co-agent/tests" && timeout 120 node "$t" 2>&1); rc=$?
    if [ "$rc" -eq 0 ]; then pass=$((pass + 1)); echo "ok   $name"; else
        fail=$((fail + 1)); failed="$failed $name"; echo "FAIL $name rc=$rc"; printf '%s\n' "$out" | tail -10 | sed 's/^/     /'; fi
done
$SQL -e "DROP DATABASE IF EXISTS \`$DB\`" >/dev/null 2>&1
rm -f "$W/my.cnf"
echo "php-tests: $pass passed, $fail failed${failed:+ (}${failed# }${failed:+)}"
[ "$fail" -eq 0 ]
