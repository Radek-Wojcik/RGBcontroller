"""Draws the RGBcontroller icon (assets/rgbcontroller.ico): the brand mark of the UI (a conic rainbow with two
clipped corners) around a dark core, at 16-256 px. Standard library only; run `python tools/make-icon.py`."""
import colorsys
import math
import os
import struct
import zlib

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
CORE = (0x0B, 0x0D, 0x12)


def inside_mark(x, y):
    """The UI mark: polygon(30% 0, 100% 0, 100% 70%, 70% 100%, 0 100%, 0 30%) in 0..1 coordinates."""
    if x < 0 or y < 0 or x > 1 or y > 1:
        return False
    if x + y < 0.3:  # top-left cut
        return False
    if x + y > 1.7:  # bottom-right cut
        return False
    return True


def pixel(size, px, py):
    # 4x4 supersampling for clean edges at small sizes.
    r = g = b = a = 0.0
    n = 4
    for sy in range(n):
        for sx in range(n):
            x = (px + (sx + 0.5) / n) / size
            y = (py + (sy + 0.5) / n) / size
            if not inside_mark(x, y):
                continue
            # The core: a dark clipped square, so the ring of color reads like a lit LED loop.
            cx, cy = (x - 0.5) / 0.30, (y - 0.5) / 0.30
            core = abs(cx) < 1 and abs(cy) < 1 and (cx + cy) > -1.6 and (cx + cy) < 1.6
            if core:
                cr, cg, cb = (c / 255 for c in CORE)
            else:
                hue = (math.atan2(y - 0.5, x - 0.5) / (2 * math.pi) + 0.25) % 1.0
                cr, cg, cb = colorsys.hsv_to_rgb(hue, 0.85, 1.0)
            r, g, b, a = r + cr, g + cg, b + cb, a + 1
    if a == 0:
        return (0, 0, 0, 0)
    return (round(255 * r / a), round(255 * g / a), round(255 * b / a), round(255 * a / (n * n)))


def png(size):
    rows = b""
    for py in range(size):
        rows += b"\x00" + b"".join(bytes(pixel(size, px, py)) for px in range(size))

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b"")


def main():
    images = [png(s) for s in SIZES]
    out = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    for size, data in zip(SIZES, images):
        out += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    out += b"".join(images)
    target = os.path.join(os.path.dirname(__file__), "..", "assets", "rgbcontroller.ico")
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as f:
        f.write(out)
    print(f"wrote {os.path.normpath(target)} ({len(out)} bytes)")


if __name__ == "__main__":
    main()
