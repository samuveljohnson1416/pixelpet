# Writes app.ico (16/32/48/64) of the pixel pet, no dependencies.
import struct, sys

GRID = ["...YY....YY...",
        "....X....X....",
        "...XXXXXXXX...",
        "..XXXXXXXXXX..",
        "..XXEXXXXEXX..",
        "..XXEXXXXEXX..",
        "XXXXXXXXXXXXXX",
        "XXXXXXXXXXXXXX",
        "..XXXXXXXXXX..",
        "...X.X..X.X...",
        "...X.X..X.X..."]
COL = {"X": (182, 196, 46, 255), "E": (20, 20, 20, 255), "Y": (48, 216, 255, 255)}  # BGRA


def image(n):
    k = max(1, n // 14)
    rows_n = len(GRID)
    ox, oy = (n - 14 * k) // 2, (n - rows_n * k) // 2
    rows = []
    for j in range(n - 1, -1, -1):  # bottom-up
        row = b""
        for i in range(n):
            gx, gy = (i - ox) // k, (j - oy) // k
            c = GRID[gy][gx] if 0 <= gx < 14 and 0 <= gy < rows_n and i >= ox and j >= oy else "."
            row += bytes(COL.get(c, (0, 0, 0, 0)))
        rows.append(row)
    mask = b"\0" * (((n + 31) // 32) * 4) * n
    return struct.pack("<IiiHHIIiiII", 40, n, n * 2, 1, 32, 0, 0, 0, 0, 0, 0) + b"".join(rows) + mask


sizes = [16, 32, 48, 64]
blobs = [image(n) for n in sizes]
out = struct.pack("<HHH", 0, 1, len(sizes))
off = 6 + 16 * len(sizes)
for n, b in zip(sizes, blobs):
    out += struct.pack("<BBBBHHII", n, n, 0, 0, 1, 32, len(b), off)
    off += len(b)
open(sys.argv[1] if len(sys.argv) > 1 else "app.ico", "wb").write(out + b"".join(blobs))
