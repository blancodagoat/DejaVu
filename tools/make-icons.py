#!/usr/bin/env python3
"""Generates the multi-resolution .ico files (and the README logo) in assets/.

Pure stdlib: rasterises the mark by supersampling, encodes each size as a PNG
and packs them into an ICO. The mark is a counterclockwise replay ring with an
arrowhead around a centre record dot — the sibling of Memento's crop-marks.

Usage: python3 tools/make-icons.py
"""

import math
import os
import struct
import zlib

SIZES = (16, 32, 48, 256)
SS = 4  # supersampling factor per axis

BG = (0x12, 0x10, 0x0E)
TEXT = (0xF2, 0xEC, 0xE2)
ACCENT = (0xDC, 0x3C, 0x3C)


def _circle(cx, cy, r):
    return lambda x, y: (x - cx) ** 2 + (y - cy) ** 2 <= r * r


def _rounded(x0, y0, x1, y1, r):
    def inside(x, y):
        if not (x0 <= x <= x1 and y0 <= y <= y1):
            return False
        cx = min(max(x, x0 + r), x1 - r)
        cy = min(max(y, y0 + r), y1 - r)
        return (x - cx) ** 2 + (y - cy) ** 2 <= r * r
    return inside


def _arc(cx, cy, r_out, r_in, a0, a1):
    """Annulus segment. Angles in degrees, screen convention (y down, clockwise
    positive), covering the sweep from a0 to a1 going clockwise."""
    span = (a1 - a0) % 360.0

    def inside(x, y):
        d2 = (x - cx) ** 2 + (y - cy) ** 2
        if not (r_in * r_in <= d2 <= r_out * r_out):
            return False
        a = math.degrees(math.atan2(y - cy, x - cx))
        return (a - a0) % 360.0 <= span
    return inside


def _triangle(p1, p2, p3):
    def half(a, b, x, y):
        return (b[0] - a[0]) * (y - a[1]) - (b[1] - a[1]) * (x - a[0])

    def inside(x, y):
        d1 = half(p1, p2, x, y)
        d2 = half(p2, p3, x, y)
        d3 = half(p3, p1, x, y)
        neg = d1 < 0 or d2 < 0 or d3 < 0
        pos = d1 > 0 or d2 > 0 or d3 > 0
        return not (neg and pos)
    return inside


def shapes(size, with_background, dot_colour):
    """Replay ring with an arrowhead plus a centre dot, in unit space.

    Ring thickness, arrowhead and dot radius have floors in device pixels so
    the mark stays legible at 16px instead of dissolving into grey.
    """
    t = max(0.10, 2.2 / size)        # ring thickness
    r_mid = 0.335                     # ring centreline radius
    dot = max(0.115, 1.7 / size)

    layers = []
    if with_background:
        layers.append((_rounded(0.02, 0.02, 0.98, 0.98, 0.20), BG))

    # The gap sits top-left; the arc runs clockwise from the gap's right edge all
    # the way around to its left edge, where the arrowhead points on
    # counterclockwise — the universal "replay" glyph.
    gap_l, gap_r = -150.0, -80.0
    layers.append((_arc(0.5, 0.5, r_mid + t / 2, r_mid - t / 2, gap_r, gap_l), ACCENT))

    # Arrowhead: tangent at the arc's counterclockwise end, pointing into the gap.
    a = math.radians(gap_l)
    px, py = 0.5 + r_mid * math.cos(a), 0.5 + r_mid * math.sin(a)
    tx, ty = math.sin(a), -math.cos(a)          # counterclockwise tangent
    nx, ny = math.cos(a), math.sin(a)           # outward normal
    head_len = max(0.16, 3.2 / size)
    head_w = max(0.115, 2.4 / size)
    tip = (px + tx * head_len, py + ty * head_len)
    base_out = (px + nx * head_w, py + ny * head_w)
    base_in = (px - nx * head_w, py - ny * head_w)
    layers.append((_triangle(tip, base_out, base_in), ACCENT))

    layers.append((_circle(0.5, 0.5, dot), dot_colour))
    return layers


def render(size, layers):
    rows = []
    for py in range(size):
        row = bytearray()
        for px in range(size):
            acc = [0.0, 0.0, 0.0, 0.0]
            for sy in range(SS):
                y = (py + (sy + 0.5) / SS) / size
                for sx in range(SS):
                    x = (px + (sx + 0.5) / SS) / size
                    hit = None
                    for shape, colour in layers:
                        if shape(x, y):
                            hit = colour
                    if hit is not None:
                        acc[0] += hit[0]
                        acc[1] += hit[1]
                        acc[2] += hit[2]
                        acc[3] += 255.0
            n = SS * SS
            a = acc[3] / n
            if a <= 0.0:
                row += b"\x00\x00\x00\x00"
                continue
            covered = acc[3] / 255.0
            row += bytes((
                int(round(acc[0] / covered)),
                int(round(acc[1] / covered)),
                int(round(acc[2] / covered)),
                int(round(a)),
            ))
        rows.append(bytes(row))
    return rows


def png(size, rows):
    raw = b"".join(b"\x00" + r for r in rows)

    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body))

    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(raw, 9))
        + chunk(b"IEND", b"")
    )


def ico(images):
    count = len(images)
    header = struct.pack("<HHH", 0, 1, count)
    offset = 6 + 16 * count
    entries, blobs = b"", b""
    for size, data in images:
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        blobs += data
        offset += len(data)
    return header + entries + blobs


def build(path, with_background, dot_colour):
    images = [(s, png(s, render(s, shapes(s, with_background, dot_colour)))) for s in SIZES]
    with open(path, "wb") as fh:
        fh.write(ico(images))
    print("wrote %s (%d bytes)" % (path, os.path.getsize(path)))


def main():
    root = os.path.join(os.path.dirname(os.path.abspath(__file__)), os.pardir, "assets")
    root = os.path.normpath(root)
    os.makedirs(root, exist_ok=True)
    # Tray variants are named for the theme they are drawn *for*: the light
    # variant is the pale mark that sits on a dark taskbar, and vice versa.
    build(os.path.join(root, "tray-light.ico"), False, TEXT)
    build(os.path.join(root, "tray-dark.ico"), False, BG)
    build(os.path.join(root, "app.ico"), True, TEXT)

    # README hero.
    with open(os.path.join(root, "logo.png"), "wb") as fh:
        fh.write(png(256, render(256, shapes(256, True, TEXT))))
    print("wrote logo.png")


if __name__ == "__main__":
    main()
