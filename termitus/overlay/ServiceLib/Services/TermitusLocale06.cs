namespace ServiceLib.Services;

// TER 0.6 strings: problem report, connection check, connection-error card, updater, about.
public sealed partial class TermitusLocale
{
    // Protocol names for the check
    public string ProtoTcpFull => S("Обычный (TCP)", "Standard (TCP)");
    public string ProtoUdpFull => S("Быстрый (UDP)", "Fast (UDP)");

    // ---- Problem report ----
    public string ReportOpen => S("Сообщить о проблеме", "Report a problem");
    public string ReportTitle => S("Сообщить о проблеме", "Report a problem");
    public string ReportWhatTitle => S("Что не так?", "What is wrong?");
    public string ReportTextPlaceholder => S("Расскажите подробнее (необязательно)", "Tell us more (optional)");
    public string ReportAttachTitle => S("Приложить диагностику", "Attach diagnostics");
    public string ReportAttachHint => S("Версия, система, режим, сервер и последние ошибки — без ссылок, адресов и ключей. Ничего не уходит, пока вы не нажмёте «Отправить».",
        "Version, system, mode, server and recent errors — no links, addresses or keys. Nothing is sent until you press “Send”.");
    public string ReportPreviewTitle => S("Что именно будет отправлено", "Exactly what will be sent");
    public string ReportPreviewHint => S("Это полный текст сообщения — ровно то, что получит поддержка.", "This is the full message — exactly what support receives.");
    public string ReportSend => S("Отправить", "Send");
    public string ReportSendingText => S("Отправляем…", "Sending…");
    public string ReportSentTitle => S("Отправлено", "Sent");
    public string ReportSentBody => S("Ответ придёт сюда, в чат поддержки, и в Telegram.", "The reply will arrive here, in the support chat, and in Telegram.");
    public string ReportOpenChat => S("Открыть чат", "Open chat");
    public string ReportOfflineTitle => S("Не удалось отправить", "Could not send");
    public string ReportOfflineText => S("Нет связи с сервером TER. Черновик сохранён на этом компьютере — отправьте, когда связь появится.",
        "No connection to the TER server. Your draft is saved on this computer — send it when you are back online.");
    public string ReportBotTitle => S("Чат поддержки сейчас недоступен", "Support chat is not available");
    public string ReportBotBody => S("Скопируйте текст ниже и отправьте его нашему боту в Telegram.", "Copy the text below and send it to our Telegram bot.");
    public string ReportCopy => S("Скопировать текст", "Copy text");
    public string ReportCheckNone => S("Проверка подключения ещё не запускалась — в сообщении этого раздела не будет.", "The connection check has not been run — this part will be missing.");
    public string ReportCheckHave => S("Результаты проверки подключения включены.", "Connection check results are included.");
    public string ReportCheckRun => S("Запустить проверку", "Run the check");
    public string ReportCheckRunningText => S("Проверяем…", "Checking…");
    public string ReportTooLong => S("Сообщение слишком длинное — сократите описание.", "The message is too long — shorten the description.");
    public string ChipConnect => S("Не подключается", "Won't connect");
    public string ChipSlow => S("Медленно", "Slow");
    public string ChipSites => S("Не открываются сайты", "Sites won't open");
    public string ChipDrops => S("Отключается", "Keeps disconnecting");
    public string ChipOther => S("Другое", "Other");
    public string ChipNone => S("Без категории", "No category");
    public string SupportReportTitle => S("Сообщить о проблеме", "Report a problem");
    public string SupportReportHint => S("Опишите, что не так: сообщение уйдёт в поддержку вместе с диагностикой, которую вы увидите заранее.",
        "Describe what is wrong: it goes to support with diagnostics you see beforehand.");
    public string SupportCheckTitle => S("Проверка подключения", "Connection check");
    public string SupportCheckHint => S("Какие серверы и протоколы работают у вас прямо сейчас.", "Which servers and protocols work for you right now.");
    public string TrayReport => S("Сообщить о проблеме…", "Report a problem…");

    // ---- Connection check ----
    public string CheckTitle => S("Проверка подключения", "Connection check");
    public string CheckSubtitle => S("Проверяем каждый сервер и протокол прямо с вашего компьютера. Результат остаётся здесь — никуда не отправляется.",
        "We test every server and protocol from your computer. The result stays here — nothing is sent.");
    public string CheckRun => S("Проверить заново", "Check again");
    public string CheckRunningText => S("Проверяем серверы…", "Checking servers…");
    public string CheckNoServersText => S("Серверов пока нет — сначала войдите в аккаунт.", "No servers yet — sign in first.");
    public string CheckLegend => S("Обычный (TCP) работает почти везде. Быстрый (UDP) быстрее, но его чаще режут провайдеры и точки доступа.",
        "Standard (TCP) works almost everywhere. Fast (UDP) is faster but is more often blocked by ISPs and hotspots.");
    public string CheckOkLine => S("Работает", "Works");
    public string CheckSlowLine(int ms) => S($"Отвечает медленно ({ms} мс): страницы и видео могут тормозить. Попробуйте страну поближе или другой протокол.",
        $"Answers slowly ({ms} ms): pages and video may lag. Try a closer country or another protocol.");
    public string CheckUdpBlockedLine => S("UDP не проходит — скорее всего, его режет ваша сеть. Этот же сервер доступен по TCP.",
        "UDP does not get through — your network most likely blocks it. This server works over TCP.");
    public string CheckTcpBlockedLine => S("TCP не проходит, а по UDP этот сервер отвечает.", "TCP does not get through, but this server answers over UDP.");
    public string CheckDownLine => S("Сервер не отвечает: он временно недоступен или ваша сеть его блокирует.", "The server does not answer: it is down or your network blocks it.");
    public string CheckAllDown => S("Ни один сервер не отвечает. Откройте любой сайт без TER: если он не открывается — дело в интернете. Если открывается — выключите другие VPN и прокси и проверьте заново. Не помогло — сообщите нам.",
        "No server answers. Open any site without TER: if it does not load, it is your internet. If it loads, turn off other VPNs and proxies and check again. Still nothing — tell us.");
    public string CheckOnlyRussia => S("Отвечает только российский сервер. Похоже на «белые списки» у вашего оператора: зарубежные адреса закрыты. Сообщите нам — подскажем, что делать.",
        "Only the Russian server answers. This looks like an operator whitelist that blocks foreign addresses. Tell us and we will help.");
    public string CheckUdpCut => S("UDP режется вашей сетью — выберите обычный TCP-сервер.", "Your network blocks UDP — choose the standard TCP server.");
    public string CheckTcpCut => S("TCP-серверы не отвечают, а UDP работает — выберите «Быстрый UDP».", "TCP servers do not answer but UDP works — choose “Fast UDP”.");
    public string CheckSomeDown(int bad, int total) => S($"{bad} из {total} серверов не отвечают — это нормально, TER выберет рабочий. Если нужная страна не отвечает, выберите другую.",
        $"{bad} of {total} servers do not answer — that is fine, TER picks a working one. If your country is down, choose another.");
    public string CheckAllSlow => S("Все серверы отвечают медленно — скорее всего, дело в вашем интернете (Wi-Fi или оператор).", "All servers answer slowly — most likely it is your internet (Wi-Fi or carrier).");
    public string CheckAllGood => S("Всё хорошо: все серверы отвечают.", "All good: every server answers.");
    public string CheckBtnUseTcp => S("Выбрать обычный TCP", "Use standard TCP");
    public string CheckBtnUseUdp => S("Выбрать быстрый UDP", "Use fast UDP");
    public string CheckBtnOtherServer => S("Попробовать другой сервер", "Try another server");
    public string CheckProtocolSet => S("Протокол выбран. Он применится при следующем подключении.", "Protocol chosen. It applies on the next connect.");
    public string CheckStampText(DateTime at) => S($"Проверено в {at:HH:mm}", $"Checked at {at:HH:mm}");
    public string CheckReport => S("Сообщить о проблеме", "Report a problem");

    // ---- Connection error card ----
    public string ErrTitleNoServer => S("Не удалось подключиться", "Could not connect");
    public string ErrTitleConnect => S("Не получилось включить защиту", "Could not turn protection on");
    public string ErrTitleDropped => S("Соединение оборвалось", "The connection dropped");
    public string ErrTitleDroppedBlocked => S("Соединение оборвалось, интернет закрыт", "The connection dropped, internet is blocked");
    public string ErrWhatFailed => S("Что не получилось", "What failed");
    public string ErrWhatWorks => S("Что работает", "What still works");
    public string ErrTryOther => S("Попробовать другой сервер", "Try another server");
    public string ErrReport => S("Сообщить о проблеме", "Report a problem");
    public string ErrCheck => S("Проверка подключения", "Connection check");
    public string ErrFailedUnknown => S("Серверы не ответили на проверку.", "The servers did not answer the check.");
    public string ErrWorksUnknown => S("Остальной интернет не затронут: пока TER выключен, всё открывается как обычно.", "The rest of your internet is unaffected: while TER is off everything opens as usual.");
    public string ErrWorksNothing => S("Остальной интернет не затронут, но серверов TER сейчас не видно.", "The rest of your internet is unaffected, but no TER servers are reachable now.");
    public string ErrNoneAnswer(string name, int n) => S($"{name}: не отвечают все ({n}).", $"{name}: none answers ({n}).");
    public string ErrSomeAnswer(string name, int ok, int n) => S($"{name}: отвечают {ok} из {n}.", $"{name}: {ok} of {n} answer.");

    // ---- Updates ----
    public string UpdateTitle(string v) => S($"Доступно обновление {v}", $"Update {v} is available");
    public string UpdateBanner(string v) => S($"Доступно обновление {v}", $"Update {v} is available");
    public string UpdateBannerHint => S("Что нового — по кнопке.", "See what is new.");
    public string UpdateWhatsNew => S("Что нового", "What is new");
    public string UpdateNow => S("Обновить", "Update");
    public string UpdateLater => S("Позже", "Later");
    public string UpdateDetails => S("Показать файл", "Show file");
    public string UpdateFound(string v) => S($"Доступна версия {v}.", $"Version {v} is available.");
    public string UpdateUpToDate => S("У вас последняя версия.", "You have the latest version.");
    public string UpdateChecking => S("Проверяем…", "Checking…");
    public string UpdateNoKey => S("Обновления в этой сборке отключены (нет ключа подписи).", "Updates are off in this build (no signing key).");
    public string UpdateInvalid => S("Файл обновлений не прошёл проверку подписи — обновление не предлагается.", "The update file failed signature verification — no update is offered.");
    public string UpdateOffline => S("Нет связи с сервером обновлений.", "No connection to the update server.");
    public string UpdateServerError => S("Сервер обновлений сейчас не отвечает.", "The update server is not answering.");
    public string UpdateDownloading => S("Скачиваем обновление…", "Downloading the update…");
    public string UpdateDownloadingMb(long mb, long? total) => total is { } t
        ? S($"Скачиваем: {mb} из {t} МБ", $"Downloading: {mb} of {t} MB")
        : S($"Скачиваем: {mb} МБ", $"Downloading: {mb} MB");
    public string UpdateHashBad => S("Скачанный файл не совпал с подписанной контрольной суммой — он удалён и не будет установлен.", "The downloaded file does not match the signed checksum — it was deleted and will not be installed.");
    public string UpdateDownloadFailed => S("Не удалось скачать обновление. Проверьте интернет и попробуйте ещё раз.", "Could not download the update. Check your internet and try again.");
    public string UpdateInstalling => S("Проверено. Устанавливаем и перезапускаем TER…", "Verified. Installing and restarting TER…");
    public string UpdateStageFailed => S("Не удалось подготовить установку. Старая версия осталась как была.", "Could not prepare the install. The old version is untouched.");
    public string UpdateManualMac => S("Скачано и проверено. Открылось окно с TER — перетащите его в «Программы» с заменой, затем запустите заново.",
        "Downloaded and verified. A window with TER opened — drag it into Applications, replacing the old one, then start it again.");
    public string UpdateManualWin(string path) => S("Скачано и проверено. Закройте TER и запустите скачанный файл.", "Downloaded and verified. Close TER and run the downloaded file.");
    public string UpdateRetry => S("Повторить", "Try again");
    public string UpdatesSection => S("Обновления", "Updates");
    public string AutoUpdateTitle => S("Проверять обновления автоматически", "Check for updates automatically");
    public string AutoUpdateHint => S("Раз в 6 часов TER скачивает с termitus.online один маленький файл с номером последней версии. Больше ничего не отправляется.",
        "Every 6 hours TER fetches one small file with the latest version number from termitus.online. Nothing else is sent.");
    public string UpdateCheckNow => S("Проверить сейчас", "Check now");

    // ---- About / open source ----
    public string AboutSection => S("О программе", "About");
    public string SourceCodeTitle => S("Исходный код", "Source code");
    public string SourceCodeHint => S("TER — открытое ПО под лицензией GPL-3.0, основано на v2rayN. Исходники доступны всем.", "TER is open-source software (GPL-3.0), based on v2rayN. The source is available to everyone.");
    public string LicensesTitle => S("Лицензии открытого ПО", "Open-source licenses");
    public string LicensesHint => S("Тексты лицензий TER и встроенных компонентов", "License texts of TER and its bundled components");
}
