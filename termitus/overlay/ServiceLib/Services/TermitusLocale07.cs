namespace ServiceLib.Services;

// TER Account 2.0: devices, friends, payments, change link, "not a manager" card.
public sealed partial class TermitusLocale
{
    // ---- Devices ----
    public string AcctDevicesTitle => S("Устройства", "Devices");
    public string AcctDevicesHint => S("Где подключена ваша подписка. Управлять ими можно прямо здесь.", "Where your subscription is connected. You can manage them right here.");
    public string AcctThisDevice => S("это устройство", "this device");
    public string AcctManages => S("управляет", "manages");
    public string AcctPaidSlot => S("платное место", "paid slot");
    public string AcctPaused => S("приостановлено", "paused");
    public string AcctDevicesLoading => S("Загружаем устройства…", "Loading devices…");
    public string AcctDevicesEmpty => S("Устройств пока нет.", "No devices yet.");
    public string AcctRowMenu => S("Действия", "Actions");
    public string AcctRename => S("Переименовать", "Rename");
    public string AcctGiveManage => S("Дать управление", "Allow managing");
    public string AcctTakeManage => S("Забрать управление", "Take away managing");
    public string AcctDisable => S("Отключить", "Remove");
    public string AcctAddDevice => S("Добавить устройство", "Add a device");
    public string AcctAddDeviceTip => S("Ссылка и QR-код для входа с другого устройства", "A link and QR code to sign in from another device");
    public string AcctRenameTitle => S("Переименовать устройство", "Rename device");
    public string AcctRenamePlaceholder => S("Название устройства", "Device name");
    public string AcctSave => S("Сохранить", "Save");
    public string AcctRemoveTitle(string name) => S($"Отключить «{name}»?", $"Remove “{name}”?");
    public string AcctRemoveBody => S("Устройство сразу перестанет подключаться. Место освободится.", "The device stops connecting immediately. Its slot is freed.");
    public string AcctRemoveThisNote => S("Это устройство, с которого вы сейчас работаете: после отключения TER выйдет из аккаунта.", "This is the device you are using right now: after removing it, TER signs out.");
    public string AcctRemoveButton => S("Отключить", "Remove");
    public string AcctBusy => S("Секунду…", "One moment…");

    // Add a device
    public string AcctInvitePayTitle => S("Новое устройство", "New device");
    public string AcctInvitePayBody(int price, string balance) => S(
        $"Это место сверх бесплатных: {price} ₽ в месяц с баланса. Спишем, когда устройство подключится по ссылке; если ссылкой не воспользуются — ничего. Сейчас на балансе {balance}.",
        $"This is beyond the free devices: {price} RUB a month from your balance, charged when the device connects via the link; an unused link costs nothing. Your balance is {balance}.");
    public string AcctInviteConfirm => S("Продолжить", "Continue");
    public string AcctNoFundsTitle => S("Не хватает баланса", "Not enough balance");
    public string AcctNoFundsBody(int price, string balance) => S(
        $"Место сверх бесплатных стоит {price} ₽ в месяц, а на балансе {balance}. Пополните баланс и попробуйте снова.",
        $"A new device costs {price} RUB a month, your balance is {balance}. Top up and try again.");
    public string AcctNoFundsBodyPlain => S("Не хватает баланса на доплату за устройство. Пополните баланс и попробуйте снова.",
        "Not enough balance for the extra device. Top up and try again.");
    public string AcctTopUp => S("Пополнить баланс", "Top up balance");
    public string AcctInviteTitle => S("Добавить устройство", "Add a device");
    public string AcctInviteScan => S("Отсканируйте код камерой телефона или откройте ссылку на другом устройстве.", "Scan the code with your phone camera, or open the link on the other device.");
    public string AcctInviteCopy => S("Скопировать", "Copy");
    public string AcctInviteCopied => S("Скопировано", "Copied");
    public string AcctInviteOnce => S("Ссылка работает 10 минут и один раз.", "The link works for 10 minutes and only once.");
    public string AcctInviteTooMany => S("Слишком много открытых приглашений. Подождите несколько минут.", "Too many open invitations. Wait a few minutes.");
    public string AcctInviteFailed => S("Не удалось создать ссылку. Попробуйте ещё раз.", "Could not create the link. Please try again.");

    // ---- Friends ----
    public string AcctFriendsTitle => S("Друзья", "Friends");
    public string AcctFriendsHint => S("Поделитесь ссылкой — каждый раз, когда друг оплачивает подписку или пополняет баланс, вы получаете бонус: дни подписки или процент на баланс.", "Share your link — every time a friend pays for a subscription or tops up, you get a bonus: subscription days or a percentage to your balance.");
    public string AcctFriendsLink => S("Ваша ссылка", "Your link");
    public string AcctFriendsNoLink => S("Ссылка появится после входа через Telegram.", "The link appears after you sign in with Telegram.");
    public string AcctFriendsInvited => S("приглашено", "invited");
    public string AcctFriendsEarnedRub => S("заработано", "earned");
    public string AcctFriendsEarnedDays => S("бонусных дней", "bonus days");
    public string AcctRewardTitle => S("Что вы получаете", "Your reward");
    public string AcctRewardMoney => S("Деньги на баланс", "Money to balance");
    public string AcctRewardDays => S("Бонусные дни", "Bonus days");
    public string AcctRewardSaved => S("Награда за друзей изменена", "Friends reward changed");
    public string AcctRewardFailed => S("Не удалось сохранить. Попробуйте ещё раз.", "Could not save. Please try again.");

    // ---- Payments ----
    public string AcctPaymentsTitle => S("Оплаты", "Payments");
    public string AcctPaymentsEmpty => S("Оплат пока не было.", "No payments yet.");
    public string AcctShowAll => S("Показать все", "Show all");
    public string AcctShowLess => S("Свернуть", "Show less");

    // ---- Change link ----
    public string AcctRotateRow => S("Сменить ссылку", "Change link");
    public string AcctRotateRowHint => S("Если ссылкой подписки поделились лишние люди.", "If your subscription link got to the wrong people.");
    public string AcctRotateTitle => S("Сменить ссылку подписки?", "Change the subscription link?");
    public string AcctRotateBody => S("Старая ссылка перестанет работать в других приложениях (Happ и подобных) — туда нужно будет добавить новую. Устройства, в которые вы вошли через TER, продолжат работать.",
        "The old link stops working in other apps (Happ and similar) — you will need to add the new one there. Devices signed in to TER keep working.");
    public string AcctRotateButton => S("Сменить ссылку", "Change link");
    public string AcctRotateDoneTitle => S("Ссылка заменена", "Link changed");
    public string AcctRotateDoneBody => S("Новую ссылку можно скопировать и добавить в другие приложения.", "You can copy the new link and add it to your other apps.");
    public string AcctRotateCopy => S("Скопировать новую ссылку", "Copy the new link");
    public string AcctRotateFailed => S("Не удалось сменить ссылку. Попробуйте ещё раз.", "Could not change the link. Please try again.");

    // ---- Not a manager ----
    public string AcctGuestSection => S("Управление подпиской", "Subscription management");
    public string AcctGuestTitle => S("Подпиской управляет её владелец", "The subscription is managed by its owner");
    public string AcctGuestBody => S("Подпиской управляет её владелец. Если это ваша подписка — войдите заново через Telegram.",
        "The subscription is managed by its owner. If it is yours, sign in again with Telegram.");
    public string AcctGuestButton => S("Войти заново", "Sign in again");

    // ---- Shared ----
    public string AcctLoadFailed => S("Не удалось загрузить данные. Попробуйте обновить страницу.", "Could not load the data. Try refreshing.");
    public string AcctActionFailed => S("Не получилось. Попробуйте ещё раз.", "That did not work. Please try again.");
}
