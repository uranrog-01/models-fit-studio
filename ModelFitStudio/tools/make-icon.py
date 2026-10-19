"""Generate ModelFitStudio/Assets/app.ico and app-256.png. No third-party dependencies.

Design (the Glass look): a squircle with a dusk gradient (deep blue to violet, a warm glow in the
lower right), a frosted highlight across the top, and a speed gauge: a pale track, a yellow arc
(#FFF21A, the Glass accent) for "how much of it fits and how fast", a needle and a hub.
Rendered at 4x and box-filtered down so it stays clean at 16 px.
"""
import math
import os
import struct
import zlib

SS = 4                      # supersampling factor
TOP = (0x24, 0x2E, 0x52)    # deep blue
MID = (0x4A, 0x36, 0x7A)    # violet
GLOW = (0xE0, 0x7A, 0x3C)   # warm dusk glow
ACCENT = (0xFF, 0xF2, 0x1A)
WHITE = (0xFF, 0xFF, 0xFF)

# the gauge, in unit coordinates (x right, y down); angles in degrees, 0 = right, clockwise
CX, CY = 0.5, 0.56
R = 0.30                    # radius of the arc's centre line
W = 0.075                   # half-width of the arc
START, SWEEP = 135, 270     # a 270-degree dial, open at the bottom
FILL = 0.68                 # how much of the dial is lit
NEEDLE = START + SWEEP * FILL


def squircle(x, y):
    """Inside a superellipse of exponent 5 filling the unit square (with a small margin)?"""
    nx, ny = (x - 0.5) / 0.47, (y - 0.5) / 0.47
    return abs(nx) ** 5 + abs(ny) ** 5 <= 1.0


def mix(a, b, t):
    return tuple(a[i] + (b[i] - a[i]) * t for i in range(3))


def over(base, top, alpha):
    return tuple(base[i] * (1 - alpha) + top[i] * alpha for i in range(3))


def angle_of(x, y):
    return math.degrees(math.atan2(y - CY, x - CX)) % 360


def on_arc(a, start, sweep):
    return (a - start) % 360 <= sweep


def seg_dist(px, py, ax, ay, bx, by):
    vx, vy = bx - ax, by - ay
    wx, wy = px - ax, py - ay
    L = vx * vx + vy * vy
    t = 0.0 if L == 0 else max(0.0, min(1.0, (wx * vx + wy * vy) / L))
    dx, dy = wx - t * vx, wy - t * vy
    return math.hypot(dx, dy)


def shade(u, v):
    """The colour at (u, v) in unit coordinates, or None outside the icon."""
    if not squircle(u, v):
        return None
    # dusk: blue at the top to violet, with a warm glow from the lower right corner
    c = mix(TOP, MID, min(1.0, v * 1.1))
    glow = max(0.0, 1.0 - math.hypot(u - 1.0, v - 1.05) / 0.85)
    c = over(c, GLOW, glow ** 1.6 * 0.75)
    # frosted highlight over the top third
    if v < 0.42:
        c = over(c, WHITE, 0.10 * (1 - v / 0.42))
    # the gauge
    d = math.hypot(u - CX, v - CY)
    a = angle_of(u, v)
    if abs(d - R) <= W and on_arc(a, START, SWEEP):
        lit = on_arc(a, START, SWEEP * FILL)
        c = ACCENT if lit else over(c, WHITE, 0.28)
    # round caps on both ends of the lit arc
    for ang in (START, START + SWEEP * FILL):
        ex = CX + R * math.cos(math.radians(ang))
        ey = CY + R * math.sin(math.radians(ang))
        if math.hypot(u - ex, v - ey) <= W:
            c = ACCENT
    # needle and hub
    nx = CX + (R - 0.05) * math.cos(math.radians(NEEDLE))
    ny = CY + (R - 0.05) * math.sin(math.radians(NEEDLE))
    if seg_dist(u, v, CX, CY, nx, ny) <= 0.032:
        c = WHITE
    if d <= 0.068:
        c = WHITE
    return c


def render(size):
    s = size * SS
    px = bytearray(s * s * 4)
    for y in range(s):
        for x in range(s):
            c = shade((x + 0.5) / s, (y + 0.5) / s)
            i = (y * s + x) * 4
            if c is None:
                continue
            px[i], px[i + 1], px[i + 2], px[i + 3] = int(c[0]), int(c[1]), int(c[2]), 255
    return px


def downsample(px, size):
    s = size * SS
    out = bytearray(size * size * 4)
    area = SS * SS
    for y in range(size):
        for x in range(size):
            r = g = b = a = 0
            for dy in range(SS):
                base = ((y * SS + dy) * s + x * SS) * 4
                for dx in range(SS):
                    i = base + dx * 4
                    al = px[i + 3]
                    r += px[i] * al
                    g += px[i + 1] * al
                    b += px[i + 2] * al
                    a += al
            o = (y * size + x) * 4
            if a:
                out[o], out[o + 1], out[o + 2] = r // a, g // a, b // a
            out[o + 3] = a // area
    return out


def chunk(tag, data):
    return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)


def png(size, rgba):
    raw = bytearray()
    stride = size * 4
    for y in range(size):
        raw.append(0)
        raw += rgba[y * stride:(y + 1) * stride]
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
            + chunk(b"IEND", b""))


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    assets = os.path.join(os.path.dirname(here), "Assets")
    os.makedirs(assets, exist_ok=True)

    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    images = []
    for size in sizes:
        data = png(size, downsample(render(size), size))
        images.append((size, data))
        print(f"  {size}x{size}: {len(data)} bytes")

    header = struct.pack("<HHH", 0, 1, len(images))
    offset = len(header) + 16 * len(images)
    entries, blobs = b"", b""
    for size, data in images:
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        blobs += data
        offset += len(data)

    ico = os.path.join(assets, "app.ico")
    with open(ico, "wb") as f:
        f.write(header + entries + blobs)
    print("wrote", ico, os.path.getsize(ico), "bytes")
    with open(os.path.join(assets, "app-256.png"), "wb") as f:
        f.write(images[-1][1])


if __name__ == "__main__":
    main()
