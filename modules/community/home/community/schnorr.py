# SPDX-License-Identifier: AGPL-3.0-or-later
"""Pure-Python BIP340 Schnorr signatures over secp256k1 and bech32 (NIP-19 npub/nsec).

No native dependency: a Freedom workbench must be able to sign Nostr events with the Python it already has.
Jacobian coordinates (one modular inverse per scalar multiplication): a few milliseconds per signature or check,
enough for a forum where a workbench signs tens of events per day. Not constant-time: keys are only used on the owner's own PC.
"""
from __future__ import annotations

import hashlib
import os

P = 0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEFFFFFC2F
N = 0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141
G = (0x79BE667EF9DCBBAC55A06295CE870B07029BFCDB2DCE28D959F2815B16F81798,
     0x483ADA7726A3C4655DA4FBFC0E1108A8FD17B448A68554199C47D08FFB10D4B8)


# Jacobian coordinates (X, Y, Z) ~ affine (X/Z^2, Y/Z^3): no modular inverse per step; Z == 0 is infinity.
_INF = (0, 1, 0)


def _jdouble(p):
    x, y, z = p
    if z == 0 or y == 0:
        return _INF
    ysq = y * y % P
    s = 4 * x * ysq % P
    m = 3 * x * x % P
    x3 = (m * m - 2 * s) % P
    return x3, (m * (s - x3) - 8 * ysq * ysq) % P, 2 * y * z % P


def _jadd(p, q):
    if p[2] == 0:
        return q
    if q[2] == 0:
        return p
    x1, y1, z1 = p
    x2, y2, z2 = q
    z1s, z2s = z1 * z1 % P, z2 * z2 % P
    u1, u2 = x1 * z2s % P, x2 * z1s % P
    s1, s2 = y1 * z2s * z2 % P, y2 * z1s * z1 % P
    if u1 == u2:
        return _jdouble(p) if s1 == s2 else _INF
    h, r = (u2 - u1) % P, (s2 - s1) % P
    h2 = h * h % P
    h3 = h * h2 % P
    u1h2 = u1 * h2 % P
    x3 = (r * r - h3 - 2 * u1h2) % P
    return x3, (r * (u1h2 - x3) - s1 * h3) % P, h * z1 * z2 % P


def _affine(p):
    if p[2] == 0:
        return None
    zi = pow(p[2], -1, P)
    zi2 = zi * zi % P
    return p[0] * zi2 % P, p[1] * zi2 * zi % P


def _jmul(pt, n: int):
    r = _INF
    q = (pt[0], pt[1], 1)
    for i in range(n.bit_length() - 1, -1, -1):
        r = _jdouble(r)
        if (n >> i) & 1:
            r = _jadd(r, q)
    return r


def _mul(pt, n: int):
    return _affine(_jmul(pt, n))


def _b32(x: int) -> bytes:
    return x.to_bytes(32, "big")


def _int(b: bytes) -> int:
    return int.from_bytes(b, "big")


def tagged_hash(tag: str, msg: bytes) -> bytes:
    t = hashlib.sha256(tag.encode()).digest()
    return hashlib.sha256(t + t + msg).digest()


def _lift_x(x: int):
    if x >= P:
        return None
    y_sq = (pow(x, 3, P) + 7) % P
    y = pow(y_sq, (P + 1) // 4, P)
    if y * y % P != y_sq:
        return None
    return x, y if y % 2 == 0 else P - y


def pubkey_gen(seckey: bytes) -> bytes:
    d0 = _int(seckey)
    if not 1 <= d0 <= N - 1:
        raise ValueError("secret key out of range")
    return _b32(_mul(G, d0)[0])


def sign(msg: bytes, seckey: bytes, aux_rand: bytes | None = None) -> bytes:
    if aux_rand is None:
        aux_rand = os.urandom(32)
    d0 = _int(seckey)
    if not 1 <= d0 <= N - 1:
        raise ValueError("secret key out of range")
    if len(aux_rand) != 32:
        raise ValueError("aux_rand must be 32 bytes")
    pt = _mul(G, d0)
    d = d0 if pt[1] % 2 == 0 else N - d0
    t = bytes(a ^ b for a, b in zip(_b32(d), tagged_hash("BIP0340/aux", aux_rand)))
    k0 = _int(tagged_hash("BIP0340/nonce", t + _b32(pt[0]) + msg)) % N
    if k0 == 0:
        raise RuntimeError("nonce is zero")
    r = _mul(G, k0)
    k = k0 if r[1] % 2 == 0 else N - k0
    e = _int(tagged_hash("BIP0340/challenge", _b32(r[0]) + _b32(pt[0]) + msg)) % N
    sig = _b32(r[0]) + _b32((k + e * d) % N)
    if not verify(msg, _b32(pt[0]), sig):
        raise RuntimeError("created signature does not verify")
    return sig


def verify(msg: bytes, pubkey: bytes, sig: bytes) -> bool:
    if len(pubkey) != 32 or len(sig) != 64:
        return False
    pt = _lift_x(_int(pubkey))
    r, s = _int(sig[:32]), _int(sig[32:])
    if pt is None or r >= P or s >= N:
        return False
    e = _int(tagged_hash("BIP0340/challenge", sig[:32] + pubkey + msg)) % N
    rr = _affine(_jadd(_jmul(G, s), _jmul(pt, N - e)))
    return rr is not None and rr[1] % 2 == 0 and rr[0] == r


def new_seckey() -> bytes:
    while True:
        k = os.urandom(32)
        if 1 <= _int(k) <= N - 1:
            return k


# ---------------------------------------------------------------- bech32 (BIP173), used by NIP-19

_CHARSET = "qpzry9x8gf2tvdw0s3jn54khce6mua7l"


def _polymod(values):
    gen = [0x3B6A57B2, 0x26508E6D, 0x1EA119FA, 0x3D4233DD, 0x2A1462B3]
    chk = 1
    for v in values:
        b = chk >> 25
        chk = (chk & 0x1FFFFFF) << 5 ^ v
        for i in range(5):
            chk ^= gen[i] if (b >> i) & 1 else 0
    return chk


def _hrp_expand(hrp):
    return [ord(x) >> 5 for x in hrp] + [0] + [ord(x) & 31 for x in hrp]


def _convertbits(data, frombits, tobits, pad=True):
    acc = bits = 0
    ret = []
    maxv = (1 << tobits) - 1
    for value in data:
        if value < 0 or value >> frombits:
            raise ValueError("bad value")
        acc = (acc << frombits) | value
        bits += frombits
        while bits >= tobits:
            bits -= tobits
            ret.append((acc >> bits) & maxv)
    if pad:
        if bits:
            ret.append((acc << (tobits - bits)) & maxv)
    elif bits >= frombits or ((acc << (tobits - bits)) & maxv):
        raise ValueError("bad padding")
    return ret


def bech32_encode(hrp: str, data: bytes) -> str:
    d5 = _convertbits(data, 8, 5)
    poly = _polymod(_hrp_expand(hrp) + d5 + [0] * 6) ^ 1
    chk = [(poly >> 5 * (5 - i)) & 31 for i in range(6)]
    return hrp + "1" + "".join(_CHARSET[d] for d in d5 + chk)


def bech32_decode(s: str) -> tuple[str, bytes]:
    if s.lower() != s and s.upper() != s:
        raise ValueError("mixed case")
    s = s.lower()
    pos = s.rfind("1")
    if pos < 1 or pos + 7 > len(s):
        raise ValueError("bad bech32")
    hrp, rest = s[:pos], s[pos + 1:]
    try:
        data = [_CHARSET.index(c) for c in rest]
    except ValueError:
        raise ValueError("bad bech32 character") from None
    if _polymod(_hrp_expand(hrp) + data) != 1:
        raise ValueError("bad bech32 checksum")
    return hrp, bytes(_convertbits(data[:-6], 5, 8, False))


def npub(pubkey_hex: str) -> str:
    return bech32_encode("npub", bytes.fromhex(pubkey_hex))


def nsec(seckey_hex: str) -> str:
    return bech32_encode("nsec", bytes.fromhex(seckey_hex))


def decode_key(s: str, want: str) -> str:
    """npub/nsec or 64-hex -> hex."""
    s = s.strip()
    if len(s) == 64 and all(c in "0123456789abcdefABCDEF" for c in s):
        return s.lower()
    hrp, data = bech32_decode(s)
    if hrp != want or len(data) != 32:
        raise ValueError(f"not an {want}")
    return data.hex()
