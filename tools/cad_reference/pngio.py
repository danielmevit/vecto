#!/usr/bin/env python3
"""Minimal PNG reader/writer (standard library only).

Copied from the read-only VEC-79 archive tools/pngio.py (behaviour unchanged).


The VEC-79 grader must run on a plain `python3` with no third-party packages, so
the benchmark ships its own tiny PNG codec instead of relying on Pillow. It
supports what the renderer (CairoSVG) and the .NET harness (BCL encoder) write:
8- and 16-bit, greyscale / RGB / palette / greyscale+alpha / RGBA, non-interlaced.
"""

import struct
import zlib

_SIG = b"\x89PNG\r\n\x1a\n"


def decode(path):
    """Decode a PNG -> (width, height, channels, bit_depth, raw_rows).

    `raw_rows` holds the unfiltered pixels, row major, `channels` bytes per pixel.
    """
    with open(path, "rb") as fh:
        data = fh.read()
    if data[:8] != _SIG:
        raise ValueError(f"{path}: not a PNG file")
    pos = 8
    width = height = 0
    bitdepth = colortype = 0
    interlace = 0
    idat = bytearray()
    palette = b""
    trns = b""
    while pos + 8 <= len(data):
        (length,) = struct.unpack(">I", data[pos:pos + 4])
        ctype = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + length]
        pos += 12 + length
        if ctype == b"IHDR":
            width, height, bitdepth, colortype, _comp, _filt, interlace = struct.unpack(">IIBBBBB", body)
        elif ctype == b"PLTE":
            palette = body
        elif ctype == b"tRNS":
            trns = body
        elif ctype == b"IDAT":
            idat += body
        elif ctype == b"IEND":
            break
    if interlace:
        raise ValueError(f"{path}: interlaced PNG is not supported")
    channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[colortype]
    if bitdepth not in (8, 16):
        raise ValueError(f"{path}: unsupported bit depth {bitdepth}")
    raw = zlib.decompress(bytes(idat))
    bpp = channels * (bitdepth // 8)
    stride = width * bpp
    out = bytearray()
    prev = bytearray(stride)
    pos = 0
    for _y in range(height):
        ft = raw[pos]
        pos += 1
        line = bytearray(raw[pos:pos + stride])
        pos += stride
        if ft == 1:
            for i in range(bpp, stride):
                line[i] = (line[i] + line[i - bpp]) & 0xFF
        elif ft == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 0xFF
        elif ft == 3:
            for i in range(stride):
                left = line[i - bpp] if i >= bpp else 0
                line[i] = (line[i] + ((left + prev[i]) >> 1)) & 0xFF
        elif ft == 4:
            for i in range(stride):
                a = line[i - bpp] if i >= bpp else 0
                b = prev[i]
                c = prev[i - bpp] if i >= bpp else 0
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pr) & 0xFF
        elif ft != 0:
            raise ValueError(f"{path}: unknown filter {ft}")
        out += line
        prev = line
    return width, height, channels, bitdepth, bytes(out), palette, trns


def read_png(path):
    """Return (width, height, rgba_bytes) of a PNG file."""
    width, height, channels, bitdepth, raw, palette, trns = decode(path)
    step = 2 if bitdepth == 16 else 1
    rgba = bytearray(width * height * 4)
    for i in range(width * height):
        base = i * channels
        o = i * 4
        if channels == 1:
            g = raw[base]
            r = gg = b = g
            a = 255
        elif channels == 3:
            r = raw[base]
            gg = raw[base + step]
            b = raw[base + 2 * step]
            a = 255
        elif channels == 4:
            r = raw[base]
            gg = raw[base + step]
            b = raw[base + 2 * step]
            a = raw[base + 3 * step]
        else:  # greyscale + alpha
            g = raw[base]
            r = gg = b = g
            a = raw[base + step]
        rgba[o] = r
        rgba[o + 1] = gg
        rgba[o + 2] = b
        rgba[o + 3] = a
    return width, height, bytes(rgba)


def read_ink_plane(path):
    """Fast path -> (width, height, ink_bytes, alpha_bytes).

    `ink_bytes` is 0xFF for a dark pixel (luminance < 128) and 0x00 otherwise;
    `alpha_bytes` is 0xFF for an opaque pixel. The benchmark's images are neutral
    grey (black ink on white paper), so the R channel carries the luminance.
    """
    width, height, channels, bitdepth, raw, palette, trns = decode(path)
    if channels == 3 and bitdepth == 8:
        gray = raw[0::3]
        alpha = b"\xff" * (width * height)
    elif channels == 4 and bitdepth == 8:
        gray = raw[0::4]
        alpha = raw[3::4]
    elif channels == 1 and bitdepth == 8:
        gray = raw
        alpha = b"\xff" * (width * height)
    elif channels == 2 and bitdepth == 8:
        gray = raw[0::2]
        alpha = raw[1::2]
    else:
        raise ValueError(f"{path}: unsupported layout for the ink plane "
                         f"(channels={channels}, bitdepth={bitdepth})")
    ink = gray.translate(bytes(0xFF if i < 128 else 0x00 for i in range(256)))
    opaque = alpha.translate(bytes(0xFF if i >= 128 else 0x00 for i in range(256)))
    return width, height, ink, opaque


def write_png(path, width, height, rgba):
    """Write an 8-bit RGBA PNG (filter 0 rows, zlib deflate)."""
    raw = bytearray()
    stride = width * 4
    for y in range(height):
        raw.append(0)
        raw += rgba[y * stride:(y + 1) * stride]
    comp = zlib.compress(bytes(raw), 9)

    def chunk(tag, body):
        return (struct.pack(">I", len(body)) + tag + body
                + struct.pack(">I", zlib.crc32(tag + body) & 0xFFFFFFFF))

    out = bytearray(_SIG)
    out += chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
    out += chunk(b"IDAT", comp)
    out += chunk(b"IEND", b"")
    with open(path, "wb") as fh:
        fh.write(bytes(out))
