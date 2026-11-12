# SPDX-License-Identifier: AGPL-3.0-or-later
# Uploads the CO sign-in endpoints and (optionally) the secret config to Hostinger via the SSH connection of install.json (or hz.py).
import os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "install"))
import aiwb_install  # noqa: E402  Freedom F1: site and server paths of this install
# Freedom F2: the SSH connection of install.json site.ssh; the owner's hz.py (~/.ssh) stays first where it exists
sys.path.insert(0, os.path.join(os.path.expanduser("~"), ".ssh"))
try:
    import hz
    _connect = hz.connect
except ImportError:
    _connect = aiwb_install.ssh_connect
HOME = aiwb_install.get("site.home", "").rstrip("/")
WEB = aiwb_install.get("site.webRoot", "").rstrip("/") + "/auth"

c = _connect()
sftp = c.open_sftp()

def run(cmd):
    _, out, err = c.exec_command(cmd)
    print(out.read().decode(), err.read().decode())

run("mkdir -p %s/co-auth %s && chmod 700 %s/co-auth" % (HOME, WEB, HOME))
secret = os.path.join(HERE, "_secret_config.php")
if os.path.exists(secret):
    sftp.put(secret, HOME + "/co-auth/config.php")
    sftp.chmod(HOME + "/co-auth/config.php", 0o600)
    os.remove(secret)            # the secret stays only on the server
    print("config.php uploaded, local copy removed")
for name in ["lib.php", "login.php", "callback.php", "token.php", "logout.php", ".htaccess"]:
    sftp.put(os.path.join(HERE, "auth", name), WEB + "/" + name)
    print("put", name)
run("ls -la %s %s/co-auth" % (WEB, HOME))
sftp.close()
c.close()
