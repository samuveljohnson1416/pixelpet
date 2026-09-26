# Writes the macOS iconset (PNGs) of the pixel pet, no dependencies. build.sh turns it into AppIcon.icns.
import os, struct, sys, zlib

GRID = ["..XXXXXXXXXX..",
        "..XXXXXXXXXX..",
        "..XXEXXXXEXX..",
        "..XXEXXXXEXX..",
        "XXXXXXXXXXXXXX",
        "XXXXXXXXXXXXXX",
        "..XXXXXXXXXX..",
        "...X.X..X.X...",
        "...X.X..X.X..."]
COL = {"X": (217, 119, 87, 255), "E": (20, 20, 20, 255)}  # RGBA, the same coral as the pet


def png(n):
    k = max(1, n // 16)
    ox, oy = (n - 14 * k) // 2, (n - len(GRID) * k) // 2
    raw = bytearray()
    for j in range(n):
        raw.append(0)  # no PNG filter on this row
        for i in range(n):
            gx, gy = i - ox, j - oy
            inside = 0 <= gx < 14 * k and 0 <= gy < len(GRID) * k
            raw += bytes(COL.get(GRID[gy // k][gx // k], (0, 0, 0, 0)) if inside else (0, 0, 0, 0))

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    head = struct.pack(">IIBBBBB", n, n, 8, 6, 0, 0, 0)  # 8-bit RGBA
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", head) + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b"")


out = sys.argv[1] if len(sys.argv) > 1 else "AppIcon.iconset"
os.makedirs(out, exist_ok=True)
for s in (16, 32, 128, 256, 512):
    open(os.path.join(out, "icon_%dx%d.png" % (s, s)), "wb").write(png(s))
    open(os.path.join(out, "icon_%dx%d@2x.png" % (s, s)), "wb").write(png(s * 2))
