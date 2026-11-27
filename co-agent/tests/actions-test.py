# SPDX-License-Identifier: AGPL-3.0-or-later
"""Runs actions-test.php against a throwaway MariaDB on 127.0.0.1 (never production).

python co-agent/tests/actions-test.py [--php PHP] [--mariadb BIN_DIR] [--test dossier-test.php]
Starts mariadbd from the aiwb-backup-check install (%LOCALAPPDATA%\\AIWorkbench\\mariadb), applies db/schema.sql, lays
out a sandbox where co_agent_home()/co-agent/secrets/db.json points at it, runs the PHP test, stops and deletes the server.
"""
import argparse, json, os, shutil, socket, subprocess, sys, tempfile, time
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
LOCAL = Path(os.environ.get("LOCALAPPDATA", ""))


def free_port():
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    p = s.getsockname()[1]
    s.close()
    return p


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--php", default=str(LOCAL / "AIWorkbenchBuild" / "php" / "php.exe"))
    ap.add_argument("--mariadb", default=None)
    ap.add_argument("--test", default="actions-test.php")  # another PHP test of tests/ on the same sandbox
    a = ap.parse_args()
    bin_dir = Path(a.mariadb) if a.mariadb else next((LOCAL / "AIWorkbench" / "mariadb").glob("mariadb-*-winx64/bin"))
    work = Path(tempfile.mkdtemp(prefix="co-actions-test-"))
    port = free_port()
    client = [str(bin_dir / "mariadb.exe"), "--no-defaults", "-h", "127.0.0.1", "-P", str(port), "-u", "root", "--protocol=TCP"]
    proc = None
    try:
        subprocess.run([str(bin_dir / "mariadb-install-db.exe"), f"--datadir={work / 'data'}"], check=True, capture_output=True)
        proc = subprocess.Popen([str(bin_dir / "mariadbd.exe"), "--no-defaults", f"--datadir={work / 'data'}", f"--port={port}",
                                 "--bind-address=127.0.0.1", "--skip-name-resolve", "--character-set-server=utf8mb4"],
                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        for _ in range(120):
            if subprocess.run(client + ["-e", "SELECT 1"], capture_output=True).returncode == 0:
                break
            time.sleep(0.5)
        subprocess.run(client + ["-e", "CREATE DATABASE co CHARACTER SET utf8mb4; CREATE USER t@'127.0.0.1' IDENTIFIED BY 't'; "
                                 "GRANT ALL ON co.* TO t@'127.0.0.1';"], check=True)
        with open(REPO / "co-agent" / "db" / "schema.sql", "rb") as f:
            subprocess.run(client + ["--default-character-set=utf8mb4", "co"], stdin=f, check=True)
        # schema 21: the people of the test workbench (members-lib.php reads them for e-mails, names, the owner)
        subprocess.run(client + ["--default-character-set=utf8mb4", "co", "-e",
                                 "INSERT INTO members (env, email, name, role, site_agent, sort, updated_utc) VALUES "
                                 "('owner', 'owner@example.org', 'Олег', 'owner', 1, 0, UTC_TIMESTAMP(3)), "
                                 "('sam', 'sam@example.org', 'Сэм', 'member', 1, 1, UTC_TIMESTAMP(3)), "
                                 "('alex', 'alex@example.org', 'Алекс', 'member', 0, 2, UTC_TIMESTAMP(3))"], check=True)
        # sandbox/d1/d2/d3/agent -> co_agent_home() = sandbox (dirname(__DIR__, 4) of web/agent's lib.php)
        box = work / "box"
        agent = box / "d1" / "d2" / "web" / "agent"
        shutil.copytree(REPO / "co-agent" / "web" / "agent", agent)
        shutil.copytree(REPO / "co-web-auth" / "auth", agent.parent / "auth")
        (box / "d1" / "d2" / "tests").mkdir(parents=True)
        shutil.copy(REPO / "co-agent" / "tests" / a.test, box / "d1" / "d2" / "tests")
        shutil.copytree(REPO / "co-agent" / "db", box / "d1" / "d2" / "db")  # answer-test.php reads schema.sql
        shutil.copytree(REPO / "co-agent" / "cron", box / "d1" / "d2" / "cron")  # msgedit-test.php: protocol-apply.php
        shutil.copytree(REPO / "modules", box / "d1" / "d2" / "modules")  # F10: modules-test.php / gateway-test.php install them
        (box / "co-agent" / "secrets").mkdir(parents=True)
        (box / "co-agent" / "secrets" / "db.json").write_text(json.dumps({"host": "127.0.0.1", "db": "co", "user": "t", "password": "t"}))
        r = subprocess.run([a.php, "-d", f"extension_dir={Path(a.php).parent / 'ext'}", "-d", "extension=mysqli", "-d", "extension=mbstring", "-d", "extension=openssl", "-d", f"mysqli.default_port={port}",
                            str(box / "d1" / "d2" / "tests" / a.test)])
        return r.returncode
    finally:
        if proc:
            subprocess.run([str(bin_dir / "mariadb-admin.exe"), "--no-defaults", "-h", "127.0.0.1", "-P", str(port), "-u", "root",
                            "--protocol=TCP", "shutdown"], capture_output=True)
            try:
                proc.wait(30)
            except subprocess.TimeoutExpired:
                proc.kill()
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
