#!/usr/bin/env bash
# apply-changes.sh — применяет ребрендинг TeRMiTuS к склонированному v2rayN.
# Идемпотентный: повторный запуск не ломает уже изменённое. Делает *.bak рядом с файлами.
#
# Использование:
#   bash apply-changes.sh [путь-к-upstream] [--rename-assembly]
# По умолчанию путь-к-upstream = ./upstream
#
# Что делает (см. BRANDING.md):
#   1) AppName / AutoRunName / папка данных  -> TeRMiTuS
#   2) строки "v2rayN" в ResUI*.resx и Views/*.axaml -> TeRMiTuS
#   3) package-osx.sh: CFBundleIdentifier/Name/DisplayName + имя .app -> бренд
#   4) (опц. --rename-assembly) AssemblyName + avares:// -> TeRMiTuS
#   5) (если есть утилиты) генерирует иконки из app_spec/mockups/d_icon_512.svg
set -euo pipefail

APP_NAME="TER"
BUNDLE_ID="online.termitus.desktop"
# Internal data folder name (only used in the rarely-hit V2RAYN_LOCAL_APPLICATION_DATA
# code path of Utils.StartupPath; the .app build stores data via AppName-independent
# LocalApplicationData/TeRMiTuS). Kept as is so existing installs keep their profiles.
DATA_DIR="TeRMiTuS"
RENAME_ASSEMBLY=0
SELF_DIR="$(cd "$(dirname "$0")" && pwd)"
# Single source of truth for the TER version (window title, Info.plist, dmg name).
TER_VERSION="$(tr -d ' \n' < "$SELF_DIR/VERSION" 2>/dev/null || true)"
[ -n "$TER_VERSION" ] || { echo "ОШИБКА: нет файла VERSION рядом с apply-changes.sh"; exit 1; }

UP="${1:-./upstream}"
[ "${2:-}" = "--rename-assembly" ] && RENAME_ASSEMBLY=1
[ "${1:-}" = "--rename-assembly" ] && { RENAME_ASSEMBLY=1; UP="./upstream"; }

ROOT="$UP/v2rayN"
[ -d "$ROOT" ] || { echo "ОШИБКА: не найден $ROOT — укажи путь к upstream v2rayN"; exit 1; }

note() { printf '  • %s\n' "$*"; }
# sed in-place, портируемо для macOS (BSD) и Linux (GNU)
sedi() { if sed --version >/dev/null 2>&1; then sed -i "$@"; else sed -i '' "$@"; fi; }
backup() { [ -f "$1.bak" ] || cp "$1" "$1.bak"; }

echo "==> 1/5 AppName, AutoRunName, папка данных"
G="$ROOT/ServiceLib/Global.cs"
U="$ROOT/ServiceLib/Common/Utils.cs"
backup "$G"; backup "$U"
# perl, not sed: works whatever the previous brand value was (v2rayN, TeRMiTuS, ...)
perl -pi -e "s/(public const string AppName = )\"[^\"]*\";/\\1\"$APP_NAME\";/" "$G"
perl -pi -e "s/(public const string AutoRunName = )\"[^\"]*\";/\\1\"${APP_NAME}AutoRun\";/" "$G"
# папка данных в Utils.cs: ...SpecialFolder.LocalApplicationData), "v2rayN")
sedi "s/LocalApplicationData), \"v2rayN\")/LocalApplicationData), \"$DATA_DIR\")/" "$U"
note "Global.cs AppName=$APP_NAME ; Utils.cs data dir=$DATA_DIR"

echo "==> 2/5 строки интерфейса (ResUI*.resx, Views/*.axaml)"
# Только ЗНАЧЕНИЯ строк: заменяем целое слово v2rayN -> TeRMiTuS. Ключи ресурсов (name="...")
# не содержат слова v2rayN как значения, поэтому замена слова безопасна. Делаем бэкапы.
# perl (BSD sed on macOS has no \b -- in 0.1.0 this step silently replaced nothing).
# Only inside <value>...</value> / Title="..."; never next to '/' or a word char, so
# URLs like github.com/2dust/v2rayN/discussions and avares://v2rayN stay intact.
find "$ROOT/ServiceLib/Resx" -name 'ResUI*.resx' -print0 | while IFS= read -r -d '' f; do
  backup "$f"
  BRAND_NAME="$APP_NAME" perl -pi -e 's{(<value>)(.*?)(</value>)}{my ($x,$y,$z)=($1,$2,$3); $y =~ s/(?<![\w\/.:])(?:v2rayN|TeRMiTuS)(?![\w\/])/$ENV{BRAND_NAME}/g; $x.$y.$z}ge' "$f"
done
find "$ROOT/v2rayN.Desktop/Views" -name '*.axaml' -print0 | while IFS= read -r -d '' f; do
  if grep -q 'Title="v2rayN"' "$f"; then backup "$f"; perl -pi -e "s/Title=\"v2rayN\"/Title=\"$APP_NAME\"/g" "$f"; fi
done
# App.axaml Name= is what macOS shows in the app menu (About/Hide/Quit <Name>).
AXN="$ROOT/v2rayN.Desktop/App.axaml"
[ -f "$AXN" ] && { backup "$AXN"; perl -pi -e "s/^(\s*)Name=\"[^\"]*\"/\\1Name=\"$APP_NAME\"/" "$AXN"; }
note "строки v2rayN -> $APP_NAME в resx и axaml"

echo "==> 3/5 macOS .app (package-osx.sh)"
P="$UP/package-osx.sh"
if [ -f "$P" ]; then
  backup "$P"
  sedi "s#<string>2dust.v2rayN</string>#<string>$BUNDLE_ID</string>#" "$P"
  # CFBundleName / CFBundleDisplayName значения v2rayN -> бренд (но НЕ CFBundleExecutable)
  sedi "s#<key>CFBundleName</key>\n  <string>v2rayN</string>#<key>CFBundleName</key>\n  <string>$APP_NAME</string>#" "$P" 2>/dev/null || true
  # построчная замена отображаемых имён (CFBundleExecutable оставляем = v2rayN)
  perl -0777 -pi -e "s{(<key>CFBundleDisplayName</key>\s*<string>)v2rayN(</string>)}{\${1}$APP_NAME\${2}}g" "$P"
  perl -0777 -pi -e "s{(<key>CFBundleName</key>\s*<string>)v2rayN(</string>)}{\${1}$APP_NAME\${2}}g" "$P"
  # имя .app и папки пакета: v2rayN.app -> TeRMiTuS.app  (CFBundleExecutable строкой ниже не трогаем)
  sedi "s#v2rayN\.app#$APP_NAME.app#g" "$P"
  note "package-osx.sh: bundle id=$BUNDLE_ID, .app=$APP_NAME.app (executable=v2rayN)"
else
  note "package-osx.sh не найден — пропуск (не macOS-сборка?)"
fi

echo "==> 4/5 AssemblyName"
if [ "$RENAME_ASSEMBLY" = "1" ]; then
  CS="$ROOT/v2rayN.Desktop/v2rayN.Desktop.csproj"
  backup "$CS"
  sedi "s#<AssemblyName>v2rayN</AssemblyName>#<AssemblyName>$APP_NAME</AssemblyName>#" "$CS"
  backup "$G"; sedi "s#avares://v2rayN/Assets/#avares://$APP_NAME/Assets/#" "$G"
  # прочие avares://v2rayN/ в axaml
  find "$ROOT" -name '*.axaml' -print0 | while IFS= read -r -d '' f; do
    if grep -q 'avares://v2rayN' "$f"; then backup "$f"; sedi "s#avares://v2rayN#avares://$APP_NAME#g" "$f"; fi
  done
  # исполняемый файл в package-osx.sh теперь TeRMiTuS
  [ -f "$P" ] && sedi "s#<string>v2rayN</string>#<string>$APP_NAME</string>#; s#MacOS/v2rayN#MacOS/$APP_NAME#g" "$P" || true
  note "AssemblyName=$APP_NAME + avares:// обновлены (ПРОВЕРИТЬ сборкой!)"
else
  note "AssemblyName оставлен v2rayN (безопасно). Для смены — флаг --rename-assembly"
fi

OVERLAY="$SELF_DIR/termitus/overlay"

echo "==> версия TER $TER_VERSION (заголовок окна, User-Agent, Info.plist)"
DBP="$ROOT/Directory.Build.props"
backup "$DBP"
perl -pi -e "s{<Version>[^<]*</Version>}{<Version>$TER_VERSION</Version>}" "$DBP"
# Title "TER 0.1.1" instead of upstream "v2rayN - V7.25.4 - Arm64"; UA "TER/0.1.1".
backup "$U"
perl -0777 -pi -e 's{\$"\{Global\.AppName\} - V\{GetVersionInfo\(\)\} - \{RuntimeInformation\.ProcessArchitecture\}"}{\$"{Global.AppName} {GetVersionInfo()}"}' "$U"
grep -q '$"{Global.AppName} {GetVersionInfo()}"' "$U" || { echo "ОШИБКА: не удалось пропатчить Utils.GetVersion"; exit 1; }
note "Directory.Build.props Version=$TER_VERSION ; заголовок окна = \"$APP_NAME $TER_VERSION\""

echo "==> 5/7 иконки"
# Предпочитаем заранее сгенерированные иконки TeRMiTuS (termitus/overlay/icons).
# Если их нет, но есть rsvg-convert+iconutil — генерируем из d_icon_512.svg на месте.
ICON_SRC="$OVERLAY/icons"
if [ -f "$ICON_SRC/v2rayN.icns" ]; then
  cp "$ICON_SRC/v2rayN.icns" "$ROOT/v2rayN.Desktop/v2rayN.icns"
  cp "$ICON_SRC/v2rayN.png"  "$ROOT/v2rayN.Desktop/v2rayN.png"
  [ -f "$ICON_SRC/v2rayN.ico" ] && cp "$ICON_SRC/v2rayN.ico" "$ROOT/v2rayN.Desktop/Assets/v2rayN.ico"
  for n in 1 2 3 4; do
    [ -f "$ICON_SRC/NotifyIcon$n.ico" ] && cp "$ICON_SRC/NotifyIcon$n.ico" "$ROOT/v2rayN.Desktop/Assets/NotifyIcon$n.ico"
  done
  note "иконки TeRMiTuS установлены из termitus/overlay/icons (.icns/.png/.ico/трей)"
else
  SVG="$(cd "$UP/.." 2>/dev/null && pwd)/app_spec/mockups/d_icon_512.svg"
  [ -f "$SVG" ] || SVG="./app_spec/mockups/d_icon_512.svg"
  if [ -f "$SVG" ] && command -v rsvg-convert >/dev/null 2>&1 && command -v iconutil >/dev/null 2>&1; then
    TMP="$(mktemp -d)"; ICONSET="$TMP/icon.iconset"; mkdir -p "$ICONSET"
    for s in 16 32 128 256 512; do
      rsvg-convert -w $s -h $s "$SVG" -o "$ICONSET/icon_${s}x${s}.png"
      rsvg-convert -w $((s*2)) -h $((s*2)) "$SVG" -o "$ICONSET/icon_${s}x${s}@2x.png"
    done
    iconutil -c icns "$ICONSET" -o "$ROOT/v2rayN.Desktop/v2rayN.icns"
    rsvg-convert -w 512 -h 512 "$SVG" -o "$ROOT/v2rayN.Desktop/v2rayN.png"
    note "иконки .icns/.png сгенерированы из d_icon_512.svg"
    rm -rf "$TMP"
  else
    note "пропуск иконок: нет готовых и нет утилит (rsvg-convert/iconutil). См. BRANDING.md п.5"
  fi
fi

echo "==> 6/7 исходники фич TeRMiTuS (overlay) -> upstream"
if [ -d "$OVERLAY" ]; then
  # Every file under overlay/ServiceLib and overlay/v2rayN.Desktop is OUR new file
  # (Termitus*.cs/.axaml); copy the trees so new files need no script change.
  (cd "$OVERLAY" && find ServiceLib ServiceLib.Tests v2rayN.Desktop -type f 2>/dev/null) | while IFS= read -r rel; do
    mkdir -p "$ROOT/$(dirname "$rel")"; cp "$OVERLAY/$rel" "$ROOT/$rel"
  done
  note "скопированы VM/View/сервисы/тема TER (overlay/ServiceLib, overlay/v2rayN.Desktop)"
else
  note "overlay не найден ($OVERLAY) — пропуск фич"
fi

echo "==> 7/7 точечная интеграция в существующие файлы (идемпотентно)"
# App.axaml: подключить тему TeRMiTuS
AX="$ROOT/v2rayN.Desktop/App.axaml"
if [ -f "$AX" ] && ! grep -q "TermitusTheme.axaml" "$AX"; then
  backup "$AX"
  perl -0777 -pi -e 's{(<ResourceInclude Source="Assets/GlobalResources.axaml" />)}{$1\n                <ResourceInclude Source="Assets/TermitusTheme.axaml" />}' "$AX"
  note "App.axaml: подключена TermitusTheme.axaml"
fi

# SimpleViewLocator.cs (Desktop): зарегистрировать home view
VL="$ROOT/v2rayN.Desktop/Common/SimpleViewLocator.cs"
if [ -f "$VL" ] && ! grep -q "TermitusHomeViewModel" "$VL"; then
  backup "$VL"
  perl -0777 -pi -e 's{(RegisterViewFactory<ThemeSettingViewModel, ThemeSettingView>\(\);)}{$1\n        RegisterViewFactory<TermitusHomeViewModel, TermitusHomeView>();}' "$VL"
  note "SimpleViewLocator.cs: зарегистрирован TermitusHomeView"
fi

# MainWindow.axaml: простой интерфейс TER на всё окно поверх полного (upstream) UI.
# Всегда патчим от чистого upstream (.bak), чтобы смена схемы не наслаивалась.
MWX="$ROOT/v2rayN.Desktop/Views/MainWindow.axaml"
if [ -f "$MWX" ]; then
  backup "$MWX"; cp "$MWX.bak" "$MWX"
  perl -0777 -pi -e 's{(<ContentControl x:Name="conTheme" DockPanel.Dock="Right" />)}{$1\n                <Button x:Name="btnTermHome" DockPanel.Dock="Right" Margin="4,0" Content="← Простой режим" ToolTip.Tip="Вернуться в простой интерфейс TER" />}' "$MWX"
  perl -0777 -pi -e 's{(<dialogHost:DialogHost[^>]*>)\n        <DockPanel>}{$1\n        <Grid>\n        <DockPanel x:Name="dockTermAdvanced" IsVisible="False">}' "$MWX"
  perl -0777 -pi -e 's{        </DockPanel>\n    </dialogHost:DialogHost>}{        </DockPanel>\n        <ContentControl x:Name="contentTermitusHome" />\n        </Grid>\n    </dialogHost:DialogHost>}' "$MWX"
  grep -q 'dockTermAdvanced' "$MWX" && grep -q 'contentTermitusHome' "$MWX" || { echo "ОШИБКА: MainWindow.axaml не пропатчен"; exit 1; }
  note "MainWindow.axaml: простой интерфейс на всё окно, полный — по «Для опытных»"
fi

# MainWindow.axaml.cs: создать home VM, переключать простой/полный интерфейс
MWC="$ROOT/v2rayN.Desktop/Views/MainWindow.axaml.cs"
if [ -f "$MWC" ]; then
  backup "$MWC"; cp "$MWC.bak" "$MWC"
  perl -0777 -pi -e 's{(private bool _blCloseByUser = false;)}{$1\n    private TermitusHomeViewModel\? _termitusHomeVm;}' "$MWC"
  perl -0777 -pi -e 's{(menuClose\.Click \+= MenuClose_Click;)}{$1\n        btnTermHome.Click += (_, _) => \{ contentTermitusHome.IsVisible = true; dockTermAdvanced.IsVisible = false; _ = _termitusHomeVm?.RefreshStateAsync(); \};\n        menuPromotion.IsVisible = false; // TER: hide upstream third-party promotion link}' "$MWC"
  perl -0777 -pi -e 's{(this\.OneWayBind\(ViewModel, vm => vm\.StatusBarViewModel, v => v\.contentStatusBarView\.Content\)\.DisposeWith\(disposables\);)}{$1\n\n            _termitusHomeVm = new TermitusHomeViewModel(ViewModel);\n            contentTermitusHome.Content = _termitusHomeVm;\n            _termitusHomeVm.ShowAdvancedRequested\n                .AsObservable()\n                .ObserveOn(RxSchedulers.MainThreadScheduler)\n                .Subscribe(_ => \{ contentTermitusHome.IsVisible = false; dockTermAdvanced.IsVisible = true; \})\n                .DisposeWith(disposables);}' "$MWC"
  [ "$(grep -c '_termitusHomeVm' "$MWC")" -ge 4 ] || { echo "ОШИБКА: MainWindow.axaml.cs не пропатчен"; exit 1; }
  note "MainWindow.axaml.cs: переключение простой/полный интерфейс"
fi

# ProfileExManager.cs: upstream real-ping runs servers in parallel and every
# result calls IndexIdEnqueue on a plain Queue<string> -> "ArgumentOutOfRange
# Array.IndexOf" races, delay results get lost (servers look dead). Make it a
# ConcurrentQueue (05.10, seen in TER 0.3.0 logs).
PEM="$ROOT/ServiceLib/Manager/ProfileExManager.cs"
if [ -f "$PEM" ]; then
  backup "$PEM"; cp "$PEM.bak" "$PEM"
  perl -0777 -pi -e 's{private readonly Queue<string> _queIndexIds = new\(\);}{private readonly ConcurrentQueue<string> _queIndexIds = new(); // TER: thread-safe, real-ping runs in parallel}' "$PEM"
  perl -0777 -pi -e 's{var id = _queIndexIds\.Dequeue\(\);}{if (!_queIndexIds.TryDequeue(out var id)) \{ break; \}}' "$PEM"
  grep -q 'ConcurrentQueue<string> _queIndexIds' "$PEM" && grep -q 'TryDequeue(out var id)' "$PEM" || { echo "ОШИБКА: ProfileExManager.cs не пропатчен"; exit 1; }
  note "ProfileExManager.cs: потокобезопасная очередь результатов пинга"
fi

# UpdateService.cs: self-update of the APP (not cores) pulled 2dust/v2rayN
# releases -- installing one would silently replace TER with stock v2rayN.
# Disabled until TER ships its own signed update channel (termitus.online).
UPS="$ROOT/ServiceLib/Services/UpdateService.cs"
if [ -f "$UPS" ] && ! grep -q "TER: app self-update disabled" "$UPS"; then
  backup "$UPS"
  perl -0777 -pi -e 's{(public async Task CheckUpdateGuiN\(bool preRelease, bool blProxy = true, CancellationToken cancellationToken = default\)\n    \{\n)}{$1        // TER: app self-update disabled -- upstream feed is 2dust/v2rayN, which would replace TER.\n        await UpdateFunc(false, "Обновления TER приходят с сайта termitus.online");\n        return;\n}' "$UPS"
  note "UpdateService.cs: самообновление приложения с 2dust/v2rayN отключено"
fi

# ConfigHandler.LoadConfig: Russian UI by default (upstream: zh for Chinese locale,
# otherwise English) + TER one-time config migrations (TermitusDefaults).
CH="$ROOT/ServiceLib/Handler/ConfigHandler.cs"
if [ -f "$CH" ] && ! grep -q "TermitusDefaults.OnConfigLoaded" "$CH"; then
  backup "$CH"
  perl -0777 -pi -e 's{(if \(config\.UiItem\.CurrentLanguage\.IsNullOrEmpty\(\)\)\n        \{\n)            config\.UiItem\.CurrentLanguage = Thread\.CurrentThread\.CurrentCulture\.TwoLetterISOLanguageName\.Equals\("zh", StringComparison\.CurrentCultureIgnoreCase\)\n                \? Global\.Languages\.First\(\)\n                : Global\.Languages\[2\];\n        \}\n}{$1            config.UiItem.CurrentLanguage = TermitusDefaults.DefaultLanguage; // TER: Russian by default\n        \}\n        TermitusDefaults.OnConfigLoaded(config);\n}' "$CH"
  grep -q "TermitusDefaults.OnConfigLoaded" "$CH" || { echo "ОШИБКА: не удалось пропатчить LoadConfig"; exit 1; }
  note "ConfigHandler.LoadConfig: язык по умолчанию ru + миграции TER"
fi

# ConfigHandler.InitBuiltinRouting: create the RU (runetfreedom RUv1) routing set
# offline instead of upstream's Chinese "V4-绕过大陆(Whitelist)" defaults.
if [ -f "$CH" ] && ! grep -q "TermitusRussiaPreset.EnsureRoutingAsync" "$CH"; then
  backup "$CH"
  perl -0777 -pi -e 's{(            items = await AppManager\.Instance\.RoutingItems\(\);\n        \}\n)(\n        if \(!blImportAdvancedRules && items\.Count\(\) > 0\))}{$1\n        // TER: Russian routing preset (offline, embedded) instead of the Chinese built-ins.\n        if (!blImportAdvancedRules && await TermitusRussiaPreset.EnsureRoutingAsync(config))\n        \{\n            items = await AppManager.Instance.RoutingItems();\n        \}\n$2}' "$CH"
  grep -q "TermitusRussiaPreset.EnsureRoutingAsync" "$CH" || { echo "ОШИБКА: не удалось пропатчить InitBuiltinRouting"; exit 1; }
  note "ConfigHandler.InitBuiltinRouting: маршрутизация RU (RUv1, «Всё, кроме РФ») по умолчанию"
fi

# CoreManager.Init: upstream copies bundled bin/ to the data dir WITHOUT overwrite,
# so an upgrade would keep geo files lacking the RU categories. Sync by marker.
CM="$ROOT/ServiceLib/Manager/CoreManager.cs"
if [ -f "$CM" ] && ! grep -q "TermitusRussiaPreset.SyncBundledGeoFiles" "$CM"; then
  backup "$CM"
  perl -0777 -pi -e 's{(                FileUtils\.CopyDirectory\(fromPath, toPath, true, false\);\n)}{$1                TermitusRussiaPreset.SyncBundledGeoFiles(fromPath, toPath); // TER\n}' "$CM"
  grep -q "TermitusRussiaPreset.SyncBundledGeoFiles" "$CM" || { echo "ОШИБКА: не удалось пропатчить CoreManager"; exit 1; }
  note "CoreManager.Init: geo-файлы из .app обновляются при смене релиза runetfreedom"
fi

# ---- TER 0.3: security & reliability (SECURITY.md #2-#4, CHANGES.md 0.3.0) ----

# CoreManager: let the TER connection guard see whether the core process is alive.
if [ -f "$CM" ] && ! grep -q "TermitusCoreAlive" "$CM"; then
  backup "$CM"
  TER_INS="$(cat <<'EOF'
    /// <summary>
    /// TER connection guard: null = no core was started, false = the main core
    /// (or its TUN pre-core) has exited, true = running.
    /// </summary>
    public bool? TermitusCoreAlive()
    {
        var main = _processService;
        if (main == null)
        {
            return null;
        }
        try
        {
            if (main.HasExited)
            {
                return false;
            }
            var pre = _processPreService;
            return pre == null || !pre.HasExited;
        }
        catch
        {
            return false;
        }
    }

EOF
)" perl -0777 -pi -e 's{\n(    #region Private\n)}{\n$ENV{TER_INS}\n\n$1}' "$CM"
  grep -q "public bool? TermitusCoreAlive()" "$CM" || { echo "ОШИБКА: не удалось пропатчить CoreManager (TermitusCoreAlive)"; exit 1; }
  note "CoreManager: TermitusCoreAlive() для авто-переподключения/kill switch"
fi

# sing-box TUN: route IPv6 into the tunnel whenever the host has a global IPv6
# address. Upstream adds the tun's IPv6 address (and so the ::/0 route) only when
# EnableIPv6Address is on (default off) -> IPv6 went around the tunnel. Same rule
# upstream already uses for the Xray TUN (autoSystemRoutingTable, HasGlobalIPv6Address).
SBI="$ROOT/ServiceLib/Services/CoreConfig/Singbox/SingboxInboundService.cs"
if [ -f "$SBI" ] && ! grep -q "TER: IPv6 leak" "$SBI"; then
  backup "$SBI"
  perl -0777 -pi -e 's{if \(_config\.TunModeItem\.EnableIPv6Address == true\)}{if (_config.TunModeItem.EnableIPv6Address == true || context.HasGlobalIPv6Address) // TER: IPv6 leak fix}' "$SBI"
  grep -q "TER: IPv6 leak" "$SBI" || { echo "ОШИБКА: не удалось пропатчить SingboxInboundService"; exit 1; }
  note "sing-box TUN: IPv6 в туннель при наличии глобального IPv6 (без утечки мимо VPN)"
fi

# Program.cs (macOS/Linux): keep the single-instance mutex referenced (a discarded
# Mutex can be finalized, releasing the lock) and, on a second launch, ask the
# running instance to show its window instead of exiting silently.
PRG="$ROOT/v2rayN.Desktop/Program.cs"
if [ -f "$PRG" ]; then
  backup "$PRG"; cp "$PRG.bak" "$PRG"
  perl -0777 -pi -e 's{(    public static EventWaitHandle ProgramStarted;\n)}{$1    private static Mutex? _termitusInstanceMutex; // TER: keep referenced\n}' "$PRG"
  perl -0777 -pi -e 's#_ = new Mutex\(true, "v2rayN", out var bOnlyOneInstance\);\n(\s*)if \(!bOnlyOneInstance\)\n(\s*)\{\n#_termitusInstanceMutex = new Mutex(true, ServiceLib.Services.TermitusTestHooks.MutexName, out var bOnlyOneInstance);\n${1}if (!bOnlyOneInstance)\n${2}\{\n${2}    TermitusInstanceSignal.OnSecondLaunch(); // TER: show the running window\n#' "$PRG"
  grep -q "TermitusInstanceSignal.OnSecondLaunch" "$PRG" && grep -q "_termitusInstanceMutex = new Mutex(true, ServiceLib.Services.TermitusTestHooks.MutexName" "$PRG" || { echo "ОШИБКА: Program.cs не пропатчен"; exit 1; }
  note "Program.cs: второй запуск показывает окно работающего TER"
fi

# MainWindow.axaml.cs (macOS/Linux): listen for "show" from a second launch.
# (MWC was restored from .bak above, so this line is re-added on every run.)
if [ -f "$MWC" ]; then
  perl -0777 -pi -e 's{(            menuAddServerViaScan\.IsVisible = false;\n)}{$1            TermitusInstanceSignal.Listen(this); // TER\n}' "$MWC"
  grep -q "TermitusInstanceSignal.Listen(this)" "$MWC" || { echo "ОШИБКА: MainWindow.axaml.cs (Listen) не пропатчен"; exit 1; }
  note "MainWindow.axaml.cs: приём сигнала «показать окно» от второго запуска"
fi

# ---- TER 0.4: account page ----

# DownloadService.DownloadStringAsync: upstream reads the subscription with
# GetStringAsync and drops the response headers. Read the response ourselves and
# hand it to TermitusAccount, which keeps Subscription-Userinfo / Profile-Title
# when (and only when) the URL is one of the user's subscriptions. No extra request.
DLS="$ROOT/ServiceLib/Services/DownloadService.cs"
if [ -f "$DLS" ]; then
  backup "$DLS"; cp "$DLS.bak" "$DLS"
  perl -0777 -pi -e 's{            return await client\.GetStringAsync\(url, linkedCts\.Token\);\n}{            // TER: keep the response headers (Subscription-Userinfo) for the account page.\n            using var terResponse = await client.GetAsync(url, linkedCts.Token);\n            terResponse.EnsureSuccessStatusCode();\n            var terBody = await terResponse.Content.ReadAsStringAsync(linkedCts.Token);\n            await TermitusAccount.OnDownloadedAsync(url, terResponse);\n            return terBody;\n}' "$DLS"
  grep -q "TermitusAccount.OnDownloadedAsync(url, terResponse)" "$DLS" || { echo "ОШИБКА: DownloadService.cs не пропатчен"; exit 1; }
  note "DownloadService: заголовок Subscription-Userinfo -> страница «Аккаунт»"
fi

# ---- TER 0.5: translucent window (blurred backdrop behind the sidebar) ----
# Window: ask for a blurred backdrop; the advanced (upstream) UI gets an opaque background of its own,
# TermitusHomeView paints the sidebar see-through (and falls back to solid when there is no blur).
if [ -f "$MWX" ]; then
  perl -0777 -pi -e 's{(    Icon="/Assets/NotifyIcon1\.ico"\n)}{$1    Background="Transparent"\n    TransparencyLevelHint="AcrylicBlur,Blur,None"\n}' "$MWX"
  perl -0777 -pi -e 's{<DockPanel x:Name="dockTermAdvanced" IsVisible="False">}{<DockPanel x:Name="dockTermAdvanced" Background="{DynamicResource TermBg}" IsVisible="False">}' "$MWX"
  grep -q 'TransparencyLevelHint="AcrylicBlur' "$MWX" && grep -q 'dockTermAdvanced" Background' "$MWX" || { echo "ОШИБКА: MainWindow.axaml (blur) не пропатчен"; exit 1; }
  note "MainWindow.axaml: полупрозрачное окно (blur) под боковой панелью"
fi

# ---- TER 0.5: macOS menu-bar item owned by TER (popover on left click, short menu on right click) ----
# Avalonia's TrayIcon opens its menu on ANY click on macOS, so on macOS the upstream TrayIcon is hidden
# and TermitusTray (NSStatusItem) takes over; Windows/Linux keep the upstream tray.
APPCS="$ROOT/v2rayN.Desktop/App.axaml.cs"
if [ -f "$APPCS" ]; then
  backup "$APPCS"; cp "$APPCS.bak" "$APPCS"
  perl -0777 -pi -e 's{(            desktop\.MainWindow = mainWindow;\n)}{$1\n            if (OperatingSystem.IsMacOS())\n            {\n                // TER: macOS menu-bar item is TermitusTray (NSStatusItem); hide the upstream TrayIcon.\n                foreach (var ti in TrayIcon.GetIcons(this) ?? [])\n                {\n                    ti.IsVisible = false;\n                }\n            }\n}' "$APPCS"
  grep -q 'ti.IsVisible = false' "$APPCS" || { echo "ОШИБКА: App.axaml.cs не пропатчен"; exit 1; }
  note "App.axaml.cs: на macOS стандартный TrayIcon скрыт (значок в строке меню — TermitusTray)"
fi
if [ -f "$MWC" ]; then
  perl -0777 -pi -e 's{(            contentTermitusHome\.Content = _termitusHomeVm;\n)}{$1            TermitusTray.Start(this, _termitusHomeVm); // TER 0.5: macOS menu-bar item\n}' "$MWC"
  grep -q 'TermitusTray.Start(this, _termitusHomeVm)' "$MWC" || { echo "ОШИБКА: MainWindow.axaml.cs (tray) не пропатчен"; exit 1; }
  note "MainWindow.axaml.cs: запуск значка в строке меню"
fi

# ---- TER 0.5: local logs are masked (tokens, proxy links, UUIDs, secrets) and kept 3 days ----
LOGF="$ROOT/ServiceLib/Common/Logging.cs"
if [ -f "$LOGF" ]; then
  backup "$LOGF"; cp "$LOGF.bak" "$LOGF"
  perl -0777 -pi -e 's{(        LogManager\.Configuration = config;\n)}{$1        TermitusLogPrivacy.PurgeOldLogs(); // TER 0.5: day logs older than 3 days are deleted\n}' "$LOGF"
  perl -0777 -pi -e 's{(_logger1\.Info\()strContent\);}{$1TermitusLogPrivacy.Mask(strContent));}' "$LOGF"
  perl -0777 -pi -e 's{_logger2\.Debug\(\$"\{strTitle\},\{ex\.Message\}"\);\n        _logger2\.Debug\(ex\.StackTrace\);\n        if \(ex\?\.InnerException != null\)\n        \{\n            _logger2\.Error\(ex\.InnerException\);\n        \}}{_logger2.Debug(TermitusLogPrivacy.Mask(\$"{strTitle},{ex.Message}"));\n        _logger2.Debug(TermitusLogPrivacy.Mask(ex.StackTrace));\n        if (ex?.InnerException != null)\n        \{\n            _logger2.Error(TermitusLogPrivacy.Mask(ex.InnerException.ToString()));\n        \}}' "$LOGF"
  grep -q 'TermitusLogPrivacy.Mask(strContent)' "$LOGF" && grep -q 'TermitusLogPrivacy.PurgeOldLogs' "$LOGF" && grep -q 'Mask(ex.StackTrace)' "$LOGF" || { echo "ОШИБКА: Logging.cs не пропатчен"; exit 1; }
  note "Logging.cs: маскирование токенов/ссылок/UUID/паролей в логах, хранение 3 дня"
fi

# ---- TER 0.5: no user-visible "v2rayN" anywhere ----
# Window title attribute (upstream default; the real title is set at runtime, but
# the first frame and the macOS window list use this one).
if [ -f "$MWX" ]; then
  perl -pi -e 's/Title="v2rayN"/Title="TER"/' "$MWX"
  note "MainWindow.axaml: Title=TER"
fi
# Help menu listed "V2rayN website" (upstream project link) next to the cores' sites.
if [ -f "$MWC" ]; then
  perl -0777 -pi -e 's{(\.Where\(t => t\.CoreType is not ECoreType\.v2fly)}{$1\n                        and not ECoreType.v2rayN // TER: no upstream project link}' "$MWC"
  grep -q "and not ECoreType.v2rayN // TER" "$MWC" || { echo "ОШИБКА: AddHelpMenuItem не пропатчен"; exit 1; }
  note "MainWindow.axaml.cs: из меню «Помощь» убрана ссылка на v2rayN"
fi
# Core-update list: the app row (v2rayN) is never offered (self-update is off).
CIM="$ROOT/ServiceLib/Manager/CoreInfoManager.cs"
if [ -f "$CIM" ]; then
  backup "$CIM"; cp "$CIM.bak" "$CIM"
  perl -0777 -pi -e 's{ECoreType\.v2rayN => !Utils\.IsPackagedInstall\(\),}{ECoreType.v2rayN => false, // TER: app self-update is off}' "$CIM"
  grep -q "ECoreType.v2rayN => false, // TER" "$CIM" || { echo "ОШИБКА: CoreInfoManager не пропатчен"; exit 1; }
  note "CoreInfoManager: строка «v2rayN» не показывается в обновлении ядер"
fi
AMG="$ROOT/ServiceLib/Manager/AppManager.cs"
if [ -f "$AMG" ]; then
  backup "$AMG"; perl -pi -e 's/\$"v2rayN start up \|/\$"TER start up |/' "$AMG"
fi

echo
echo "Готово. Проверь сборку:  cd $ROOT && dotnet build v2rayN.Desktop/v2rayN.Desktop.csproj -c Release"
echo "Откатить всё:  найди *.bak рядом с изменёнными файлами и верни их."
