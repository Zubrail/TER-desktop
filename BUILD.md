# BUILD.md — собрать и запустить TeRMiTuS Desktop на MacBook (Apple Silicon)

Основа: `v2rayN` 7.25.4, проект `v2rayN.Desktop` (Avalonia), **.NET 10**.
Всё проверено по исходникам upstream на коммите `3666fc5` (2026-10-02).

Словарик: **SDK** — набор для сборки .NET; **publish** — компиляция в готовые файлы;
**runtime identifier (RID)** — для какой платформы собираем (`osx-arm64` = Apple Silicon);
**self-contained** — .NET встроен в приложение, у пользователя ничего ставить не нужно;
**.app** — папка-приложение macOS; **Gatekeeper** — защита macOS, пускающая только
подписанные приложения; **notarization** — проверка Apple, снимающая предупреждение.

---

## 0. Что нужно один раз поставить (на самом Mac)

```bash
# Homebrew (если ещё нет)
/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"

# .NET 10 SDK (Apple Silicon)
brew install --cask dotnet-sdk          # или: dotnet-sdk@10, если по умолчанию другой
dotnet --version                        # ожидаем 10.x

# Утилиты упаковки .app/.dmg (их использует package-osx.sh)
brew install p7zip wget create-dmg
```

Если `brew install --cask dotnet-sdk` ставит не ту мажорную версию — скачать .NET 10 SDK
(arm64, .pkg) со страницы https://dotnet.microsoft.com/download/dotnet/10.0 и поставить.

---

## 1. Получить код и применить ребрендинг

```bash
# Наш форк (публичный, см. GPL-COMPLIANCE.md). Для локальной сборки годится и копия upstream:
git clone https://github.com/Zubrail/TER-desktop.git
cd TER-desktop

# Склонировать подмодуль GlobalHotKeys (пустой после обычного clone; нужен решению):
git submodule update --init --recursive

# Применить брендинг TeRMiTuS + фичи v1 к исходникам. Идемпотентно (делает *.bak).
bash apply-changes.sh ./upstream       # путь к склонированному v2rayN
```

**Что делает `apply-changes.sh` в этой версии (v1):**
1–4. Ребрендинг: `AppName`/`AutoRunName`/папка данных → `TeRMiTuS`; строки `v2rayN`→`TeRMiTuS`
   в `ResUI*.resx` и `Views/*.axaml`; `package-osx.sh` — bundle id `online.termitus.desktop`,
   имя `.app` = `TeRMiTuS.app` (исполняемый файл остаётся `v2rayN` = `AssemblyName`).
5. **Иконки:** ставит готовые `termitus/overlay/icons/*` (сгенерированы из `d_icon_512.svg`):
   `v2rayN.icns` (16–1024, retina), `v2rayN.png`, `Assets/v2rayN.ico` (Windows),
   перекрашенные `Assets/NotifyIcon1..4.ico` (серый=выкл, мятный=подключено).
   Если готовых нет, но есть `rsvg-convert`+`iconutil` — генерирует на месте.
6. **Фичи:** копирует в `upstream` новый упрощённый экран D и логику:
   `ServiceLib/ViewModels/TermitusHomeViewModel.cs`, `ServiceLib/Services/TermitusLocale.cs`
   и `TermitusSecurity.cs`, `v2rayN.Desktop/Views/TermitusHomeView.axaml(.cs)`,
   `v2rayN.Desktop/Assets/TermitusTheme.axaml`.
7. **Интеграция** (точечные идемпотентные правки): `App.axaml` подключает тему TeRMiTuS;
   `SimpleViewLocator.cs` регистрирует домашний экран; `MainWindow.axaml(.cs)` показывает
   домашний экран-оверлей по умолчанию и кнопку «Главная»/«Для опытных» для переключения
   на полный интерфейс v2rayN.

Источники всех фич лежат в репозитории форка под `termitus/overlay/` (это и есть наш
публикуемый по GPL дельта-код); `apply-changes.sh` раскладывает их на чистый clone v2rayN.
Полный список изменённых файлов — в `CHANGES.md`.

Папка решения: `upstream/v2rayN/v2rayN.sln`. Нас интересует проект
`upstream/v2rayN/v2rayN.Desktop` (кросс-платформенный Avalonia; проект `v2rayN/v2rayN`
— это Windows-only WPF, его на Mac НЕ собираем).

---

## 2. Restore + Build (проверка, что всё компилируется)

```bash
cd upstream/v2rayN
dotnet restore v2rayN.Desktop/v2rayN.Desktop.csproj
dotnet build   v2rayN.Desktop/v2rayN.Desktop.csproj -c Release
```

`restore` подтянет Avalonia, ReactiveUI, Semi.Avalonia и пр. (объявлены в
`Directory.Packages.props`). Если `restore` падает по сети — повторить; NuGet кеширует.

---

## 3. Publish под Apple Silicon

```bash
cd upstream/v2rayN        # тут лежит v2rayN.sln

OUT=bin/termitus/osx-arm64
dotnet publish v2rayN.Desktop/v2rayN.Desktop.csproj \
  -c Release \
  -r osx-arm64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o "$OUT"
```

Для Intel-Mac — повторить с `-r osx-x64` (и, при желании, склеить в universal через
`lipo`, но для своего MacBook достаточно `osx-arm64`).

На выходе в `$OUT` — исполняемый `v2rayN` (имя из `AssemblyName`; после ребрендинга при
желании `TeRMiTuS`, см. BRANDING.md), `*.icns`, `*.png` и зависимости.

---

## 4. Положить ядра (Xray + sing-box) и собрать .app + .dmg

v2rayN не хранит ядра в репозитории — они скачиваются из
`2dust/v2rayN-core-bin`. Это делает штатный `package-osx.sh` (лежит в корне upstream).
Он: качает архив ядер под нужную арку, кладёт их рядом с бинарником, формирует
`*.app/Contents` (Info.plist, иконка) и делает `.dmg` через `create-dmg`.

```bash
cd upstream                      # где лежит package-osx.sh
bash package-osx.sh osx-arm64 "v2rayN/bin/termitus/osx-arm64" "7.25.4-termitus"
#                    ^RID        ^путь к publish-выходу из шага 3   ^строка версии
```

Результат: `v2rayN-Package-osx-arm64/<Имя>.app` и `v2rayN-osx-arm64.dmg`.
> Проверить, что ядро Xray в архиве **≥ 26.1.23** (нужно для пиннинга из нашей подписки).
> Если старше — заменить `xray` в `Contents/MacOS` на свежий релиз XTLS/Xray-core
> (osx-arm64) перед упаковкой. Эту проверку автоматизируем в CI позже.

**Ребрендинг .app** (имя, bundle id, иконка): в `package-osx.sh` правятся поля `Info.plist`
и имя `.app`/`.icns` — это делает `apply-changes.sh` (см. BRANDING.md). После него
`.app` называется `TeRMiTuS.app`, bundle id `online.termitus.desktop`.

---

## 5. Запуск БЕЗ подписи (локально, на своём Mac)

Неподписанное приложение Gatekeeper по умолчанию блокирует («приложение повреждено»/
«не удалось проверить разработчика»). Варианты запуска своей сборки:

```bash
# Снять карантин с собранного .app (самый чистый способ для своей сборки):
xattr -dr com.apple.quarantine "v2rayN-Package-osx-arm64/TeRMiTuS.app"
open "v2rayN-Package-osx-arm64/TeRMiTuS.app"
```

Либо вручную: Finder → правый клик по `.app` → **Открыть** → в диалоге ещё раз «Открыть».
Либо: Системные настройки → «Конфиденциальность и безопасность» → внизу «Всё равно открыть».

**TUN-режим (весь трафик Mac через VPN) требует прав администратора.** v2rayN создаёт
tun-интерфейс и правит маршруты/DNS — при включении запросит пароль (`sudo`). Это
нормально и НЕ требует Apple Network Extension / entitlement, поэтому правило App Store
5.4 здесь не действует (мы не публикуемся в App Store). Если TUN не нужен — работает
режим системного прокси без админ-прав.

---

## 6. Подпись и нотаризация (для раздачи другим — нужен Apple Developer $99/год)

Нужно: аккаунт **Apple Developer Program ($99/год**, физлицу можно; оплата — зарубежной
картой, см. `DECISIONS-desktop.md`), сертификат **Developer ID Application** в Keychain,
app-specific password или App Store Connect API key для нотаризации.

```bash
APP="v2rayN-Package-osx-arm64/TeRMiTuS.app"
IDENT="Developer ID Application: ИМЯ (TEAMID)"

# 6.1 Подписать с hardened runtime (TUN-клиенту нужны некоторые исключения)
codesign --deep --force --options runtime --timestamp \
  --entitlements termitus.entitlements \
  -s "$IDENT" "$APP"
codesign --verify --deep --strict --verbose=2 "$APP"

# 6.2 Собрать .dmg (шаг 4 уже делает; либо заново create-dmg) и нотаризовать
xcrun notarytool submit "TeRMiTuS-osx-arm64.dmg" \
  --apple-id "<apple-id>" --team-id "<TEAMID>" --password "<app-specific-pass>" \
  --wait

# 6.3 Пристегнуть штамп нотаризации к .dmg (чтобы проверялось офлайн)
xcrun stapler staple "TeRMiTuS-osx-arm64.dmg"
spctl -a -t open --context context:primary-signature -v "TeRMiTuS-osx-arm64.dmg"
```

Минимальный `termitus.entitlements` (создать в корне; TUN/клиенту обычно хватает):
```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>com.apple.security.cs.allow-jit</key><true/>
  <key>com.apple.security.cs.allow-unsigned-executable-memory</key><true/>
  <key>com.apple.security.cs.disable-library-validation</key><true/>
</dict></plist>
```
(нужно, потому что бандлятся сторонние бинарники ядер xray/sing-box, не подписанные нашим ID.)

После нотаризации `.dmg` открывается двойным кликом без предупреждений. Всё это
выносится в GitHub Actions (секреты: сертификат .p12, пароль, API key) — но первый раз
удобнее прогнать руками на Mac, чтобы понять шаги.

---

## 7. Частые проблемы
- `dotnet: command not found` → не поставился SDK или не в PATH (перелогиниться / `brew link`).
- `restore` тянет не ту версию .NET → проверить, что активна 10.x: `dotnet --list-sdks`.
- `.app` не запускается, «повреждено» → шаг 5 (`xattr -dr com.apple.quarantine`).
- TUN не поднимается → запускать с правами админа; на sing-box 1.14.0 была регрессия TUN
  на macOS (upstream issue #10108) — при проблеме зафиксировать версию ядра на рабочую.
- Нет ядер в .app → не отработал `package-osx.sh` (нет `wget`/`7z`/сети).

---

## 8. TER 0.1.1+: фактическая сборка на Mac

```bash
# версия — в файле VERSION (одна точка правды)
bash apply-changes.sh ./upstream
cd upstream/v2rayN && dotnet test ServiceLib.Tests && \
  dotnet publish v2rayN.Desktop/v2rayN.Desktop.csproj -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -o bin/termitus/osx-arm64
cd ../.. && bash package-ter-osx.sh            # -> dist/TER-<VERSION>-osx-arm64.dmg (+.sha256)
```
Нужно один раз: ядра в `cores/x` (Xray) и `cores/s/*` (sing-box); geo-файлы runetfreedom в `cores/rf`
(`geoip.dat`, `geosite.dat`, их `.sha256sum` и `RF_RELEASE` = тег релиза
https://github.com/runetfreedom/russia-v2ray-rules-dat/releases); dmgbuild:
`python3 -m venv .tools/venv && .tools/venv/bin/pip install dmgbuild`. Фон окна .dmg:
`swift termitus/dmg/gen_dmg_background.swift termitus/dmg && tiffutil -cathidpicheck termitus/dmg/dmg-background.png termitus/dmg/dmg-background@2x.png -out termitus/dmg/dmg-background.tiff`.
dotnet build/test/publish внутри песочницы Claude Code зависают — запускать вне её.

## Если приложение «не открывается»
v2rayN/TER пускает только один экземпляр: если старая копия висит скрытой (например, запущена прямо из
смонтированного dmg `/Volumes/TER`), новая при запуске сразу закрывается — в DiagnosticReports будет
`v2rayN-*.ips` с SIGABRT. Проверка: `ps aux | grep -i v2rayN`. Лечение: закрыть старую (иконка в трее → Выход
или `kill <pid>`), отмонтировать dmg, запускать только из /Applications.
**С 0.3:** второй запуск выводит окно уже работающего TER на передний план; если тот не отвечает
(TER ≤ 0.2, зависший процесс или обычный v2rayN — общий mutex), показывается окно «TER уже запущен» с
инструкцией вместо молчаливого выхода (SECURITY.md, раздел «0.3»).

## Тесты (0.3)
```bash
cd upstream/v2rayN
PATH=$HOME/.dotnet:$PATH dotnet build ServiceLib.Tests/ServiceLib.Tests.csproj -c Debug
TESTINGPLATFORM_TELEMETRY_OPTOUT=1 DOTNET_ROOT=$HOME/.dotnet ./ServiceLib.Tests/bin/Debug/net10.0/ServiceLib.Tests
# только тесты TER:
DOTNET_ROOT=$HOME/.dotnet ./ServiceLib.Tests/bin/Debug/net10.0/ServiceLib.Tests --treenode-filter "/*/*/Termitus*/*"
```
Проверка компиляции под Windows с Mac: `dotnet build v2rayN.Desktop/v2rayN.Desktop.csproj -c Release -r win-x64 --self-contained false -o "$TMPDIR/winbuild"`
(WPF-проект `v2rayN/v2rayN.csproj` на Mac не собирается — нет пакета Microsoft.WindowsDesktop.App для osx).

## Живая проверка сборки рядом с работающим TER (0.4.1)
Сборка `bin/Release/net10.0` портативная: данные лежат рядом с бинарником. Скопировать её в отдельную папку и
запустить как **тестовый экземпляр** — он не мешает установленному TER (свой mutex/канал, системный прокси и TUN
не трогает, свои порты 31808+, ссылки не открывает):
```bash
SRC=upstream/v2rayN/v2rayN.Desktop/bin/Release/net10.0; D=$TMPDIR/ter-live
rm -rf "$D" && cp -c -R "$SRC" "$D" && rm -rf "$D"/gui* "$D"/binConfigs
DOTNET_ROOT=$HOME/.dotnet TER_TEST_INSTANCE=live TER_TEST_NO_OPEN=1 TER_PERF=1 \
  TER_TEST_TOUR="wait:3000,dump:start,shot:start,pages:2,login,wait:15000,dump:login,quit" "$D/v2rayN"
grep "TER " "$D"/guiLogs/*.txt; open "$D"/guiLogs/ter-shot-start.png
```
Шаги тура и переменные — `TermitusSelfTest.cs` / `TermitusTestHooks.cs`. `TER_TEST_API_BASE=https://10.255.255.1`
— проверить «нет связи» при входе; `TER_TEST_IMPORT_URL` + шаг `import` — импорт подписки (можно с локального
фейкового сервера). Тур работает только при `TER_TEST_INSTANCE` (в нём есть «Выйти»). Никогда не запускать тур на
`/Applications/TER.app` и не завершать процессы по имени `v2rayN` (убьёте установленный TER) — только по PID.

Профилирование фриза: `TER_PERF=1` пишет `ui-blocked … | jit … gc … | marks …`; глубже —
`dotnet tool install dotnet-trace --tool-path ./tools` и
`./tools/dotnet-trace collect -p <PID> --duration 00:00:00:10 --format Speedscope`.

## 0.6: ключ обновлений, URL исходников, упаковка, живая проверка
```bash
# ОДИН РАЗ (до первого релиза): ключ подписи обновлений. Приватный — вне репозитория (~/.ter-release/, 0600), сделать копию офлайн.
python3 termitus/tools/sign_release.py keygen --patch-app termitus/overlay/ServiceLib/Services/TermitusUpdate.cs
# публичный ключ попал в TermitusUpdateKey.PublicKeyBase64 -> закоммитить это изменение (публичный ключ секретом не является)
echo "https://github.com/<аккаунт>/<репозиторий-исходников>" > termitus/SOURCE_URL   # см. GPL-COMPLIANCE.md; тоже в коммит
bash apply-changes.sh ./upstream && (cd upstream/v2rayN && dotnet publish ... как в п.8) && bash package-ter-osx.sh
```
`package-ter-osx.sh` НЕ соберёт релиз без вшитого ключа и с заглушкой `REPLACE-ME` в `SOURCE_URL` (для тестовых сборок:
`ALLOW_NO_UPDATE_KEY=1 ALLOW_PLACEHOLDER_SOURCE=1 bash package-ter-osx.sh` — такие сборки раздавать нельзя). В .app кладутся
`ter-source-url.txt` и `THIRD-PARTY-NOTICES.txt` (лицензии + версии компонентов).
Выпуск обновления для пользователей (подпись, загрузка, проверка) — `termitus-platform/docs/RUNBOOK.md` «Выпуск обновления TER».

Живая проверка 0.6 против фейкового бэкенда (ничего не ставится в /Applications, системный прокси тест-экземпляр не трогает):
```bash
# сообщение о проблеме + проверка подключения с имитацией «UDP режется» (TER_TEST_PROBE: подстрока имени или tcp/udp = мс, -1 = нет ответа)
termitus/tools/live_fake.sh "wait:3000,import,wait:12000,check,dump2:c,report:connect,report-text:текст,shot:report,report-send,dump2:sent,quit" \
    TER_TEST_PROBE="udp=-1,tcp=150" POST_LOG=$TMPDIR/posts.log
#   SUPPORT_FAIL_FIRST=1 — первая отправка 503 (черновик, «Повторить»); SHUTDOWN_AFTER=22 — сервер пропадает (офлайн)
# обновления: подписанный набор + фейковый сервер + тест-экземпляр
python3 termitus/tools/make_update_fixture.py $TMPDIR/updfix --variant valid     # valid|tampered|same|badhash; печатает APP_DIR и TER_TEST_UPDATE_*
termitus/tools/live_fake.sh "wait:3000,import,wait:8000,update-check,dump2:u,update-now,wait:12000,quit" \
    APP_DIR=... TER_TEST_UPDATE_URL=... TER_TEST_UPDATE_PUBKEY=... TER_TEST_UPDATE_TARGET=... REQ_LOG=$TMPDIR/req.log
```
Шаги тура и переменные — шапка `TermitusSelfTest.cs` и `TermitusTestHooks.cs`.
