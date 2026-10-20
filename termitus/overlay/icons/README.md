# TER icons

Brand mark: the bold yellow «T» (wide top bar tapering to a point) with a soft warm glow on a
near-black background, the same as the web app's apple-touch-icon and the Telegram bot avatar
(`termitus-platform/static/icons/`). The T is a vector polygon traced from `icon-512.png`
(coordinates at the top of `gen_icon.py`); every size is rasterised from the vector.

Regenerate everything (pure python 3, no Pillow, works on any OS):

```bash
python3 termitus/overlay/icons/gen_icon.py              # writes into this folder
python3 termitus/overlay/icons/gen_icon.py --preview /tmp/prev   # + PNG previews
```

Outputs: `v2rayN.icns` (macOS squircle with margin, 16..1024 incl. @2x), `v2rayN.png` (512),
`v2rayN.ico` (Windows, 16/24/32/48/64/128/256), `NotifyIcon1..4.ico` (Windows tray:
1 = off, grey T; 2 = on, yellow T + mint dot; 3 and 4 repeat 1 and 2), and
`../v2rayN.Desktop/Assets/ter-logo.png` (in-app logo, Home header and tray popover).
The macOS menu-bar glyph is drawn in code (`TermitusTrayIcon` in `Common/TermitusTray.cs`,
same polygon; template image when off). Brand assets are «all rights reserved» (see
GPL-COMPLIANCE.md / TRADEMARKS.md).
