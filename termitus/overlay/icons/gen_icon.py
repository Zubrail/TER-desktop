#!/usr/bin/env python3
"""TER icons: the brand «T» (yellow, wide top bar tapering to a point, soft warm glow, near-black
background) - the same mark as the web app's apple-touch-icon and the Telegram bot avatar.

Pure python 3 (stdlib only: zlib/struct), no Pillow needed. The T is a vector polygon traced from
termitus-platform/static/icons/icon-512.png; every size is rasterised from the vector (analytic
anti-aliasing), so 16 px stays crisp.

  python3 gen_icon.py [--out DIR]
writes into DIR (default: this folder): v2rayN.icns (PNG entries, written directly), v2rayN.ico,
v2rayN.png (512), NotifyIcon1..4.ico, ../v2rayN.Desktop/Assets/ter-logo.png (160),
and preview PNGs when --preview DIR is given.
"""
import math, os, struct, sys, zlib

# ---- brand geometry, 512 box (measured on icon-512.png) ----
T_POLY = [(152, 163.5), (360, 163.5), (338, 216.5), (285, 216.5), (256, 353), (227, 216.5), (174, 216.5)]
T_CX, T_CY = 256, 258          # visual centre of the mark
BG = (0x0B, 0x0B, 0x0C)
YEL = (255, 214, 0)
GLOW = (255, 196, 0)

# ---------------- PNG / ICO writers ----------------
def png_bytes(w, h, rgba):
    raw = b"".join(b"\x00" + bytes(rgba[y * w * 4:(y + 1) * w * 4]) for y in range(h))
    def chunk(t, d):
        c = struct.pack(">I", len(d)) + t + d
        return c + struct.pack(">I", zlib.crc32(t + d) & 0xffffffff)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))

def ico_bytes(images):
    """images: list of (size, png_bytes) -> multi-size ICO with PNG entries."""
    head = struct.pack("<HHH", 0, 1, len(images))
    off = 6 + 16 * len(images)
    ents, blobs = b"", b""
    for s, p in images:
        ents += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(p), off + len(blobs))
        blobs += p
    return head + ents + blobs

# ---------------- rasteriser ----------------
def coverage(poly, size, tf, ss=4):
    """Per-pixel coverage (0..1) of poly (512-space) mapped by tf(x,y)->pixel. Scanline, exact in x."""
    pts = [tf(x, y) for x, y in poly]
    cov = [0.0] * (size * size)
    ys = [p[1] for p in pts]
    y0, y1 = max(0, int(math.floor(min(ys)))), min(size - 1, int(math.ceil(max(ys))))
    n = len(pts)
    for py in range(y0, y1 + 1):
        for k in range(ss):
            sy = py + (k + 0.5) / ss
            xs = []
            for i in range(n):
                (ax, ay), (bx, by) = pts[i], pts[(i + 1) % n]
                if (ay <= sy < by) or (by <= sy < ay):
                    xs.append(ax + (sy - ay) / (by - ay) * (bx - ax))
            xs.sort()
            for j in range(0, len(xs) - 1, 2):
                a, b = xs[j], xs[j + 1]
                for px in range(max(0, int(a)), min(size - 1, int(b)) + 1):
                    ov = min(b, px + 1) - max(a, px)
                    if ov > 0:
                        cov[py * size + px] += ov / ss
    return cov

def blur_grid(g, n, sigma, passes=3):
    """Cheap gaussian-ish blur: repeated box blur on an n x n grid."""
    r = max(1, int(round(sigma * math.sqrt(12.0 / passes + 1) / 2)))
    for _ in range(passes):
        for axis in (0, 1):
            out = [0.0] * (n * n)
            for a in range(n):
                acc, win = 0.0, 2 * r + 1
                line = [g[(a * n + b) if axis == 0 else (b * n + a)] for b in range(n)]
                pre = [0.0]
                for v in line: pre.append(pre[-1] + v)
                for b in range(n):
                    lo, hi = max(0, b - r), min(n, b + r + 1)
                    v = (pre[hi] - pre[lo]) / win
                    out[(a * n + b) if axis == 0 else (b * n + a)] = v
            g = out
    return g

_GLOW = {}
def glow_field():
    """Glow alpha on a 128 grid over the 512 box: blurred T, tuned to icon-512.png."""
    if "g" not in _GLOW:
        n = 128
        m = coverage(T_POLY, n, lambda x, y: (x / 4, y / 4), 4)
        b = blur_grid(m, n, 11.0)
        _GLOW["g"] = (n, b)
    return _GLOW["g"]

def glow_at(u, v):  # u,v in 512 space
    n, g = _GLOW["g"] if "g" in _GLOW else glow_field()
    x, y = u / 4 - 0.5, v / 4 - 0.5
    if x < 0 or y < 0 or x >= n - 1 or y >= n - 1:
        return 0.0
    xi, yi = int(x), int(y); fx, fy = x - xi, y - yi
    a = g[yi * n + xi]; b = g[yi * n + xi + 1]; c = g[(yi + 1) * n + xi]; d = g[(yi + 1) * n + xi + 1]
    return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy

GLOW_GAIN = 0.2   # blurred-T term on top of the radial halo (fitted to icon-512.png)

def shape_alpha(kind, size, i, j):
    """Mask of the tile at pixel (i,j): 'mac' squircle with margin, 'round' rounded square, 'full'."""
    if kind == "full":
        return 1.0
    x = (i + 0.5) / size * 2 - 1; y = (j + 0.5) / size * 2 - 1
    if kind == "mac":      # superellipse, body = 80.5% of the canvas (macOS grid: 824/1024)
        s = 0.8047; n = 5.0
        f = (abs(x / s) ** n + abs(y / s) ** n) ** (1 / n) - 1
        px = size * s / 2        # ~ pixels per unit of f
        return max(0.0, min(1.0, 0.5 - f * px))
    if kind == "round":    # rounded square, radius 20% of the side
        r = 0.4
        qx, qy = abs(x) - (1 - r), abs(y) - (1 - r)
        d = (math.hypot(max(qx, 0), max(qy, 0)) + min(max(qx, qy), 0) - r) * size / 2
        return max(0.0, min(1.0, 0.5 - d))
    raise ValueError(kind)

def render_tile(size, kind="mac", t_color=YEL, glow=1.0, boost=1.0, dot=None, bg=BG):
    """RGBA tile: background, warm glow, T. boost scales the T (optical size for tiny icons)."""
    body = 0.8047 if kind == "mac" else 1.0
    sc = size * body / 512.0
    ox = size / 2 - T_CX * sc * boost; oy = size / 2 - T_CY * sc * boost
    # keep the T optically centred in the tile
    tf = lambda x, y: (ox + x * sc * boost, oy + y * sc * boost)
    cov = coverage(T_POLY, size, tf)
    gs = glow_field()  # warm cache
    px = bytearray(size * size * 4)
    for j in range(size):
        for i in range(size):
            a = shape_alpha(kind, size, i, j)
            if a <= 0:
                continue
            # tile-space point -> 512-space point of the original artwork
            u = ((i + 0.5) - size / 2) / (sc * boost) + T_CX
            v = ((j + 0.5) - size / 2) / (sc * boost) + T_CY
            ga = min(1.0, (0.21 * math.exp(-(math.hypot(u - 256, v - 260) / 130.0) ** 2) + glow_at(u, v) * GLOW_GAIN) * glow)
            r = bg[0] + (GLOW[0] - bg[0]) * ga
            g = bg[1] + (GLOW[1] - bg[1]) * ga
            b = bg[2] + (GLOW[2] - bg[2]) * ga
            c = cov[j * size + i]
            if c > 0:
                c = min(1.0, c)
                r += (t_color[0] - r) * c; g += (t_color[1] - g) * c; b += (t_color[2] - b) * c
            o = (j * size + i) * 4
            px[o:o + 4] = bytes((int(r + .5), int(g + .5), int(b + .5), int(a * 255 + .5)))
    if dot:  # small status dot, lower-right
        rr = size * 0.17; cx = size * 0.76; cy = size * 0.76
        for j in range(max(0, int(cy - rr - 2)), min(size, int(cy + rr + 2))):
            for i in range(max(0, int(cx - rr - 2)), min(size, int(cx + rr + 2))):
                d = math.hypot(i + .5 - cx, j + .5 - cy)
                for rad, col in ((rr + size * 0.045, bg), (rr, dot)):   # dark ring, then dot
                    k = max(0.0, min(1.0, rad - d + 0.5))
                    if k > 0:
                        o = (j * size + i) * 4
                        for q in range(3):
                            px[o + q] = int(px[o + q] + (col[q] - px[o + q]) * k + .5)
                        px[o + 3] = max(px[o + 3], int(k * 255))
    return bytes(px)

def tiny_boost(size):  # optical enlargement of the mark at small sizes
    return 1.35 if size <= 32 else (1.18 if size <= 64 else 1.0)

def app_png(size, kind):
    return png_bytes(size, size, render_tile(size, kind, boost=tiny_boost(size) if kind != "mac" else (1.3 if size <= 32 else 1.0),
                                              glow=1.0 if size > 32 else 0.8))

GREY = (150, 153, 162)
MINT = (16, 185, 129)
def tray_png(size, on):
    if on:
        return png_bytes(size, size, render_tile(size, "round", boost=1.3 if size <= 32 else 1.1, glow=0.0, dot=MINT))
    return png_bytes(size, size, render_tile(size, "round", t_color=GREY, boost=1.3 if size <= 32 else 1.1, glow=0.0))

ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]

def main():
    here = os.path.dirname(os.path.abspath(__file__))
    out = here; prev = None
    a = sys.argv[1:]
    if "--out" in a: out = a[a.index("--out") + 1]
    if "--preview" in a: prev = a[a.index("--preview") + 1]; os.makedirs(prev, exist_ok=True)
    os.makedirs(out, exist_ok=True)
    w = lambda name, data: open(os.path.join(out, name), "wb").write(data)

    # macOS .icns (PNG entries written directly; no iconutil needed)
    types = [("icp4", 16), ("icp5", 32), ("ic11", 32), ("ic12", 64), ("ic07", 128), ("ic13", 256),
             ("ic08", 256), ("ic14", 512), ("ic09", 512), ("ic10", 1024)]
    cache = {}
    body = b""
    for t, px in types:
        if px not in cache: cache[px] = app_png(px, "mac")
        body += t.encode() + struct.pack(">I", len(cache[px]) + 8) + cache[px]
    w("v2rayN.icns", b"icns" + struct.pack(">I", len(body) + 8) + body)
    w("v2rayN.png", cache[512])
    if prev:
        for s in (16, 32, 128, 512):
            if s not in cache: cache[s] = app_png(s, "mac")
            open(os.path.join(prev, f"mac_{s}.png"), "wb").write(cache[s])
    # Windows .ico (rounded square, full-bleed)
    imgs = [(s, app_png(s, "round")) for s in ICO_SIZES]
    w("v2rayN.ico", ico_bytes(imgs))
    if prev:
        for s, p in imgs: open(os.path.join(prev, f"win_{s}.png"), "wb").write(p)
    # tray: 1 = off (grey T), 2 = on (yellow T + mint dot); 3/4 repeat 1/2 as before
    # (upstream: 1 = ForcedClear, 2 = ForcedChange, 3 = Automatic, 4 = PAC)
    for name, on in (("NotifyIcon1", False), ("NotifyIcon2", True), ("NotifyIcon3", False), ("NotifyIcon4", True)):
        t = [(s, tray_png(s, on)) for s in ICO_SIZES]
        w(name + ".ico", ico_bytes(t))
        if prev:
            for s, p in t:
                if s in (16, 32): open(os.path.join(prev, f"{name}_{s}.png"), "wb").write(p)
    # in-app logo (Home header 40 dip, tray popover 26 dip)
    logo = os.path.join(here, "..", "v2rayN.Desktop", "Assets", "ter-logo.png")
    if os.path.isdir(os.path.dirname(logo)):
        open(logo, "wb").write(png_bytes(160, 160, render_tile(160, "round")))
    print("done ->", out)

if __name__ == "__main__":
    main()
