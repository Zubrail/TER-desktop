# TeRMiTuS иконки

Сгенерированы из `app_spec/mockups/d_icon_512.svg` (направление D: скруглённый квадрат
«янтарь→коралл», белый купол-термитник, арка-тоннель, тёплая точка, две искры).

В среде подготовки не было SVG-растеризатора (rsvg-convert/inkscape/cairosvg) и сети,
поэтому иконки отрисованы напрямую примитивами **Pillow** (`gen_icon.py`) — это
воспроизводимо и даёт пиксель-точный результат. На Mac владельца можно перегенерировать:

```bash
python3 -m pip install Pillow            # если нет
# набор .iconset + .icns:
mkdir -p /tmp/t.iconset
for s in 16 32 128 256 512; do
  python3 gen_icon.py $s       /tmp/t.iconset/icon_${s}x${s}.png
  python3 gen_icon.py $((s*2)) /tmp/t.iconset/icon_${s}x${s}@2x.png
done
iconutil -c icns /tmp/t.iconset -o v2rayN.icns
python3 gen_icon.py 512 v2rayN.png
# Windows .ico и трей:
python3 - <<'PY'
from gen_icon import render
sz=[16,20,24,32,48,64,128,256]; i=[render(s) for s in sz]
i[-1].save("v2rayN.ico", sizes=[(s,s) for s in sz], append_images=i[:-1])
PY
python3 gen_tray.py                      # NotifyIcon1..4.ico (серый/мятный)
```

Если на Mac есть `rsvg-convert`, можно вместо этого рендерить прямо из SVG
(см. ветку генерации в `apply-changes.sh`). Бренд-ассеты — «all rights reserved»
(см. GPL-COMPLIANCE.md: код GPL-3.0, имя и логотип — наши).
