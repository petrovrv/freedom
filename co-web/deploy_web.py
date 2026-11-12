# SPDX-License-Identifier: AGPL-3.0-or-later
# Uploads dist/ to the web root of the CO site (install.json site.webRoot) (keeps auth/ and default.php) through the SSH connection of install.json (or hz.py).
import io, os, sys, tarfile

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
WEB = aiwb_install.get("site.webRoot", "").rstrip("/")
HOME = aiwb_install.get("site.home", "").rstrip("/")
if not WEB or not HOME:
    sys.exit("install.json: site.webRoot and site.home are required")
buf = io.BytesIO()
with tarfile.open(fileobj=buf, mode="w:gz") as tar:
    tar.add(os.path.join(HERE, "dist"), arcname=".")
buf.seek(0)
c = _connect()
sftp = c.open_sftp()
sftp.putfo(buf, HOME + "/co-dist.tgz")
_, out, err = c.exec_command("set -e; cd %s; rm -rf assets; tar xzf ~/co-dist.tgz -C %s; rm ~/co-dist.tgz; ls -la %s %s/assets" % (WEB, WEB, WEB, WEB))
print(out.read().decode(), err.read().decode())
sftp.close()
c.close()
