# TER — VPN-клиент для macOS (и скоро Windows)

**TER** — простой клиент для сервиса TeRMiTuS: одна кнопка «Подключить», автоматический
выбор быстрого сервера, российские сайты открываются напрямую. Основан на открытом проекте
[v2rayN](https://github.com/2dust/v2rayN) (© 2dust), распространяется под **GPL-3.0**.

- Скачать готовое приложение: раздел **Releases** этого репозитория или https://termitus.online/app/
- Поддержка: Telegram-бот TeRMiTuS (кнопка «Поддержка» есть и в самом приложении).

## Что внутри репозитория

| Путь | Что это |
|---|---|
| `upstream/` | Исходный код v2rayN без изменений (коммит из `UPSTREAM_COMMIT`), вместе с подмодулем `GlobalHotKeys` |
| `termitus/overlay/` | Весь наш код: новые экраны, вход, оплата, проверка связи, обновления и тесты |
| `apply-changes.sh` | Накладывает наш код и брендинг на `upstream/` |
| `package-ter-osx.sh` | Собирает `TER.app` и `.dmg` |
| `termitus/tools/sign_release.py` | Проверка подписи обновлений (Ed25519). Приватного ключа здесь нет |
| `BUILD.md` | Подробная инструкция по сборке |

## Собрать самому (macOS, Apple Silicon)

```bash
brew install --cask dotnet-sdk        # нужен .NET 10 SDK
git clone https://github.com/Zubrail/TER-desktop.git && cd TER-desktop
bash apply-changes.sh ./upstream
cd upstream/v2rayN && dotnet build ServiceLib.Tests/ServiceLib.Tests.csproj -c Debug \
  && ./ServiceLib.Tests/bin/Debug/net10.0/ServiceLib.Tests      # тесты
cd ../.. && bash package-ter-osx.sh                                # TER.app + .dmg в dist/
```

Ядра (Xray-core, sing-box) и геофайлы скачиваются при сборке, их версии и ссылки на исходники
перечислены в `THIRD-PARTY-NOTICES.txt` внутри приложения.

## Приватность

Клиент не собирает статистику и не ведёт журнал посещённых сайтов. Журнал ядра выключен,
локальный прокси слушает только этот компьютер. В отчёт о проблеме не попадают ссылка
подписки, ключи, адреса серверов и имя пользователя компьютера (это можно проверить по коду
`TermitusDiagnostics.cs` и тестам).

## Лицензия и бренд

Код — **GPL-3.0** (см. `LICENSE`). Имя **TER / TeRMiTuS** и логотип лицензией не покрываются,
см. `TRADEMARKS.md`.

---

**English.** TER is a simple VPN client for the TeRMiTuS service, a fork of
[v2rayN](https://github.com/2dust/v2rayN) licensed under GPL-3.0. `upstream/` holds the
unmodified v2rayN source, `termitus/overlay/` holds our changes, and `apply-changes.sh` merges
them. Build steps are above. The TER/TeRMiTuS name and logo are not covered by the GPL (see `TRADEMARKS.md`).
