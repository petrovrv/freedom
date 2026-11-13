"""Children's WSL2 sandbox aiwb-family: create / status / add-user / reset-user / del-user / check / destroy."""
import argparse
import base64
import io
import json
import os
import re
import subprocess
import sys
import tarfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
FILES = HERE / "files"
NOWIN = 0x08000000  # CREATE_NO_WINDOW
DISTRO = "aiwb-family"
LOCATION = r"C:\AIWorkbench\wsl\aiwb-family"
def _core_dirs():
    r"""Core tree with install/aiwb_install.py: AIWB_FREEDOM / AIWB_SOURCE, devtree.json "repo", C:/AIWorkbench/freedom."""
    dirs = [os.environ.get("AIWB_FREEDOM"), os.environ.get("AIWB_SOURCE")]
    local = os.environ.get("LOCALAPPDATA") or os.path.join(os.path.expanduser("~"), "AppData", "Local")
    try:
        with open(os.environ.get("AIWB_DEVTREE_CONFIG") or os.path.join(local, "AIWorkbench", "devtree.json"),
                  encoding="utf-8-sig") as f:
            dirs.append((json.load(f) or {}).get("repo"))
    except (OSError, ValueError, AttributeError):
        pass
    return dirs + [r"C:\AIWorkbench\freedom", os.path.expanduser("~/AIWorkbench/freedom")]


def default_users():
    """Household members of this install (install.json members without role owner) as name:uid, uid 3001.. in order.
    AIWB_SANDBOX_USERS="name:uid ..." overrides; [] when neither is known (then pass --users)."""
    env = os.environ.get("AIWB_SANDBOX_USERS", "").split()
    if env:
        return env
    for d in _core_dirs():
        if d and os.path.isfile(os.path.join(d, "install", "aiwb_install.py")):
            sys.path.insert(0, os.path.join(d, "install"))
            try:
                import aiwb_install
                members = aiwb_install.load().get("members") or []
            except Exception:  # noqa: BLE001 - no install.json: no defaults
                return []
            names = [m.get("env") for m in members if m.get("env") and m.get("role") != "owner"]
            return [f"{n}:{3001 + i}" for i, n in enumerate(names)]
    return []


DEFAULT_USERS = default_users()
PACKAGES = ("build-essential gcc g++ gdb cmake make python3 python3-venv python3-pip python3-dev git ripgrep "
            "dotnet-sdk-8.0 openjdk-21-jdk-headless rustc cargo nftables iptables iproute2 curl ca-certificates "
            "xz-utils procps psmisc less nano vim-tiny unzip zip file strace")
NAME_RE = re.compile(r"^[a-z][a-z0-9_-]{1,30}$")


def wsl(args, input_bytes=None, timeout=600):
    r = subprocess.run(["wsl.exe"] + args, input=input_bytes, capture_output=True, timeout=timeout,
                       creationflags=NOWIN, env={**os.environ, "WSL_UTF8": "1"}, cwd="C:\\")
    return r.returncode, r.stdout.decode("utf-8", "replace"), r.stderr.decode("utf-8", "replace")


def root(distro, script, timeout=1800):
    """Run a bash script as root inside the distro (script via stdin, never on a command line)."""
    return wsl(["-d", distro, "-u", "root", "--cd", "/", "-e", "bash", "-s"], script.encode(), timeout)


def as_user(distro, user, cmd, timeout=600):
    """The interface for Bridge: wsl.exe -d aiwb-family -u <user> --cd ~ -- bash -lc "<cmd>"."""
    b64 = base64.b64encode(cmd.encode()).decode()
    return wsl(["-d", distro, "-u", user, "--cd", "~", "--", "bash", "-lc", f"echo {b64} | base64 -d | bash"],
               timeout=timeout)


def distros():
    rc, out, _ = wsl(["-l", "-v"], timeout=60)
    res = {}
    for line in out.replace("\x00", "").splitlines()[1:]:
        parts = line.replace("*", " ").split()
        if len(parts) >= 3:
            res[parts[0]] = parts[1]
    return res


def die(msg, code=1):
    print(json.dumps({"ok": False, "error": msg}, ensure_ascii=False))
    sys.exit(code)


def check_rc(step, rc, out, err):
    if rc:
        die(f"{step} failed rc={rc}: {(out + err)[-3000:]}")


def files_tar():
    """Files installed into the distro (path in distro -> (local file, mode))."""
    m = {
        "etc/wsl.conf": ("wsl.conf", 0o644),
        "usr/local/sbin/aiwb-family-boot": ("aiwb-family-boot", 0o700),
        "usr/local/sbin/aiwb-family-watchdog": ("aiwb-family-watchdog", 0o700),
        "usr/local/src/aiwb-enter.c": ("aiwb-enter.c", 0o644),
        "etc/profile.d/00-aiwb-sandbox.sh": ("00-aiwb-sandbox.sh", 0o644),
        "etc/profile.d/10-aiwb-env.sh": ("10-aiwb-env.sh", 0o644),
    }
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w") as t:
        for dst, (src, mode) in m.items():
            data = (FILES / src).read_bytes().replace(b"\r\n", b"\n")
            ti = tarfile.TarInfo(dst)
            ti.size, ti.mode, ti.uid, ti.gid = len(data), mode, 0, 0
            t.addfile(ti, io.BytesIO(data))
    return buf.getvalue()


INSTALL_FILES = r"""
set -e
mkdir -p /etc/aiwb-family /usr/local/libexec
[ -f /etc/aiwb-family/sandbox.conf ] || cat > /etc/aiwb-family/sandbox.conf <<'EOF'
__SANDBOX_CONF__
EOF
touch /etc/aiwb-family/users; chmod 644 /etc/aiwb-family/users /etc/aiwb-family/sandbox.conf
getent group aiwbfam >/dev/null || groupadd -g 2998 aiwbfam
id aiwbsvc >/dev/null 2>&1 || useradd -u 2999 -M -d /nonexistent -s /usr/sbin/nologin -c "aiwb-family service" aiwbsvc
gcc -O2 -Wall -Werror -o /usr/local/libexec/aiwb-enter.new /usr/local/src/aiwb-enter.c
chown root:aiwbfam /usr/local/libexec/aiwb-enter.new && chmod 4750 /usr/local/libexec/aiwb-enter.new
mv -f /usr/local/libexec/aiwb-enter.new /usr/local/libexec/aiwb-enter
ln -sfn /usr/local/libexec/aiwb-enter /usr/local/bin/aiwb-shell
grep -qx /usr/local/bin/aiwb-shell /etc/shells || echo /usr/local/bin/aiwb-shell >> /etc/shells
chmod 755 /var/crash 2>/dev/null || true  # the only world-writable dir of the rootfs (disk outside the quota)
printf '127.0.0.1\tlocalhost\n127.0.1.1\taiwb-family\n::1\tlocalhost ip6-localhost ip6-loopback\n' > /etc/hosts
echo files-ok
"""

PROVISION = r"""
set -e
export DEBIAN_FRONTEND=noninteractive
[ -L /etc/resolv.conf ] && { rm -f /etc/resolv.conf; echo "nameserver 10.255.255.254" > /etc/resolv.conf; }
apt-get update -q >/var/log/aiwb-apt.log 2>&1
apt-get install -y -q --no-install-recommends __PACKAGES__ >>/var/log/aiwb-apt.log 2>&1
# Node.js: newest LTS from nodejs.org, sha256-checked, into /opt/node
ver=$(curl -fsSL https://nodejs.org/dist/index.json | python3 -c 'import json,sys; print(next(r["version"] for r in json.load(sys.stdin) if r["lts"]))')
if [ "$(/usr/local/bin/node -v 2>/dev/null)" != "$ver" ]; then
    f=node-$ver-linux-x64.tar.xz
    cd /tmp && curl -fsSLO https://nodejs.org/dist/$ver/$f && curl -fsSL https://nodejs.org/dist/$ver/SHASUMS256.txt | grep " $f\$" | sha256sum -c -
    rm -rf /opt/node && mkdir -p /opt/node && tar -xJf /tmp/$f -C /opt/node --strip-components=1 && rm -f /tmp/$f
    for b in node npm npx corepack; do ln -sf /opt/node/bin/$b /usr/local/bin/$b; done
fi
echo "provision-ok node=$(node -v) gcc=$(gcc -dumpversion) dotnet=$(dotnet --version) java=$(java -version 2>&1 | head -1)"
"""

ADD_USER = r"""
set -e
u=__U__; uid=__UID__; disk=__DISK__
cur=$(id -u $u 2>/dev/null || true)
if [ -n "$cur" ] && { [ "$cur" -lt 3001 ] || [ "$cur" -gt 3998 ]; }; then
    echo "aiwb-enter: $u is an existing account (uid $cur) of the distro, not a sandbox user" >&2; exit 3
fi
if [ -z "$cur" ]; then
    useradd -u $uid -U -M -d /home/$u -s /usr/local/bin/aiwb-shell -G aiwbfam -c "aiwb-family sandbox" $u
fi
usermod -s /usr/local/bin/aiwb-shell -G aiwbfam $u
passwd -l $u >/dev/null
for g in sudo adm admin wheel docker; do gpasswd -d $u $g >/dev/null 2>&1 || true; done
awk -v u=$u '$1!=u' /etc/aiwb-family/users > /etc/aiwb-family/users.new
echo "$u $(id -u $u) $disk" >> /etc/aiwb-family/users.new
mv -f /etc/aiwb-family/users.new /etc/aiwb-family/users; chmod 644 /etc/aiwb-family/users
/usr/local/sbin/aiwb-family-boot cgroups; /usr/local/sbin/aiwb-family-boot homes; /usr/local/sbin/aiwb-family-boot net
if [ ! -e /home/$u/.profile ]; then
    cp -rT /etc/skel /home/$u && chown -R $u:$u /home/$u
fi
__RUSTUP__
echo "user-ok $u uid=$(id -u $u) home=$(stat -c '%a %U' /home/$u) disk=$(df -h --output=size /home/$u | tail -1)"
"""

RUSTUP = r"""
if [ ! -x /home/$u/.cargo/bin/rustup ]; then
    curl -fsSL https://static.rust-lang.org/rustup/dist/x86_64-unknown-linux-gnu/rustup-init -o /tmp/rustup-init
    chmod 755 /tmp/rustup-init && mv /tmp/rustup-init /usr/local/libexec/rustup-init
fi
"""

DEL_USER = r"""
u=__U__
uid=$(id -u $u 2>/dev/null) || { echo "no user $u"; exit 0; }
pkill -9 -u $uid; sleep 1
umount -l /home/$u 2>/dev/null
__WIPE__
"""

STATUS = r"""
CG=/sys/fs/cgroup/aiwb-family
echo "== users"; cat /etc/aiwb-family/users
echo "== limits"; grep -v '^#' /etc/aiwb-family/sandbox.conf | sed 's/ *#.*//'
for d in $CG $CG/*/; do [ -f $d/memory.current ] || continue
  printf "%s cpu.max=%s mem=%sM/%s pids=%s/%s\n" "${d#/sys/fs/cgroup/}" "$(cat $d/cpu.max)" "$(( $(cat $d/memory.current) / 1048576 ))" "$(cat $d/memory.max)" "$(cat $d/pids.current)" "$(cat $d/pids.max)"
done
echo "== homes"; df -h --output=target,size,used,avail /home/* 2>/dev/null
echo "== netns"; ip netns list
echo "== firewall"; nft list table inet aiwb_family 2>/dev/null | grep -E 'counter|elements' | sed 's/^[[:space:]]*//'
echo "== watchdog"; pgrep -af aiwb-family-watchdog || echo "not running"
echo "== ready"; ls -l /run/aiwb/ready 2>/dev/null || echo "not booted"
echo "== mounts under /mnt"; awk '$5 ~ /^\/mnt\//{print $5}' /proc/self/mountinfo
echo "== disk"; df -h --output=size,used,avail /
echo "== log"; tail -5 /var/log/aiwb-family.log
"""

CHECK = r"""
set +e
cd ~; rm -rf ~/.aiwb-check; mkdir -p ~/.aiwb-check; cd ~/.aiwb-check
ok() { echo "PASS $1"; }; bad() { echo "FAIL $1"; }
printf '#include <stdio.h>\nint main(){puts("hello c");}\n' > h.c && gcc h.c -o hc && ./hc | grep -q 'hello c' && ok c || bad c
printf '#include <iostream>\nint main(){std::cout<<"hello cpp\\n";}\n' > h.cpp && g++ h.cpp -o hcpp && ./hcpp | grep -q 'hello cpp' && ok cpp || bad cpp
python3 -c 'print("hello py")' | grep -q 'hello py' && ok python || bad python
python3 -m venv v >/dev/null && ./v/bin/python -c 'print(1)' >/dev/null && ok python-venv || bad python-venv
node -e 'console.log("hello node")' | grep -q 'hello node' && ok node || bad node
mkdir -p dn && cd dn && dotnet new console -o . --force >/dev/null 2>&1 && dotnet run 2>&1 | grep -q 'Hello, World' && ok dotnet || bad dotnet; cd ..
printf 'public class H{public static void main(String[] a){System.out.println("hello java");}}\n' > H.java && javac H.java && java H | grep -q 'hello java' && ok java || bad java
printf 'fn main(){println!("hello rust");}\n' > h.rs && rustc -o hrs h.rs 2>/dev/null && ./hrs | grep -q 'hello rust' && ok rust || bad rust
# isolation: each of these must be refused
[ -z "$(ls -A /mnt 2>/dev/null)" ] && ok "ls /mnt empty" || bad "ls /mnt: $(ls /mnt | tr '\n' ' ')"
cat /etc/shadow >/dev/null 2>&1 && bad "cat /etc/shadow readable" || ok "cat /etc/shadow refused"
for o in $(awk '{print $1}' /etc/aiwb-family/users); do [ "$o" = "$(id -un)" ] && continue
  ls /home/$o >/dev/null 2>&1 && bad "ls /home/$o readable" || ok "ls /home/$o refused"; done
cmd.exe /c ver >/dev/null 2>&1 && bad "cmd.exe runs" || ok "cmd.exe refused"
powershell.exe -c 1 >/dev/null 2>&1 && bad "powershell.exe runs" || ok "powershell.exe refused"
[ ! -e /mnt/c/Windows ] && ok "no /mnt/c" || bad "/mnt/c visible"
[ -z "$(ls -A /mnt/wsl 2>/dev/null)" ] && ok "no /mnt/wsl" || bad "/mnt/wsl visible"
[ ! -e /tmp/.X11-unix/X0 ] && ok "no WSLg X socket" || bad "WSLg X socket visible"
ls /usr/lib/wsl/drivers 2>/dev/null | grep -q . && bad "Windows drivers visible" || ok "no /usr/lib/wsl/drivers"
sudo -n true >/dev/null 2>&1 && bad "sudo works" || ok "sudo refused"
unshare -U true 2>/dev/null && bad "user namespace allowed" || ok "user namespace refused"
[ "$(ps -e --no-headers | wc -l)" -lt 15 ] && ok "foreign processes hidden" || bad "sees $(ps -e --no-headers | wc -l) processes"
grep -q aiwb-throttle /proc/self/cgroup /sys/fs/cgroup/cgroup.procs 2>/dev/null; ls /sys/fs/cgroup | grep -q -e system.slice -e aiwb-throttle && bad "VM cgroup tree visible" || ok "own cgroup view only"
echo "INFO limits cpu.max=$(cat /sys/fs/cgroup/cpu.max) mem=$(cat /sys/fs/cgroup/memory.max) pids=$(cat /sys/fs/cgroup/pids.max)"
echo "INFO /tmp=$(df --output=target /tmp | tail -1) disk=$(df -h --output=size ~ | tail -1)"
# network: internet yes, LAN / tailnet / Windows host / other distros no
curl -s -o /dev/null -m 10 -w '%{http_code}' https://pypi.org/simple/pip/ | grep -q 200 && ok "internet (pypi)" || bad "internet (pypi)"
curl -s -o /dev/null -m 10 https://registry.npmjs.org/ && ok "internet (npm)" || bad "internet (npm)"
for t in __NETDENY__; do curl -s -o /dev/null -m 3 "http://$t/" && bad "reached $t" || ok "blocked $t"; done
cd ~; rm -rf ~/.aiwb-check
"""


def ensure_running(distro):
    rc, out, err = root(distro, "test -e /run/aiwb/ready || /usr/local/sbin/aiwb-family-boot boot; "
                                "for i in $(seq 30); do test -e /run/aiwb/ready && break; sleep 1; done; "
                                "test -e /run/aiwb/ready && echo ready", 120)
    return "ready" in out


def parse_users(items):
    res = []
    for it in items:
        name, _, uid = it.partition(":")
        if not NAME_RE.match(name):
            die(f"bad user name {name!r}")
        res.append((name, int(uid) if uid else 0))
    return res


def add_user(distro, name, uid, disk_gb, rustup=False):
    if not uid:
        _, out, _ = root(distro, "awk -F: '$3>=3001 && $3<3999{print $3}' /etc/passwd | sort -n | tail -1", 60)
        uid = int(out.strip() or 3000) + 1
    if uid < 1001:
        die("uid must be >= 1001")
    s = (ADD_USER.replace("__U__", name).replace("__UID__", str(uid)).replace("__DISK__", str(disk_gb))
         .replace("__RUSTUP__", RUSTUP if rustup else ""))
    rc, out, err = root(distro, s, 600)
    check_rc(f"add-user {name}", rc, out, err)
    if rustup:
        rc2, o2, e2 = as_user(distro, name, "/usr/local/libexec/rustup-init -y --profile minimal --no-modify-path "
                                            ">/dev/null 2>&1; ~/.cargo/bin/rustc --version", 1800)
        out += o2 + e2
    return out.strip()


def cmd_create(a):
    d = distros()
    if a.distro not in d:
        Path(a.location).parent.mkdir(parents=True, exist_ok=True)
        rc, out, err = wsl(["--install", "Ubuntu-24.04", "--name", a.distro, "--location", a.location,
                            "--vhd-size", a.vhd_size, "--no-launch", "--web-download"], timeout=1800)
        check_rc("wsl --install", rc, out, err)
    # first: switch systemd off before anything else (the cgroup tree of the WSL VM is shared)
    rc, out, err = wsl(["-d", a.distro, "-u", "root", "--cd", "/", "-e", "tar", "-xf", "-", "-C", "/"], files_tar(), 120)
    check_rc("copy files", rc, out, err)
    conf = (FILES / "sandbox.conf").read_text(encoding="utf-8").replace("\r\n", "\n").strip()
    rc, out2, err = root(a.distro, PROVISION.replace("__PACKAGES__", PACKAGES), 3600)
    check_rc("provision", rc, out2, err)
    rc, out, err = root(a.distro, INSTALL_FILES.replace("__SANDBOX_CONF__", conf), 300)
    check_rc("install files", rc, out, err)
    # restart only this distro so that wsl.conf (no systemd, no automount, no interop) applies
    wsl(["--terminate", a.distro], timeout=120)
    if not ensure_running(a.distro):
        die("sandbox did not boot; see /var/log/aiwb-family.log")
    users = [add_user(a.distro, n, u, a.disk_gb, a.rustup) for n, u in parse_users(a.users)]
    print(json.dumps({"ok": True, "distro": a.distro, "provision": out2.strip().splitlines()[-1:], "users": users},
                     ensure_ascii=False, indent=1))
    return 0


def cmd_update(a):
    """Push the current files and recompile the launcher (no reinstall)."""
    rc, out, err = wsl(["-d", a.distro, "-u", "root", "--cd", "/", "-e", "tar", "-xf", "-", "-C", "/"], files_tar(), 120)
    check_rc("copy files", rc, out, err)
    conf = (FILES / "sandbox.conf").read_text(encoding="utf-8").replace("\r\n", "\n").strip()
    rc, out, err = root(a.distro, INSTALL_FILES.replace("__SANDBOX_CONF__", conf)
                        + "/usr/local/sbin/aiwb-family-boot cgroups; /usr/local/sbin/aiwb-family-boot net\n", 300)
    check_rc("install files", rc, out, err)
    print(json.dumps({"ok": True, "out": out.strip().splitlines()}, ensure_ascii=False))
    return 0


def cmd_status(a):
    d = distros()
    if a.distro not in d:
        die(f"distro {a.distro} not installed")
    res = {"distro": a.distro, "state": d[a.distro]}
    if d[a.distro] == "Running" or a.wake:
        ensure_running(a.distro)
        rc, out, err = root(a.distro, STATUS, 60)
        res["inside"] = out.splitlines()
    print(json.dumps(res, ensure_ascii=False, indent=1))
    return 0


def cmd_add_user(a):
    if not ensure_running(a.distro):
        die("sandbox not booted")
    print(json.dumps({"ok": True, "user": add_user(a.distro, a.name, a.uid, a.disk_gb, a.rustup)}, ensure_ascii=False))
    return 0


def cmd_reset_user(a):
    if not NAME_RE.match(a.name):
        die("bad name")
    if not ensure_running(a.distro):
        die("sandbox not booted")
    _, out, _ = root(a.distro, f"awk '$1==\"{a.name}\"{{print $2, $3}}' /etc/aiwb-family/users", 60)
    if not out.strip():
        die(f"{a.name} is not a sandbox user")
    uid, disk = out.split()
    wipe = f"rm -f /var/lib/aiwb-family/homes/{a.name}.img; echo wiped"
    rc, o, e = root(a.distro, DEL_USER.replace("__U__", a.name).replace("__WIPE__", wipe), 120)
    check_rc("reset", rc, o, e)
    print(json.dumps({"ok": True, "reset": add_user(a.distro, a.name, int(uid), int(a.disk_gb or disk), a.rustup)},
                     ensure_ascii=False))
    return 0


def cmd_del_user(a):
    if not NAME_RE.match(a.name):
        die("bad name")
    wipe = (f"rm -f /var/lib/aiwb-family/homes/{a.name}.img; awk -v u={a.name} '$1!=u' /etc/aiwb-family/users > "
            "/etc/aiwb-family/users.new && mv -f /etc/aiwb-family/users.new /etc/aiwb-family/users; "
            f"ip netns del aiwb-{a.name} 2>/dev/null; ip link del afh$uid 2>/dev/null; "
            f"rmdir /sys/fs/cgroup/aiwb-family/{a.name} 2>/dev/null; userdel {a.name}; "
            "/usr/local/sbin/aiwb-family-boot net; echo deleted")
    rc, o, e = root(a.distro, DEL_USER.replace("__U__", a.name).replace("__WIPE__", wipe), 120)
    check_rc("del-user", rc, o, e)
    print(json.dumps({"ok": True, "out": o.strip()}, ensure_ascii=False))
    return 0


def cmd_check(a):
    if not ensure_running(a.distro):
        die("sandbox not booted")
    # LAN / tailnet / Windows host / other distros targets: gateway of WSL NAT, VM own IP, docker bridge, router
    _, out, _ = root(a.distro, "ip -4 route show default | awk '{print $3}'; ip -4 -o addr show eth0 | awk '{print $4}' | "
                               "cut -d/ -f1; echo 10.255.255.254:80; echo 172.17.0.1; echo 192.168.1.1; "
                               "echo 100.100.100.100; ip=$(ip -4 -o addr show eth0 | awk '{print $4}' | cut -d/ -f1); "
                               r"ss -ltnH | awk '{print $4}' | grep -E '^(0\.0\.0\.0|\*):' | cut -d: -f2 | sort -un | head -5 | "
                               "sed \"s/^/$ip:/\"", 60)
    targets = " ".join(out.split() + a.deny)
    rc, out, err = as_user(a.distro, a.user, CHECK.replace("__NETDENY__", targets), 1800)
    lines = [x for x in (out + err).splitlines() if x.startswith(("PASS", "FAIL", "INFO"))]
    fails = [x for x in lines if x.startswith("FAIL")]
    other = [x for x in (out + err).splitlines() if x and x not in lines][:20]
    print(json.dumps({"ok": not fails and bool(lines), "user": a.user, "results": lines, "other_output": other},
                     ensure_ascii=False, indent=1))
    return 0 if not fails and lines else 1


def cmd_run(a):
    if not a.command:
        die("no command")
    cmd = " ".join(a.command)
    rc, out, err = as_user(a.distro, a.user, cmd, a.timeout)
    sys.stdout.write(out)
    sys.stderr.write(err)
    return rc


def cmd_destroy(a):
    if not a.yes:
        die("destroy deletes the distro and all children's files; repeat with --yes")
    d = distros()
    if a.distro in d:
        # remove what lives in the shared VM (firewall tables, docker rule, cgroups) before unregistering
        root(a.distro, "for u in $(awk '{print $2}' /etc/aiwb-family/users); do pkill -9 -u $u; done; sleep 1; "
                       "/usr/local/sbin/aiwb-family-boot unnet; "
                       "for c in /sys/fs/cgroup/aiwb-family/*/; do rmdir $c; done; rmdir /sys/fs/cgroup/aiwb-family", 120)
        wsl(["--terminate", a.distro], timeout=120)
        rc, out, err = wsl(["--unregister", a.distro], timeout=600)
        check_rc("unregister", rc, out, err)
    print(json.dumps({"ok": True, "destroyed": a.distro}))
    return 0


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--distro", default=DISTRO)
    sub = ap.add_subparsers(dest="cmd", required=True)
    c = sub.add_parser("create", help="install Ubuntu 24.04 as the sandbox distro, provision, add users")
    c.add_argument("--location", default=LOCATION)
    c.add_argument("--vhd-size", default="40GB", help="maximum size of the distro disk")
    c.add_argument("--users", nargs="*", default=DEFAULT_USERS, help="name[:uid] ...")
    c.add_argument("--disk-gb", type=int, default=10, help="home disk image per user")
    c.add_argument("--rustup", action="store_true", help="also install rustup + stable toolchain per user")
    sub.add_parser("update", help="push changed sandbox files and recompile the launcher")
    s = sub.add_parser("status", help="state, limits, usage, firewall counters")
    s.add_argument("--wake", action="store_true", help="start the distro if stopped")
    u = sub.add_parser("add-user", help="add (or repair) a sandbox user")
    u.add_argument("name")
    u.add_argument("--uid", type=int, default=0)
    u.add_argument("--disk-gb", type=int, default=10)
    u.add_argument("--rustup", action="store_true")
    r = sub.add_parser("reset-user", help="kill processes, wipe the home image and start from /etc/skel")
    r.add_argument("name")
    r.add_argument("--disk-gb", type=int, default=0, help="new image size (default: keep)")
    r.add_argument("--rustup", action="store_true")
    x = sub.add_parser("del-user", help="remove a sandbox user and her files")
    x.add_argument("name")
    k = sub.add_parser("check", help="build+run hello in C/C++/Python/Node/.NET/Java/Rust and test isolation")
    k.add_argument("--user", default=(DEFAULT_USERS[0].split(":")[0] if DEFAULT_USERS else None), required=not DEFAULT_USERS)
    k.add_argument("--deny", nargs="*", default=[], help="extra addresses that must be unreachable (LAN hosts)")
    n = sub.add_parser("run", help="run a command as a sandbox user (the Bridge interface)")
    n.add_argument("user")
    n.add_argument("--timeout", type=int, default=600)
    n.add_argument("command", nargs=argparse.REMAINDER)
    dd = sub.add_parser("destroy", help="remove firewall/cgroups and unregister the distro")
    dd.add_argument("--yes", action="store_true")
    a = ap.parse_args()
    return {"create": cmd_create, "update": cmd_update, "status": cmd_status, "add-user": cmd_add_user,
            "reset-user": cmd_reset_user, "del-user": cmd_del_user, "check": cmd_check, "run": cmd_run,
            "destroy": cmd_destroy}[a.cmd](a)


if __name__ == "__main__":
    sys.exit(main())
