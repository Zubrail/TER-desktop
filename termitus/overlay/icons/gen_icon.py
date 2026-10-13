#!/usr/bin/env python3
# Renders app_spec/mockups/d_icon_512.svg to PNG with Pillow (no SVG rasterizer available).
# Faithful reproduction of the SVG primitives (rounded-square amber->coral gradient,
# white termite-mound dome, coral arch tunnel, warm glowing dot, two sparkles).
import math
from PIL import Image, ImageDraw

def lerp(a, b, t):
    return a + (b - a) * t

def lerp_col(c1, c2, t):
    return tuple(int(round(lerp(c1[i], c2[i], t))) for i in range(len(c1)))

def cubic(p0, p1, p2, p3, n=120):
    pts = []
    for i in range(n + 1):
        t = i / n
        mt = 1 - t
        x = mt**3*p0[0] + 3*mt**2*t*p1[0] + 3*mt*t**2*p2[0] + t**3*p3[0]
        y = mt**3*p0[1] + 3*mt**2*t*p1[1] + 3*mt*t**2*p2[1] + t**3*p3[1]
        pts.append((x, y))
    return pts

def arc_top(cx, cy, r, n=90):
    # semicircle bulging upward from (cx-r,cy) to (cx+r,cy), top at (cx,cy-r)
    pts = []
    for i in range(n + 1):
        ang = math.pi + (math.pi) * (i / n)  # pi -> 2pi
        pts.append((cx + r*math.cos(ang), cy + r*math.sin(ang)))
    return pts

def render(size):
    S = size / 512.0
    def T(x, y):            # group transform translate(56,64) scale(.78)
        return ((56 + 0.78*x) * S, (64 + 0.78*y) * S)
    def P(x, y):            # plain 512-space
        return (x * S, y * S)

    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))

    # --- background: diagonal linear gradient #FFC15C -> #FF6B4A, rounded rect rx=114 ---
    c1, c2 = (0xFF, 0xC1, 0x5C), (0xFF, 0x6B, 0x4A)
    grad = Image.new("RGB", (size, size))
    gpx = grad.load()
    for y in range(size):
        for x in range(size):
            t = (x + y) / (2 * (size - 1))
            gpx[x, y] = lerp_col(c1, c2, t)
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, size-1, size-1], radius=114*S, fill=255)
    img.paste(grad, (0, 0), mask)

    d = ImageDraw.Draw(img)

    # --- white dome ---
    dome = cubic((64,400),(64,250),(150,116),(256,112)) + cubic((256,112),(362,116),(448,250),(448,400))
    dome = [T(x, y) for (x, y) in dome]
    dome.append(T(64, 400))
    d.polygon(dome, fill=(255, 255, 255, 255))

    # --- warm glow behind dot (radial #FFE9B0 -> white), soft ---
    gcx, gcy = T(256, 356)
    gr = 0.78 * 64 * S
    glow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    steps = 48
    for i in range(steps, 0, -1):
        t = i / steps
        rr = gr * t
        col = lerp_col((255, 244, 214), (255, 255, 255), 1 - t)
        a = int(180 * (1 - t) + 40)
        gd.ellipse([gcx-rr, gcy-rr, gcx+rr, gcy+rr], fill=(col[0], col[1], col[2], a))
    img.alpha_composite(glow)
    d = ImageDraw.Draw(img)

    # --- coral arch tunnel #FF7A45 ---
    arch = [T(212, 400), T(212, 330)]
    arch += [T(x, y) for (x, y) in arc_top(256, 330, 44)]
    arch += [T(300, 400)]
    d.polygon(arch, fill=(0xFF, 0x7A, 0x45, 255))

    # --- inner warm dot ---
    icx, icy = T(256, 356)
    ir = 0.78 * 16 * S
    d.ellipse([icx-ir, icy-ir, icx+ir, icy+ir], fill=(0xFF, 0xF7, 0xE6, 255))

    # --- two sparkles (512-space, outside group) ---
    for (sx, sy, sr, alpha) in [(372, 150, 10, 217), (404, 190, 6, 153)]:
        cx, cy = P(sx, sy)
        rr = sr * S
        d.ellipse([cx-rr, cy-rr, cx+rr, cy+rr], fill=(255, 255, 255, alpha))

    return img

if __name__ == "__main__":
    import sys
    size = int(sys.argv[1]) if len(sys.argv) > 1 else 1024
    out = sys.argv[2] if len(sys.argv) > 2 else "/tmp/term_icon.png"
    render(size).save(out)
    print("wrote", out, size)
