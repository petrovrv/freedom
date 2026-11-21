"""Streaming recipient-only encryption for mail backups (FMCS1)."""
import base64
import os
from pathlib import Path
import tempfile

from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey, X25519PublicKey
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.hkdf import HKDF

MAGIC = b'FMCS1\n'
CHUNK = 1024 * 1024
INFO = b'freedom concierge-mail backup v1'


def _raw(key):
    return key.public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw)


def _decode(value):
    try:
        raw = base64.b64decode(value, validate=True)
        if len(raw) != 32:
            raise ValueError()
        return raw
    except (ValueError, TypeError) as exc:
        raise ValueError('Invalid X25519 key') from exc


def generate_identity():
    key = X25519PrivateKey.generate()
    private = key.private_bytes(serialization.Encoding.Raw, serialization.PrivateFormat.Raw,
                                serialization.NoEncryption())
    return base64.b64encode(private).decode(), base64.b64encode(_raw(key.public_key())).decode()


def public_from_private(private_b64):
    return base64.b64encode(_raw(X25519PrivateKey.from_private_bytes(_decode(private_b64)).public_key())).decode()


def _cipher(private, public, ephemeral, recipient):
    shared = private.exchange(X25519PublicKey.from_public_bytes(public))
    key = HKDF(algorithm=hashes.SHA256(), length=32, salt=ephemeral + recipient, info=INFO).derive(shared)
    return AESGCM(key)


def _read(src, size):
    parts = bytearray()
    while len(parts) < size:
        data = src.read(size - len(parts))
        if not data:
            break
        parts.extend(data)
    return bytes(parts)


def seal_stream(src, dst, public_b64):
    recipient = _decode(public_b64)
    private = X25519PrivateKey.generate()
    ephemeral = _raw(private.public_key())
    cipher = _cipher(private, recipient, ephemeral, recipient)
    dst.write(MAGIC + ephemeral)
    current = _read(src, CHUNK)
    counter = 0
    while True:
        following = _read(src, CHUNK)
        final = not following
        encrypted = cipher.encrypt(b'\0' * 4 + counter.to_bytes(8, 'big'), current, bytes([final]))
        dst.write(len(encrypted).to_bytes(4, 'big') + encrypted)
        if final:
            return
        current = following
        counter += 1


def open_stream(src, dst, private_b64):
    private = X25519PrivateKey.from_private_bytes(_decode(private_b64))
    if _read(src, len(MAGIC)) != MAGIC:
        raise ValueError('Invalid sealed header')
    ephemeral = _read(src, 32)
    cipher = _cipher(private, ephemeral, ephemeral, _raw(private.public_key()))
    counter = 0
    while True:
        length = _read(src, 4)
        if len(length) != 4:
            raise ValueError('Missing final chunk')
        size = int.from_bytes(length, 'big')
        if not 16 <= size <= CHUNK + 16:
            raise ValueError('Invalid chunk length')
        encrypted = _read(src, size)
        if len(encrypted) != size:
            raise ValueError('Truncated chunk')
        nonce = b'\0' * 4 + counter.to_bytes(8, 'big')
        # The final bit is authenticated AAD, not a separate wire field.
        try:
            plain = cipher.decrypt(nonce, encrypted, b'\0')
            final = False
        except InvalidTag:
            try:
                plain = cipher.decrypt(nonce, encrypted, b'\1')
                final = True
            except InvalidTag:
                raise ValueError('Sealed chunk authentication failed') from None
        if final and src.read(1):
            raise ValueError('Trailing sealed data')
        dst.write(plain)
        if final:
            return
        counter += 1


def _file(operation, source, destination, key):
    destination = Path(destination)
    fd, name = tempfile.mkstemp(prefix='.seal-', dir=destination.parent)
    try:
        with os.fdopen(fd, 'wb') as dst, open(source, 'rb') as src:
            operation(src, dst, key)
            dst.flush()
            os.fsync(dst.fileno())
        os.replace(name, destination)
    finally:
        if os.path.exists(name):
            os.unlink(name)


def seal_file(source, destination, public_b64):
    _file(seal_stream, source, destination, public_b64)


def open_file(source, destination, private_b64):
    _file(open_stream, source, destination, private_b64)
