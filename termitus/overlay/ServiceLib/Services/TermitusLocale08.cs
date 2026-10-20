namespace ServiceLib.Services;

// TER 0.6.5: free trial from the app, «Пригласить друга».
public sealed partial class TermitusLocale
{
    // ---- Trial ----
    public string TrialButton => S("Попробовать бесплатно — 2 дня", "Try free for 2 days");
    public string TrialStarting => S("Готовим пробный доступ…", "Preparing your trial…");
    public string TrialConsentPre => S("Принимаю ", "I accept the ");
    public string TrialTerms => S("условия использования", "Terms of Service");
    public string TrialConsentMid => S(" и даю ", " and give my ");
    public string TrialConsentLink => S("согласие на обработку данных", "consent to data processing");
    public string TrialConsentPost => S(" (подробнее — в ", " (details in the ");
    public string TrialPrivacy => S("политике конфиденциальности", "Privacy Policy");
    public string TrialConsentEnd => S(")", ")");
    public string TrialNeedConsent => S("Чтобы взять пробный доступ, поставьте галочку согласия.", "To start the trial, tick the consent box.");
    public string TrialOpenBot => S("Открыть бота", "Open the bot");
    public string TrialOffline => S("Нет связи с сервером. Проверьте интернет и нажмите кнопку ещё раз.", "No connection to the server. Check your internet and press the button again.");
    public string TrialFailed => S("Не получилось получить пробный доступ. Попробуйте позже или напишите в нашего бота.", "Could not get the trial. Try later or message our bot.");
    public string TrialNotConfigured => S("Пробный доступ из приложения сейчас недоступен. Его можно взять в нашем Telegram-боте.", "The in-app trial is not available right now. You can get it in our Telegram bot.");
    public string TrialRefusedInstall => S("Пробный период на этом устройстве уже брали — возьмите его в нашем Telegram-боте.", "The trial has already been used on this device — get it in our Telegram bot.");
    public string TrialRefusedBusy => S("Сегодня пробный доступ уже раздан. Загляните завтра или возьмите его в нашем Telegram-боте.", "Today's trials are all taken. Come back tomorrow or get one in our Telegram bot.");
    public string TrialRefusedIp => S("С вашего адреса сегодня уже брали пробный доступ. Возьмите его в нашем Telegram-боте.", "A trial was already taken from your address today. Get it in our Telegram bot.");
    public string TrialCardTitle => S("Пробный доступ", "Trial access");
    public string TrialCodeLabel => S("Ваш код", "Your code");
    public string TrialCodeHint => S("Привяжите пробный доступ к Telegram — он станет настоящим аккаунтом: продление, оплата, друзья.", "Link the trial to Telegram — it becomes a real account: renewal, payment, friends.");
    public string TrialBind => S("Привязать к Telegram", "Link to Telegram");
    public string TrialCopyCode => S("Скопировать код", "Copy code");
    public string TipTrialBind => S("Откроется наш бот с вашим кодом", "Opens our bot with your code");
    public string TrialBindStarting => S("Готовим вход через Telegram…", "Preparing the Telegram sign-in…");
    public string TrialBindWaiting => S("Подтвердите вход в Telegram — откроется наш бот.", "Confirm the sign-in in Telegram — our bot opens.");
    public string TrialBindCancel => S("Отмена", "Cancel");
    public string TrialBindDone => S("Готово: пробный период — 7 дней, теперь он в вашем Telegram-аккаунте", "Done: the trial is now 7 days and lives in your Telegram account");
    public string TrialBindLoggedIn => S("Вход выполнен", "Signed in");
    public string TrialNoteUnavailable => S("Код уже использован или недействителен", "The code is already used or invalid");
    public string TrialNoteCooldown => S("Пробный период уже был недавно", "A trial was taken recently");
    public string TrialNoteError => S("Не получилось привязать код, напишите в поддержку", "Could not link the code, please write to support");
    public string TrialBindNote(string? note) => note switch
    {
        "code_unavailable" => TrialNoteUnavailable,
        "cooldown" => TrialNoteCooldown,
        _ => TrialNoteError,
    };
    public string TrialRefusalText(string reason, string? detail)
        => !string.IsNullOrWhiteSpace(detail) ? detail
            : reason switch { "install" => TrialRefusedInstall, "busy" => TrialRefusedBusy, _ => TrialRefusedIp };

    // ---- Invite a friend ----
    public string InviteRowTitle => S("Пригласить друга", "Invite a friend");
    public string InviteRowHint => S("15% или 4 дня с каждой оплаты друга", "15% or 4 days from every payment of your friend");
    public string InviteSheetTitle => S("Пригласить друга", "Invite a friend");
    public string InviteSheetScan => S("Друг наводит камеру на код или открывает ссылку — и подключается.", "Your friend scans the code or opens the link and joins.");
    public string InviteCopy => S("Скопировать", "Copy");
    public string InviteCopied => S("Ссылка скопирована", "Link copied");
    public string InviteNoLink => S("Ссылка для друзей появится после входа через Telegram.", "The friends link appears after you sign in with Telegram.");
    public string TipInviteRow => S("Откроется ваша ссылка для друзей с QR-кодом", "Opens your friends link with a QR code");

    // ---- 0.6.5 Windows: core file blocked by the antivirus / crash report ----
    public string ErrTitleCoreBlocked => S("Защитник Windows заблокировал файл ядра", "Windows Defender blocked a core file");
    public string CoreBlockedFailed(IReadOnlyList<string> names) => S(
        $"Не найден файл: {string.Join(", ", names)}. Скорее всего, антивирус Windows (Защитник) принял его за угрозу и убрал в карантин.",
        $"File not found: {string.Join(", ", names)}. Most likely Windows antivirus (Defender) took it for a threat and quarantined it.");
    public string CoreBlockedWhat => S(
        "Что сделать: откройте «Безопасность Windows» → «Защита от вирусов и угроз» → «Журнал защиты», восстановите файл и выберите «Разрешить на устройстве». Если файла в журнале нет — скачайте TER заново. Остальной интернет не затронут.",
        "What to do: open «Windows Security» → «Virus & threat protection» → «Protection history», restore the file and choose «Allow on device». If it is not there, download TER again. The rest of your internet is unaffected.");
    public string CrashAsk => S(
        "TER закрылся с ошибкой. Отправить отчёт разработчикам? Ниже — весь текст: в нём нет ссылок, ключей и адресов. Без вашего «Отправить» ничего не уходит.",
        "TER closed with an error. Send a report to the developers? The whole text is below: it has no links, keys or addresses. Nothing is sent unless you press «Send».");
}
