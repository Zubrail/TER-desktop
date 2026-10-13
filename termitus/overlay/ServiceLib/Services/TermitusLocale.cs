namespace ServiceLib.Services;

/// <summary>
/// TeRMiTuS UI strings for the simplified design-D home screen.
///
/// This is a lightweight i18n (internationalization) hook: it keeps all new
/// TeRMiTuS-specific texts in one place, Russian by default, with English
/// strings ready to switch on. It deliberately does NOT touch the upstream
/// ResUI.resx / ResUI.Designer.cs machinery (which would need a build to
/// regenerate); when we are ready to ship EN we can either keep this helper
/// or migrate these keys into ResUI. Bind from XAML via {Binding Loc.Xxx}
/// on the view model, so compiled bindings keep working.
/// </summary>
public sealed partial class TermitusLocale
{
    public static TermitusLocale Current { get; } = new();

    private static bool IsEn()
    {
        var lang = AppManager.Instance.Config?.UiItem?.CurrentLanguage ?? "ru";
        // Default to Russian for everything that is not explicitly an English locale.
        return lang.StartsWith("en", StringComparison.OrdinalIgnoreCase);
    }

    private static string S(string ru, string en) => IsEn() ? en : ru;

    // Greeting / first run
    public string Hello => S("Привет!", "Hi!");
    public string FirstRunTitle => S("Вставьте ссылку-подписку", "Paste your subscription link");
    public string FirstRunHint => S("Ссылку вы получили в нашем Telegram-боте", "You got the link from our Telegram bot");
    public string SubscriptionPlaceholder => S("https://termitus.online/subscription/…", "https://termitus.online/subscription/…");
    public string ImportButton => S("Подключить подписку", "Add subscription");
    public string Importing => S("Загружаем подписку…", "Importing subscription…");
    public string ImportFailed => S("Не удалось загрузить. Проверьте ссылку.", "Import failed. Check the link.");
    public string ImportNoServers => S("Подписка добавлена, но серверы не загрузились. Проверьте интернет и попробуйте ещё раз.",
                                       "Subscription added, but no servers were loaded. Check your connection and try again.");
    // First run: sign in with Telegram (0.4)
    public string FirstRunLoginTitle => S("Войдите, чтобы подключить подписку", "Sign in to add your subscription");
    public string FirstRunLoginHint => S("Вход подтверждается в нашем Telegram-боте — ссылку копировать не нужно.",
                                         "You confirm the sign-in in our Telegram bot — no link to copy.");
    public string LoginTelegram => S("Войти через Telegram", "Sign in with Telegram");
    public string HaveLink => S("У меня есть ссылка", "I have a link");
    public string LoginStarting => S("Открываем Telegram…", "Opening Telegram…");
    public string LoginWaiting => S("Подтвердите вход в Telegram…", "Confirm the sign-in in Telegram…");
    public string LoginWaitingHint => S("Мы открыли бот TER. Нажмите там «Подтвердить» — приложение подключится само.",
                                        "We opened the TER bot. Press «Confirm» there and the app connects by itself.");
    public string LoginTimeLeft(string left) => S($"Осталось {left}", $"{left} left");
    public string LoginOpenAgain => S("Открыть Telegram ещё раз", "Open Telegram again");
    public string Cancel => S("Отмена", "Cancel");
    public string LoginExpired => S("Время на вход вышло. Нажмите «Войти через Telegram» ещё раз.",
                                    "The sign-in timed out. Press «Sign in with Telegram» again.");
    public string LoginDeclined => S("Вход отклонён в Telegram.", "The sign-in was declined in Telegram.");
    public string LoginUsed => S("Этот вход уже использован. Начните заново.", "This sign-in was already used. Start again.");
    public string LoginUnavailable => S("Вход через Telegram пока недоступен — вставьте ссылку из бота.",
                                        "Sign-in with Telegram is not available yet — paste the link from the bot.");
    public string LoginOffline => NoConnection;
    public string LoginFailed => S("Не получилось войти. Попробуйте ещё раз или вставьте ссылку.",
                                   "Sign-in failed. Try again or paste the link.");
    public string LoginBackToTelegram => S("Войти через Telegram", "Sign in with Telegram");
    public string PreparingServers => S("Готовим ваши серверы… Это займёт до 2 минут.", "Preparing your servers… This takes up to 2 minutes.");
    public string LinkTitle => S("Вставьте ссылку-подписку", "Paste your subscription link");

    public string FindingBestServer => S("Ищем самый быстрый сервер…", "Finding the fastest server…");
    // 0.5 connect screen: live stats, route and protocol chips
    public string MsUnit => S("мс", "ms");
    public string StatPing => S("Пинг", "Ping");
    public string StatDown => S("Загрузка", "Download");
    public string StatUp => S("Отдача", "Upload");
    public string TipStatPing => S("Время отклика через TER. Чем меньше, тем быстрее открываются сайты", "Response time through TER. Lower is snappier");
    public string TipStatDown => S("Скорость скачивания прямо сейчас", "Current download speed");
    public string TipStatUp => S("Скорость отправки прямо сейчас", "Current upload speed");
    public string TipGraph => S("График скорости за последнюю минуту: заливка — скачивание, линия — отправка", "Speed over the last minute: filled area is download, line is upload");
    public string ConnectCaptionOff => S("Защита выключена", "Protection is off");
    public string ConnectCaptionOn => S("Защита включена. Нажмите, чтобы отключить", "Protection is on. Tap to turn it off");
    public string ConnectCaptionBusy => S("Подождите, подключаемся…", "Please wait, connecting…");
    public string RouteChipLabel => S("Маршрут", "Route");
    public string RouteChipExceptRu => S("Всё, кроме РФ", "All except Russia");
    public string RouteChipBlocked => S("Только заблокированное", "Blocked sites only");
    public string RouteChipAll => S("Всё через TER", "Everything via TER");
    public string TipRouteChip => S("Какие сайты идут через TER. Нажмите, чтобы изменить", "Which sites go through TER. Tap to change");
    public string ProtocolChipLabel => S("Протокол", "Protocol");
    public string ProtocolAutoShort => S("Авто", "Auto");
    public string ProtocolTcpShort => S("Обычный", "Standard");
    public string ProtocolUdpShort => S("Быстрый", "Fast");
    public string TipProtocolChip => S("Способ передачи данных. «Авто» подходит почти всем. Нажмите, чтобы изменить", "How data travels. Auto suits almost everyone. Tap to change");
    public string ProtocolAutoTitle => S("Авто", "Auto");
    public string ProtocolAutoHint => S("TER сам выберет самый быстрый из работающих", "TER picks the fastest one that works");
    public string ProtocolTcpTitle => S("Обычный (TCP)", "Standard (TCP)");
    public string ProtocolTcpHint => S("Самый совместимый — работает почти в любой сети", "Most compatible — works on almost any network");
    public string ProtocolUdpTitle => S("Быстрый (UDP)", "Fast (UDP)");
    public string ProtocolUdpHint => S("Лучше на слабой связи, но некоторые сети его режут", "Better on weak links, but some networks block it");
    public string ProtocolNote => S("Если выбранного вида нет или он не отвечает, TER возьмёт другой — без вашего участия", "If the chosen kind is missing or silent, TER uses another one by itself");
    // 0.5 theme
    public string SettingsAppearance => S("Оформление", "Appearance");
    public string ThemeSystemTitle => S("Системная", "System");
    public string ThemeSystemHint => S("Светлая или тёмная — как в macOS, меняется сама", "Light or dark like macOS, switches by itself");
    public string ThemeLightTitle => S("Светлая", "Light");
    public string ThemeLightHint => S("Всегда светлое оформление", "Always light");
    public string ThemeDarkTitle => S("Тёмная", "Dark");
    public string ThemeDarkHint => S("Всегда тёмное оформление", "Always dark");
    // 0.5 tray
    public string PopOn => S("Подключено", "Connected");
    public string PopOff => S("Отключено", "Disconnected");
    public string PopBusy => S("Подключаемся…", "Connecting…");
    public string PopWarn => S("Нужно внимание", "Needs attention");
    public string TrayConnect => S("Подключить", "Connect");
    public string TrayDisconnect => S("Отключить", "Disconnect");
    public string TrayCountry => S("Страна", "Country");
    public string TrayOpen => S("Открыть TER", "Open TER");
    public string TrayQuit => S("Выйти из TER", "Quit TER");
    public string TrayAdvanced => S("Для опытных", "For advanced users");
    public string TraySysProxyClear => S("Системный прокси: выключить", "System proxy: off");
    public string TraySysProxySet => S("Системный прокси: включить", "System proxy: on");
    public string TraySysProxyNothing => S("Системный прокси: не менять", "System proxy: leave as is");
    public string TrayCopyProxy => S("Скопировать команду прокси", "Copy proxy command");
    public string TrayUpdateSub => S("Обновить подписку", "Update subscription");
    public string TrayUpdateSubViaProxy => S("Обновить подписку через TER", "Update subscription via TER");
    public string TrayTooltipOn => S("TER — подключено", "TER — connected");
    public string TrayTooltipOff => S("TER — отключено", "TER — disconnected");
    public string TrayTooltipBusy => S("TER — подключаемся…", "TER — connecting…");
    public string TrayTooltipWarn => S("TER — нужно внимание", "TER — needs attention");
    public string PopSpeed => S("Скорость", "Speed");
    public string PopSubscription => S("Подписка", "Subscription");
    public string PopOpen => S("Открыть TER", "Open TER");
    public string PopQuit => S("Выйти", "Quit");
    public string TipPopOpen => S("Открыть главное окно TER", "Open the main TER window");
    public string TipPopQuit => S("Закрыть TER полностью. Защита выключится", "Quit TER completely. Protection will turn off");
    public string TipPopCountry => S("Выбрать страну подключения", "Choose the country");
    public string TipPopToggle => S("Включить или выключить защиту", "Turn protection on or off");
    // 0.5 payment sheet
    public string PayTitle => S("Оплата подписки", "Pay for subscription");
    public string PayPlansTitle => S("Тариф", "Plan");
    public string PayMethodTitle => S("Способ оплаты", "Payment method");
    public string PayPlansHint => S("Выберите, на сколько продлить", "Choose how long to renew for");
    public string PayMethodHint => S("Оплата откроется в браузере", "Payment opens in your browser");
    public string PayDays(int days) => days switch
    {
        30 => S("1 месяц", "1 month"),
        90 => S("3 месяца", "3 months"),
        180 => S("6 месяцев", "6 months"),
        365 => S("год", "1 year"),
        _ => S(days + " дн.", days + " days"),
    };
    public string PayButton => S("Оплатить", "Pay");
    public string TipPayButton => S("Откроется страница оплаты в браузере", "The payment page opens in your browser");
    public string PayAcceptPrefix => S("Нажимая «Оплатить», вы принимаете ", "By pressing \"Pay\" you accept the ");
    public string PayOfferLink => S("оферту", "offer");
    public string TipPayOffer => S("Откроется текст оферты", "Opens the offer text");
    public string PayLoadingText => S("Загружаем тарифы…", "Loading plans…");
    public string PayBotTitle => S("Оплата пока в Telegram-боте", "Payment is in the Telegram bot for now");
    public string PayBotHint => S("Оплата прямо в приложении скоро появится. Сейчас продлить подписку можно в нашем боте — это займёт минуту.", "Paying in the app is coming soon. For now you can renew in our bot, it takes a minute.");
    public string PayOpenBot => S("Открыть бота", "Open the bot");
    public string TipPayBot => S("Откроется Telegram с нашим ботом", "Opens Telegram with our bot");
    public string PayWaiting => S("Ждём оплату. Завершите её в браузере — здесь всё обновится само.", "Waiting for payment. Finish it in your browser — this updates by itself.");
    public string PayWaitingTitle => S("Ждём оплату…", "Waiting for payment…");
    public string PayCancelWait => S("Не жду", "Stop waiting");
    public string TipPayCancelWait => S("Перестать ждать (если уже оплатили, подписка обновится при следующем открытии «Аккаунта»)", "Stop waiting (if you already paid, the subscription updates next time you open Account)");
    public string PayDoneTitle => S("Оплата прошла", "Payment received");
    public string PayDoneHint => S("Спасибо! Подписка продлена, данные в «Аккаунте» обновлены.", "Thank you! Your subscription is extended and Account is updated.");
    public string PayPaidToast => S("Оплата прошла, подписка продлена", "Payment received, subscription extended");
    public string PayFailed => S("Оплата не прошла. Деньги не списаны — попробуйте другой способ или тариф.", "The payment did not go through. Nothing was charged — try another method.");
    public string PayExpired => S("Время на оплату вышло. Выберите тариф и нажмите «Оплатить» ещё раз.", "The payment window expired. Choose a plan and press Pay again.");
    public string PayTimedOut => S("Мы не дождались подтверждения оплаты. Если вы уже заплатили, подписка обновится в течение нескольких минут — загляните в «Аккаунт».", "No payment confirmation yet. If you already paid, the subscription updates within minutes — check Account.");
    public string PayErrorText => S("Не получилось. Попробуйте ещё раз чуть позже.", "Something went wrong. Please try again in a moment.");
    public string PayBack => S("К выбору тарифа", "Back to plans");
    public string Close => S("Закрыть", "Close");
    public string OtherVpnTitle => S("На этом Mac уже работает другой VPN", "Another VPN is already running on this Mac");
    public string OtherVpnText => S("Два VPN одновременно мешают друг другу: сайты могут не открываться, а TER не сможет достучаться до сервера. Выключите другой VPN (например, Happ, v2ray или WireGuard) и нажмите «Проверить снова».",
                                    "Two VPNs at once get in each other's way: sites may not open and TER may not reach its server. Turn the other VPN off and press \"Check again\".");
    public string OtherVpnRetry => S("Проверить снова", "Check again");
    public string OtherVpnForce => S("Всё равно подключить", "Connect anyway");
    public string TipOtherVpnRetry => S("Проверим, выключен ли другой VPN, и сразу подключимся", "We check that the other VPN is off, then connect");
    public string TipOtherVpnForce => S("Подключиться, не выключая другой VPN (может не заработать)", "Connect without turning the other VPN off (may not work)");
    public string NoServerResponds => S("Серверы не отвечают. Проверьте интернет или обновите подписку.",
                                        "No server responds. Check your connection or refresh the subscription.");
    public string Ms(int ms) => S($"{ms} мс", $"{ms} ms");

    // Connect button / status
    public string TapToConnect => S("Нажмите, чтобы подключиться", "Tap to connect");
    // 0.6.2: TER's own admin-password sheet (upstream's lives in the hidden advanced view)
    public string TunPwdTitle => S("Пароль от Mac", "Your Mac password");
    public string TunPwdBody => S("Чтобы через TER шли все программы, а не только браузер, macOS нужно разрешение администратора. Введите пароль, которым входите в Mac.",
        "To send every app through TER, not just the browser, macOS needs administrator permission. Enter the password you use to log in to your Mac.");
    public string TunPwdNote => S("Пароль остаётся только на этом Mac: никуда не отправляется и не сохраняется. После перезапуска TER спросит снова.",
        "The password stays on this Mac: it is never sent or saved. TER asks again after a restart.");
    public string TunPwdPlaceholder => S("Пароль от Mac", "Mac password");
    public string TunPwdOk => S("Разрешить", "Allow");
    public string TunPwdSkip => S("Только браузеры", "Browsers only");
    public string TunPwdChecking => S("Проверяем…", "Checking…");
    public string TunPwdWrong => S("Пароль не подошёл. Попробуйте ещё раз.", "Wrong password. Try again.");
    public string TunPwdNotAdmin => S("У этой учётной записи Mac нет прав администратора — защищены будут только браузеры.",
        "This Mac account is not an administrator — only browsers will be protected.");
    // 0.6.1: «Защищать весь трафик» on Connect
    public string TunAsking => S("Ждём пароль от Mac…", "Waiting for your Mac password…");
    public string TunSkipped => S("Пароль не введён — через TER идут только браузеры. Чтобы защитить всё: отключитесь и подключитесь снова.",
        "No password — only browsers go through TER. To protect everything: disconnect and connect again.");
    public string Connecting => S("Подключаемся…", "Connecting…");
    public string Connected => S("Подключено", "Connected");
    public string Disconnected => S("Отключено", "Disconnected");
    public string Connect => S("Подключиться", "Connect");
    public string Disconnect => S("Отключиться", "Disconnect");
    public string Protected => S("Защищено", "Protected");
    public string AllTrafficTun => S("Весь трафик компьютера", "All computer traffic");
    public string RuDirectHint => S("Российские сайты — напрямую · изменить в настройках",
                                    "Russian sites go direct · change in settings");

    // Location row
    public string Auto => S("Авто · самый быстрый", "Auto · fastest");
    public string LocationAuto => S("Авто", "Auto");
    public string ChosenByYou => S("Выбрано вами", "Your choice");

    // Location picker
    public string PickerTitle => S("Выберите локацию", "Choose a location");
    public string PickerHint => S("Мы сами выберем лучший способ подключения в каждой стране",
                                  "We pick the best way to connect in every country");
    public string AutoTitle => S("Авто", "Auto");
    public string AutoCaption => S("Самая быстрая из доступных сейчас", "Fastest one available right now");
    public string AutoNow(string title) => S($"Сейчас: {title}", $"Now: {title}");
    public string RussiaCaption => S("Только по вашему выбору — «Авто» её не выбирает", "Only if you pick it — Auto never uses it");
    public string NotResponding => S("Сейчас не отвечает", "Not responding right now");
    public string LocationDown => S("Эта локация сейчас не отвечает — выберите другую или «Авто»",
                                    "This location is not responding — pick another one or Auto");
    public string Retest => S("Проверить скорость", "Check speed");
    public string Testing => S("Проверяем…", "Checking…");
    public string Back => S("Назад", "Back");

    // Sidebar
    public string NavHome => S("Главная", "Home");
    public string NavServers => S("Серверы", "Servers");
    public string NavSettings => S("Настройки", "Settings");
    public string NavSupport => S("Поддержка", "Support");
    public string NavAdvanced => S("Для опытных", "Advanced");
    public string NavAdvancedHint => S("Полный интерфейс: протоколы, правила, логи", "Full interface: protocols, rules, logs");
    public string PrivateNetwork => "PRIVATE NETWORK";

    // Settings: every switch says in words what it does
    public string SettingsTitle => S("Настройки", "Settings");
    public string SettingsStartup => S("Запуск", "Startup");
    public string AutoRunTitle => S("Открывать TER при входе в систему", "Open TER at login");
    public string AutoRunHint => S("Приложение запустится само, когда вы включите компьютер.",
                                   "The app starts by itself when you turn on the computer.");
    public string AllTrafficTitle => S("Защищать весь трафик компьютера", "Protect all computer traffic");
    public string AllTrafficHint => S("Через TER идут все программы: браузер, Telegram, игры. При первом подключении после запуска TER Mac спросит пароль администратора. Выключено — через TER идут только браузеры.",
        "Every app goes through TER: browser, Telegram, games. On the first Connect after TER starts, your Mac asks for the administrator password. Off — only browsers go through TER.");
    public string SettingsRouting => S("Какие сайты идут через TER", "Which sites go through TER");
    public string RouteExceptRuTitle => S("Всё, кроме российских сайтов", "Everything except Russian sites");
    public string RouteExceptRuHint => S("Банки, Госуслуги и маркетплейсы открываются напрямую и не ругаются на VPN.",
                                         "Banks, Gosuslugi and marketplaces open directly and don't complain about VPN.");
    public string RouteBlockedTitle => S("Только заблокированные сайты", "Only blocked sites");
    public string RouteBlockedHint => S("Через TER идут только недоступные в России сайты. Остальное — как без VPN.",
                                        "Only sites unavailable in Russia go through TER. Everything else works as without VPN.");
    public string RouteAllTitle => S("Весь интернет", "The whole internet");
    public string RouteAllHint => S("Абсолютно всё через TER, включая российские сайты. Некоторые банки могут не открыться.",
                                    "Absolutely everything goes through TER, Russian sites included. Some banks may not open.");
    public string SettingsSecurity => S("Безопасность", "Security");
    public string KillSwitchTitle => S("Блокировать интернет, если VPN оборвался", "Block the internet if the VPN drops");
    public string KillSwitchHint => S("Если связь с сервером пропадёт, браузер и программы не выйдут в интернет напрямую, пока TER не переподключится. Когда выключено, TER тоже переподключается сам, а если не получится — честно предупредит, что вы без защиты.",
                                      "If the server connection drops, the browser and apps will not go online directly until TER reconnects. When off, TER still reconnects by itself, and if that fails it warns you that you are unprotected.");

    // Connection guard (auto-reconnect / kill switch)
    public string Reconnecting => S("Переподключаемся…", "Reconnecting…");
    public string ReasonCoreDied => S("Соединение оборвалось", "The connection dropped");
    public string ReasonNetwork => S("Сеть изменилась", "The network changed");
    public string ReasonTunnel => S("Сервер не отвечает — ищем другой", "The server does not respond — looking for another one");
    public string Restored => S("Соединение восстановлено", "Connection restored");
    public string KillSwitchBlocked => S("Защита прервана", "Protection interrupted");
    public string KillSwitchBlockedHint => S("Интернет заблокирован, чтобы ничего не ушло мимо VPN. TER пробует переподключиться каждые 2 минуты. Нажмите кнопку, чтобы отключить защиту и вернуть обычный интернет.",
                                             "The internet is blocked so nothing leaks past the VPN. TER retries every 2 minutes. Press the button to turn protection off and get the normal internet back.");
    public string DroppedUnprotected => S("VPN отключился — вы сейчас без защиты", "The VPN is off — you are not protected now");
    public string DroppedUnprotectedHint => S("Переподключиться не удалось. Нажмите кнопку, чтобы попробовать снова, или выберите другую страну.",
                                              "Reconnecting failed. Press the button to try again or pick another country.");
    public string RoutingMissing => S("Профиль маршрутизации не найден — перезапустите приложение", "Routing profile not found — restart the app");
    public string Recommended => S("рекомендуем", "recommended");

    // Support
    public string SupportTitle => S("Поддержка", "Support");
    public string SupportBotTitle => S("Написать в Telegram-бот", "Message our Telegram bot");
    public string SupportBotHint => S("Продление подписки, новые устройства, вопросы — ответим в боте.",
                                      "Renewal, new devices, questions — we answer in the bot.");
    public string SupportFaqTitle => S("Частые вопросы", "FAQ");
    public string SupportFaqHint => S("Что делать, если не подключается или медленно работает.",
                                      "What to do if it doesn't connect or is slow.");
    public string SupportAdvancedHint => S("Для опытных: «Для опытных» в меню слева — там серверы по протоколам, правила маршрутизации и журнал.",
                                           "Power users: «Advanced» in the left menu has per-protocol servers, routing rules and the log.");

    public string KbTitle => S("Как всё устроено", "How it works");

    // Support chat (0.4)
    public string ChatTitle => S("Чат с поддержкой", "Chat with support");
    public string ChatEmpty => S("Напишите вопрос — ответ придёт сюда же. Можно закрыть окно: TER покажет точку у «Поддержки», когда ответят.",
                                 "Write your question — the answer arrives right here. You can close the window: TER shows a dot next to «Support» when we reply.");
    public string ChatLoading => S("Загружаем переписку…", "Loading the conversation…");
    public string ChatPlaceholder => S("Сообщение…", "Message…");
    public string Send => S("Отправить", "Send");
    public string ChatSending => S("отправляется…", "sending…");
    public string ChatSendFailed => S("Не отправилось — проверьте интернет и попробуйте ещё раз.", "Not sent — check your connection and try again.");
    public string ChatTooLong(int max) => S($"Слишком длинное сообщение (больше {max} символов).", $"The message is too long (over {max} characters).");
    public string ChatOperator => S("Поддержка TER", "TER support");
    public string ChatOrBot => S("Удобнее в Telegram? Напишите в бот", "Prefer Telegram? Message our bot");
    public string ChatRateLimited(int seconds) => S($"Слишком часто. Попробуйте через {seconds} с.", $"Too many messages. Try again in {seconds} s.");
    public string ChatGuest => S("Чат в приложении доступен после входа через Telegram. Пока напишите нам в бот.",
                                 "The in-app chat needs a Telegram sign-in. Until then, message our bot.");
    public string TipSupportUnread => S("Новый ответ поддержки", "New reply from support");
    public string Today => S("сегодня", "today");

    // Tooltips for icon buttons
    public string TipConnect => S("Включить или выключить защиту", "Turn protection on or off");
    public string TipLocation => S("Выбрать страну подключения", "Choose the country");
    public string TipSubChip => S("Аккаунт: срок подписки, трафик, устройства", "Account: subscription term, traffic, devices");
    public string TipRetest => S("Заново измерить скорость всех стран", "Measure every country again");

    // Subscription chip / account
    public string Account => S("Аккаунт", "Account");
    public string SubActive => S("подписка активна", "subscription active");
    public string SubUntil(string date) => S($"до {date}", $"until {date}");
    public string SubExpired => S("подписка истекла", "subscription expired");
    public string RenewInTelegram => S("Продлить подписку", "Renew subscription");
    public bool En => IsEn();

    // Account page
    public string AccountTitle => S("Аккаунт", "Account");
    public string AccountSubscription => S("Подписка", "Subscription");
    public string AccountUsage => S("Использование", "Usage");
    public string AccountDevicesTitle => S("Устройства", "Devices");
    public string Refresh => S("Обновить", "Refresh");
    public string Refreshing => S("Обновляем…", "Refreshing…");
    public string Renew => S("Продлить", "Renew");
    public string TipRefreshAccount => S("Заново запросить данные подписки у сервера", "Ask the server for the subscription data again");
    public string TipRenew => S("Выбрать тариф и оплатить прямо в приложении", "Choose a plan and pay right in the app");
    public string StatusActive => S("Подписка активна", "Subscription active");
    public string StatusExpired => S("Подписка истекла", "Subscription expired");
    public string StatusInactive => S("Нет активной подписки", "No active subscription");
    public string StatusUnknown => S("Нет данных о подписке", "No subscription data");
    public string ExpiresIn(int days) => days <= 0
        ? S("Истекает меньше чем через сутки", "Expires in less than a day")
        : S($"Истекает через {days} {TermitusAccount.RuPlural(days, "день", "дня", "дней")}",
            $"Expires in {days} day{(days == 1 ? "" : "s")}");
    public string Until(string date) => S($"до {date}", $"until {date}");
    public string ExpiredOn(string date) => S($"закончилась {date}", $"ended on {date}");
    public string NoEndDate => S("без даты окончания", "no end date");
    public string InactiveHint => S("Продлите подписку в Telegram-боте — серверы снова заработают.",
                                    "Renew in our Telegram bot and the servers will work again.");
    public string UnknownHint => S("Нажмите «Обновить». Если не помогло — проверьте интернет.",
                                   "Press «Refresh». If that does not help, check your connection.");
    public string TrafficUsed => S("Трафик", "Traffic");
    public string TrafficOf(string used, string total) => S($"{used} из {total}", $"{used} of {total}");
    public string TrafficUnlimited(string used) => S($"{used} · без ограничений", $"{used} · unlimited");
    public string TrafficHint => S("Скачано и отправлено через TER на всех устройствах этой подписки.",
                                   "Downloaded and uploaded through TER on all devices of this subscription.");
    public string Plan => S("Тариф", "Plan");
    public string Balance => S("Баланс", "Balance");
    public string BalanceHint => S("Деньги на вашем счёте TER.", "Money on your TER account.");
    public string TopUp => S("Пополнить", "Top up");
    public string TipTopUp => S("Откроется страница пополнения баланса", "Opens the balance top-up page");
    public string DevicesOf(int used, int allowed) => S($"{used} из {allowed}", $"{used} of {allowed}");
    public string DevicesHint => S("Новое устройство добавляется в Telegram-боте.", "Add a new device in our Telegram bot.");
    public string DeviceInactive => S("не активно", "inactive");
    public string UpdatedAt(string when) => S($"Обновлено: {when}", $"Updated: {when}");
    public string Never => S("ещё не обновлялось", "never");
    public string RefreshFailed => S("Не удалось обновить данные аккаунта", "Could not refresh the account data");
    public string SubDaysLeft(int days) => days <= 0
        ? S("меньше суток", "less than a day")
        : S($"осталось {days} {TermitusAccount.RuPlural(days, "день", "дня", "дней")}", $"{days} day{(days == 1 ? "" : "s")} left");
    public string SubInactive => S("нет подписки", "no subscription");
    public string SubUnknown => S("подписка", "subscription");
    public string SubSignedOut => S("вход не выполнен", "not signed in");
    public string BannerExpiring(int days) => days <= 0
        ? S("Подписка заканчивается сегодня", "Your subscription ends today")
        : S($"Подписка заканчивается через {days} {TermitusAccount.RuPlural(days, "день", "дня", "дней")}",
            $"Your subscription ends in {days} day{(days == 1 ? "" : "s")}");
    public string BannerExpired => S("Подписка закончилась — VPN не работает", "Your subscription has ended — the VPN does not work");
    public string BannerHint => S("Выберите тариф и оплатите прямо здесь — это пара минут.", "Choose a plan and pay right here, it takes a couple of minutes.");

    // No connection to our server (0.4.1): one calm text everywhere.
    public string NoConnection => S("Нет связи с сервером TER. Проверьте интернет или выключите другие VPN-приложения.",
                                    "No connection to the TER server. Check your internet or turn off other VPN apps.");
    public string NoConnectionShort => S("Нет связи с сервером TER", "No connection to the TER server");
    public string Retry => S("Повторить", "Try again");
    public string ShowingSaved => S("Ниже — последние сохранённые данные.", "Below is the last saved data.");

    // Telegram account (0.4.1)
    public string TelegramTitle => S("Telegram", "Telegram");
    public string TelegramHint => S("Аккаунт, через который вы вошли", "The account you signed in with");

    // Signed out (0.4.1)
    public string SignedOutTitle => S("Вы не вошли", "You are not signed in");
    public string SignedOutHint => S("Войдите через Telegram — здесь появятся подписка, баланс и устройства.",
                                     "Sign in with Telegram to see your subscription, balance and devices here.");
    public string SignIn => S("Войти", "Sign in");

    // Log out (0.4.1)
    public string Logout => S("Выйти", "Log out");
    public string LogoutRowHint => S("Подписка и серверы будут удалены с этого компьютера.",
                                     "The subscription and servers are removed from this computer.");
    public string LogoutTitle => S("Выйти из аккаунта на этом компьютере?", "Log out on this computer?");
    public string LogoutBody => S("Чтобы войти снова, понадобится Telegram.", "You will need Telegram to sign in again.");
    public string LogoutDeviceNote => S("Этот компьютер останется в списке ваших устройств в боте, пока вы не удалите его там.",
                                        "This computer stays in your device list in the bot until you remove it there.");
    public string LoggingOut => S("Выходим…", "Logging out…");
    public string LoggedOut => S("Вы вышли из аккаунта.", "You are logged out.");
    public string LogoutFailed => S("Не получилось выйти. Попробуйте ещё раз.", "Could not log out. Please try again.");

    // Advanced
    public string Advanced => S("Для опытных", "Advanced");
    public string Home => S("Главная", "Home");
}
