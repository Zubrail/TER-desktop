# dmgbuild settings for the TER installer window (no Finder/AppleScript needed:
# dmgbuild writes the .DS_Store itself, so it also works headless / in CI).
# Used by package-ter-osx.sh:  dmgbuild -s this.py -D app=<TER.app> -D bg=<tiff> -D icon=<icns> TER out.dmg
import os.path

app = defines["app"]  # noqa: F821  (injected by dmgbuild)
appname = os.path.basename(app)

format = "UDZO"
filesystem = "HFS+"
files = [app]
symlinks = {"Applications": "/Applications"}
icon = defines.get("icon")  # noqa: F821  volume icon
hide_extensions = [appname]

background = defines["bg"]  # noqa: F821  retina tiff (600x400 @1x/@2x)
window_rect = ((200, 120), (600, 400))
default_view = "icon-view"
show_status_bar = False
show_tab_view = False
show_toolbar = False
show_pathbar = False
show_sidebar = False
icon_size = 128
text_size = 13
icon_locations = {
    appname: (150, 190),
    "Applications": (450, 190),
}
