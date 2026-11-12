# SPDX-License-Identifier: AGPL-3.0-or-later
"""Signed release manifests of Freedom (SPEC §3.5, §4.4).

A release is a folder (built CO Web, installers, sources). Its manifest lists every file with size and
sha256 and carries Ed25519 signatures of maintainers. Anyone may mirror the files; a client accepts them
only when at least `threshold` signatures of keys from the trust list are valid and every hash matches,
so a mirror can serve but cannot change a release.

  python install/freedom_release.py keygen --name NAME            # key in keyring aiwb/freedom-release, prints the public key
  python install/freedom_release.py build DIR --version 0.1.0 [--out DIR/freedom-manifest.json]
  python install/freedom_release.py sign MANIFEST --name NAME     # adds this maintainer's signature
  python install/freedom_release.py verify DIR [--manifest M] [--trust install/maintainers.json] [--strict]

The trust list (install/maintainers.json) holds public keys and the threshold; it ships with the installer
and changes only through a release signed under the previous list. Exit codes: 0 ok, 1 error, 2 not verified.
"""
from __future__ import annotations

import argparse
import base64
import datetime
import hashlib
import json
import os
import pathlib
import sys

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey, Ed25519PublicKey
from cryptography.hazmat.primitives import serialization

MANIFEST_NAME = "freedom-manifest.json"
KEYRING_SERVICE = "aiwb/freedom-release"
DEFAULT_TRUST = pathlib.Path(__file__).resolve().with_name("maintainers.json")
FORMAT = "freedom-release/1"


def sha256_file(p: pathlib.Path) -> str:
    h = hashlib.sha256()
    with p.open("rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def list_files(root: pathlib.Path) -> list[pathlib.Path]:
    out = []
    for p in sorted(root.rglob("*")):
        rel = p.relative_to(root).as_posix()
        if p.is_file() and rel != MANIFEST_NAME and p.name != "desktop.ini" and ".git/" not in rel + "/":
            out.append(p)
    return out


def canonical(body: dict) -> bytes:
    """Bytes that are signed: the manifest without its signatures, sorted keys, no spaces."""
    return json.dumps({k: v for k, v in body.items() if k != "signatures"}, sort_keys=True,
                      separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def build(root: pathlib.Path, version: str) -> dict:
    files = [{"path": p.relative_to(root).as_posix(), "size": p.stat().st_size, "sha256": sha256_file(p)}
             for p in list_files(root)]
    return {"format": FORMAT, "version": version,
            "created": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            "files": files, "signatures": []}


def public_hex(key: Ed25519PrivateKey) -> str:
    return key.public_key().public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw).hex()


def sign(manifest: dict, name: str, key: Ed25519PrivateKey) -> dict:
    pub = public_hex(key)
    sig = base64.b64encode(key.sign(canonical(manifest))).decode()
    manifest["signatures"] = [s for s in manifest.get("signatures", []) if s.get("key") != pub]
    manifest["signatures"].append({"name": name, "key": pub, "sig": sig})
    return manifest


def load_trust(path: pathlib.Path) -> dict:
    t = json.loads(path.read_text(encoding="utf-8"))
    keys = {m["key"].lower(): m["name"] for m in t.get("maintainers", []) if not m.get("revoked")}
    thr = int(t.get("threshold", 1))
    if thr < 1:
        raise ValueError("trust list: threshold must be >= 1")
    return {"keys": keys, "threshold": thr}


def verify(root: pathlib.Path, manifest: dict, trust: dict, strict: bool = False) -> list[str]:
    """Problems found; empty list = verified."""
    problems = []
    if manifest.get("format") != FORMAT:
        problems.append(f"unknown manifest format {manifest.get('format')!r}")
    body = canonical(manifest)
    good = set()
    for s in manifest.get("signatures", []):
        k = str(s.get("key", "")).lower()
        if k not in trust["keys"]:
            continue
        try:
            Ed25519PublicKey.from_public_bytes(bytes.fromhex(k)).verify(base64.b64decode(s.get("sig", "")), body)
            good.add(k)
        except (InvalidSignature, ValueError):
            problems.append(f"bad signature of {trust['keys'][k]}")
    if len(good) < trust["threshold"]:
        problems.append(f"{len(good)} valid trusted signature(s), {trust['threshold']} needed")
    listed = set()
    root_r = root.resolve()
    for f in manifest.get("files", []):
        rel = str(f.get("path", ""))
        p = (root / rel).resolve()
        if rel.startswith("/") or ".." in pathlib.PurePosixPath(rel).parts or root_r not in p.parents:
            problems.append(f"unsafe path {rel!r}")
            continue
        listed.add(rel)
        if not p.is_file():
            problems.append(f"missing {rel}")
        elif p.stat().st_size != f.get("size") or sha256_file(p) != f.get("sha256"):
            problems.append(f"changed {rel}")
    if strict:
        for p in list_files(root):
            rel = p.relative_to(root).as_posix()
            if rel not in listed:
                problems.append(f"not in manifest {rel}")
    return problems


def keyring_key(name: str, create: bool = False) -> Ed25519PrivateKey:
    import keyring
    raw = keyring.get_password(KEYRING_SERVICE, name)
    if raw:
        return Ed25519PrivateKey.from_private_bytes(bytes.fromhex(raw))
    if not create:
        raise RuntimeError(f"no key {KEYRING_SERVICE}/{name} (run keygen)")
    key = Ed25519PrivateKey.generate()
    keyring.set_password(KEYRING_SERVICE, name, key.private_bytes(
        serialization.Encoding.Raw, serialization.PrivateFormat.Raw, serialization.NoEncryption()).hex())
    return key


def write_json(path: pathlib.Path, data: dict) -> None:
    tmp = path.with_name(path.name + ".tmp")
    tmp.write_text(json.dumps(data, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    os.replace(tmp, path)


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    g = sub.add_parser("keygen"); g.add_argument("--name", required=True)
    b = sub.add_parser("build"); b.add_argument("dir", type=pathlib.Path); b.add_argument("--version", required=True)
    b.add_argument("--out", type=pathlib.Path)
    s = sub.add_parser("sign"); s.add_argument("manifest", type=pathlib.Path); s.add_argument("--name", required=True)
    v = sub.add_parser("verify"); v.add_argument("dir", type=pathlib.Path); v.add_argument("--manifest", type=pathlib.Path)
    v.add_argument("--trust", type=pathlib.Path, default=DEFAULT_TRUST); v.add_argument("--strict", action="store_true")
    a = ap.parse_args(argv)
    try:
        if a.cmd == "keygen":
            print(json.dumps({"name": a.name, "key": public_hex(keyring_key(a.name, create=True))}))
        elif a.cmd == "build":
            out = a.out or a.dir / MANIFEST_NAME
            m = build(a.dir, a.version)
            write_json(out, m)
            print(f"{out}: {len(m['files'])} files, version {a.version}, unsigned")
        elif a.cmd == "sign":
            m = json.loads(a.manifest.read_text(encoding="utf-8"))
            write_json(a.manifest, sign(m, a.name, keyring_key(a.name)))
            print(f"{a.manifest}: signed by {a.name} ({len(m['signatures'])} signature(s))")
        else:
            mp = a.manifest or a.dir / MANIFEST_NAME
            problems = verify(a.dir, json.loads(mp.read_text(encoding="utf-8")), load_trust(a.trust), a.strict)
            for p in problems:
                print("  " + p)
            print("verified" if not problems else f"NOT verified: {len(problems)} problem(s)")
            return 2 if problems else 0
    except Exception as e:  # noqa: BLE001 - CLI boundary
        print(f"error: {e}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
