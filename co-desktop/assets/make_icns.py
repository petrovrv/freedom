#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
"""Make src/CO.Mac/CO.icns (icon of CO.app on the Mac) from icon-512.png: PNG chunks only, which macOS 10.7+ reads."""
import io, os, struct
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
src = Image.open(os.path.join(HERE, "icon-512.png")).convert("RGBA")
chunks = b""
for tag, size in ((b"ic07", 128), (b"ic08", 256), (b"ic09", 512), (b"ic12", 64), (b"ic11", 32)):
    buf = io.BytesIO()
    src.resize((size, size), Image.LANCZOS).save(buf, "PNG")
    data = buf.getvalue()
    chunks += tag + struct.pack(">I", len(data) + 8) + data
out = os.path.join(HERE, "..", "src", "CO.Mac", "CO.icns")
open(out, "wb").write(b"icns" + struct.pack(">I", len(chunks) + 8) + chunks)
print(out, len(chunks) + 8)
