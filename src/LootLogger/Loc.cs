using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using LootLogger.Core.Comparator;
using LootLogger.Core.Game;
using LootLogger.Core.Loot;

namespace LootLogger;

/// <summary>
/// Тексты интерфейса на русском и английском. Язык переключается на лету:
/// привязки в XAML ({local:Tr Ключ}) обновляются сами, названия предметов — тоже.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? LanguageChanged;

    public bool IsEnglish { get; private set; }
    public CultureInfo Culture => IsEnglish ? En : Ru;

    public string this[string key] => Get(key);

    public static string T(string key) => Instance.Get(key);

    public static string F(string key, params object[] args) => string.Format(Instance.Culture, Instance.Get(key), args);

    public void SetLanguage(bool english)
    {
        IsEnglish = english;
        ItemInfo.UseEnglish = english;
        GameDatabase.InstanceLabel = Get("Instance");

        var culture = Culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
        LanguageChanged?.Invoke();
    }

    private string Get(string key)
    {
        return Strings.TryGetValue(key, out var pair) ? (IsEnglish ? pair.En : pair.Ru) : key;
    }

    private static readonly Dictionary<string, (string Ru, string En)> Strings = new()
    {
        ["AppTitle"] = ("Albion Лутлогер", "Albion Loot Logger"),
        ["TabJournal"] = ("Журнал лута", "Loot log"),
        ["TabCompare"] = ("Сверка с сундуком", "Chest check"),
        ["TabStats"] = ("Статистика игроков", "Player stats"),
        ["StatsTotals"] = ("{0:N0} серебра · {1:N0} шт. · записей: {2:N0}", "{0:N0} silver · {1:N0} items · entries: {2:N0}"),
        ["StatsComparison"] = ("Сверка: сдано {0:N0}% · не сдано на {1:N0}", "Chest check: {0:N0}% deposited · missing {1:N0}"),
        ["StatsEmpty"] = ("Лута пока нет", "No loot yet"),
        ["StatsSummary"] = ("Игроков: {0:N0} · залутано всего на {1:N0} серебра. Наведите на иконку, чтобы увидеть подробности.", "Players: {0:N0} · total looted: {1:N0} silver. Hover an icon for details."),
        ["StatsTipTotals"] = ("Количество: {0:N0} · стоимость: {1:N0}", "Quantity: {0:N0} · value: {1:N0}"),
        ["StatsTipLine"] = ("• {0} шт. с {1} — {2:HH:mm:ss}", "• {0} from {1} — {2:HH:mm:ss}"),
        ["StatsTipMore"] = ("…и ещё {0}", "…and {0} more"),
        ["AllGuilds"] = ("Все гильдии", "All guilds"),
        ["NoGuild"] = ("Без гильдии", "No guild"),
        ["GuildFilter"] = ("Гильдия:", "Guild:"),
        ["Legend"] = ("Рамка по итогам сверки: зелёная — сдано, голубая — сдал другой игрок, красная — не сдано, жёлтая — потеряно, фиолетовая — погиб с лутом.", "Frame after the chest check: green — deposited, blue — deposited by another player, red — not deposited, yellow — lost, purple — died with it."),
        ["IconsTitle"] = ("Иконки предметов", "Item icons"),
        ["IconsProgress"] = ("Скачано {0:N0} из {1:N0}. Загрузка идёт в фоне один раз, дальше иконки берутся с диска.", "Downloaded {0:N0} of {1:N0}. This happens once in the background; after that icons are read from disk."),
        ["IconsPaused"] = ("Сервер иконок недоступен, загрузка приостановлена ({0:N0} из {1:N0}). Вместо картинок показываются подписи; загрузка продолжится при следующем запуске.", "The icon server is unavailable, download paused ({0:N0} of {1:N0}). Labels are shown instead; it resumes on the next start."),
        ["IconsDone"] = ("Все иконки на диске ({0:N0}). Иконки новых предметов докачиваются сами.", "All icons are on disk ({0:N0}). Icons for new items are fetched automatically."),
        ["SendToDiscord"] = ("В Discord", "To Discord"),
        ["SendToDiscordTip"] = ("Отправить показанные записи журнала (CSV) в канал Discord из настроек", "Send the shown log entries (CSV) to the Discord channel from Settings"),
        ["DiscordTitle"] = ("Отправка в Discord", "Send to Discord"),
        ["DiscordHint"] = ("Вставьте ссылку-вебхук канала: в Discord откройте настройки канала → «Интеграции» → «Вебхуки» → «Новый вебхук» → «Копировать URL». Ссылка хранится только на этом компьютере — не показывайте её посторонним: по ней любой может писать в канал.",
            "Paste the channel webhook URL: in Discord open channel settings → Integrations → Webhooks → New Webhook → Copy Webhook URL. The URL is stored only on this computer — keep it private: anyone with it can post to the channel."),
        ["DiscordNotSet"] = ("Вебхук не указан.", "Webhook is not set."),
        ["DiscordReady"] = ("Готово: кнопка «В Discord» во вкладке журнала отправит файл в этот канал.", "Ready: the “To Discord” button on the log tab will send the file to this channel."),
        ["DiscordInvalid"] = ("Это не похоже на ссылку-вебхук Discord (должна начинаться с https://discord.com/api/webhooks/).", "This doesn't look like a Discord webhook URL (it should start with https://discord.com/api/webhooks/)."),
        ["DiscordSetupFirst"] = ("Сначала укажите ссылку-вебхук канала во вкладке «Настройки».", "Set the channel webhook URL on the Settings tab first."),
        ["DiscordNothing"] = ("В журнале нет записей для отправки (с текущими фильтрами).", "There are no log entries to send (with the current filters)."),
        ["DiscordMessage"] = ("📦 Журнал лута: {0:N0} записей на {1:N0} серебра · {2:dd.MM HH:mm}–{3:HH:mm}{4}", "📦 Loot log: {0:N0} entries worth {1:N0} silver · {2:dd.MM HH:mm}–{3:HH:mm}{4}"),
        ["DiscordSent"] = ("Отправлено в Discord: {0:N0} записей.", "Sent to Discord: {0:N0} entries."),
        ["DiscordFailed"] = ("Не удалось отправить в Discord.\n{0}", "Could not send to Discord.\n{0}"),
        ["DiscordWebhookGone"] = ("Вебхук не найден — возможно, его удалили. Создайте новый и вставьте ссылку в настройках.", "Webhook not found — it may have been deleted. Create a new one and paste the URL in Settings."),
        ["DiscordTooLarge"] = ("Файл слишком большой для канала.", "The file is too large for the channel."),
        ["DiscordRateLimited"] = ("Discord просит подождать: слишком много отправок. Попробуйте через минуту.", "Discord asks to wait: too many uploads. Try again in a minute."),
        ["DiscordTimeout"] = ("Discord не ответил вовремя. Проверьте интернет (и VPN, если Discord у вас открывается только через него).", "Discord did not respond in time. Check your internet (and VPN, if Discord only works through it)."),
        ["DiscordNetwork"] = ("Нет связи с Discord: {0}", "Cannot reach Discord: {0}"),
        ["TabSettings"] = ("Настройки", "Settings"),
        ["Language"] = ("Язык", "Language"),

        ["SearchTip"] = ("Поиск по игроку, гильдии, предмету или локации", "Search by player, guild, item or zone"),
        ["OnlyParty"] = ("Только моя группа", "Only my party"),
        ["OnlyPartyTip"] = ("Лут, который подобрал кто-то из вашей группы, или лут с тела члена вашей группы", "Loot picked up by your party members or looted from their bodies"),
        ["PlayerLoot"] = ("Лут с игроков", "Loot from players"),
        ["MobLoot"] = ("Лут с мобов и сундуков", "Loot from mobs and chests"),
        ["ShowMounts"] = ("Маунты", "Mounts"),
        ["ShowConsumables"] = ("Зелья и еда", "Potions and food"),
        ["OtherItemsHint"] = ("Прочее — от 50 000 за стопку", "Other — 50,000+ per stack"),
        ["OtherItemsTip"] = ("Экипировка (оружие, броня, плащи, сумки и т. п.) показывается всегда. Руны, души, реликвии, ресурсы, артефакты, инструменты для сбора и всё остальное, что нельзя надеть, — только если стопка стоит 50 000 серебра и дороже. В файл автозаписи пишется всё.",
            "Equipment (weapons, armor, capes, bags, etc.) is always shown. Runes, souls, relics, resources, artifacts, gathering tools and anything else you can't wear are shown only if the stack is worth 50,000 silver or more. Everything is still written to the autosave file."),
        ["ExportCsv"] = ("Экспорт CSV…", "Export CSV…"),
        ["ExportCsvTip"] = ("Сохранить показанные записи в файл", "Save the shown entries to a file"),
        ["ImportLogs"] = ("Загрузить журналы…", "Load logs…"),
        ["ImportLogsTip"] = ("Добавить журналы других лутлогеров (этой программы или Statistics Analysis Tool), например для сверки с сундуком",
            "Add logs from other loot loggers (this app or Statistics Analysis Tool), e.g. for the chest check"),
        ["NewSession"] = ("Новая сессия", "New session"),
        ["LogFolder"] = ("Папка с журналами", "Logs folder"),

        ["ColTime"] = ("Время", "Time"),
        ["ColLooter"] = ("Кто залутал", "Looted by"),
        ["ColGuild"] = ("Гильдия", "Guild"),
        ["ColItem"] = ("Предмет", "Item"),
        ["ColQty"] = ("Кол-во", "Qty"),
        ["ColValue"] = ("Стоимость", "Value"),
        ["ColFrom"] = ("С кого", "Looted from"),
        ["ColVictimGuild"] = ("Гильдия жертвы", "Victim guild"),
        ["ColZone"] = ("Локация", "Zone"),
        ["ColPlayer"] = ("Игрок", "Player"),
        ["ColLooted"] = ("Залутал", "Looted"),
        ["ColDeposited"] = ("Сдал", "Deposited"),
        ["ColNotDeposited"] = ("Не сдал", "Missing"),
        ["ColLost"] = ("Потерял", "Lost"),
        ["ColExtra"] = ("Сдал сверх", "Extra"),
        ["ColPercent"] = ("Сдано, %", "Deposited, %"),
        ["ColNotDepositedValue"] = ("Не сдано на", "Missing value"),
        ["ColStatus"] = ("Статус", "Status"),
        ["ColLootedAt"] = ("Залутано", "Looted at"),
        ["ColDepositedAt"] = ("Сдано", "Deposited at"),

        ["ChestLogTitle"] = ("Лог гильдейского сундука", "Guild chest log"),
        ["ChestLogHint"] = ("В игре откройте сундук → логи, скопируйте их кнопкой копирования и вставьте сюда. Можно добавить несколько логов подряд.",
            "In the game open the chest → logs, copy them with the copy button and paste them here. You can add several logs one after another."),
        ["Paste"] = ("Вставить из буфера", "Paste from clipboard"),
        ["FromFile"] = ("Из файла…", "From file…"),
        ["Clear"] = ("Очистить", "Clear"),
        ["ChestUtc"] = ("Время в логе сундука — UTC (как в игре)", "Chest log times are UTC (as in the game)"),
        ["ChestUtcTip"] = ("Снимите галочку, если время в логе указано по часам вашего компьютера", "Uncheck if the log uses your computer's local time"),
        ["Compare"] = ("Сравнить", "Compare"),
        ["ExportReport"] = ("Экспорт отчёта…", "Export report…"),
        ["OnlyProblems"] = ("Показывать только не сданное", "Show only items not deposited"),
        ["PlayerItems"] = ("Предметы игрока {0}", "Items of {0}"),

        ["StatusNotDeposited"] = ("Не сдано", "Not deposited"),
        ["StatusDeposited"] = ("Сдано", "Deposited"),
        ["StatusLost"] = ("Потеряно (залутали с него)", "Lost (looted from them)"),
        ["StatusDied"] = ("Погиб с этим лутом (возможно, уничтожено)", "Died with this loot (possibly destroyed)"),
        ["ColDied"] = ("Погиб с лутом", "Died with"),
        ["StatusByOther"] = ("Сдано другим игроком", "Deposited by another player"),
        ["StatusByOtherNamed"] = ("Сдал другой игрок: {0}", "Deposited by {0}"),
        ["StatusForOther"] = ("Сдал за другого игрока", "Deposited for another player"),
        ["StatusForOtherNamed"] = ("Сдал за {0}", "Deposited for {0}"),
        ["ColByOther"] = ("Сдал другой", "By other"),
        ["ColByOtherTip"] = ("Лут не сдан самим игроком, но такой же предмет позже сдал другой игрок его гильдии (скорее всего, передали трейдом). Считается сданным.",
            "Loot not deposited by the player, but the same item was later deposited by another player of their guild (most likely traded). Counted as deposited."),
        ["StatusExtra"] = ("Сдано, нет в журнале лута", "Deposited, not in loot log"),
        ["StatusBeforeLog"] = ("Старая запись — сдано до начала журнала", "Old record — deposited before the log started"),
        ["OldDepositTip"] = ("Сдано раньше первой записи в журнале лута — к этой сверке не относится и нигде не учитывается.",
            "Deposited before the first loot log entry — not part of this check and not counted anywhere."),
        ["OldPlayerTip"] = ("У игрока только старые записи сундука (раньше первого лута в журнале) — они нигде не учитываются.",
            "This player only has old chest records (before the first loot entry) — they are not counted anywhere."),
        ["CompareOldLines"] = ("Старых записей сундука (раньше первого лута в журнале) не учтено: {0:N0}",
            "Old chest records ignored (before the first loot entry): {0:N0}"),

        ["GameFolder"] = ("Папка игры", "Game folder"),
        ["Browse"] = ("Выбрать…", "Browse…"),
        ["Open"] = ("Открыть", "Open"),
        ["Adapter"] = ("Сетевой адаптер", "Network adapter"),
        ["Apply"] = ("Применить", "Apply"),
        ["RefreshList"] = ("Обновить список", "Refresh list"),
        ["AdapterAuto"] = ("Авто (рекомендуется)", "Auto (recommended)"),
        ["AdapterHint"] = ("«Авто» слушает все адаптеры и сам находит трафик игры. С VPN, ExitLag или GearUP трафик может идти через другие порты — тогда через 15 секунд программа начнёт искать игру по содержимому пакетов на всех портах. Ручной выбор адаптера нужен редко. Если данные так и не пошли, нажмите «Копировать диагностику» и пришлите текст.",
            "“Auto” listens on all adapters and finds the game traffic itself. With a VPN, ExitLag or GearUP the traffic may use other ports — then after 15 seconds the app starts looking for the game by packet contents on all ports. Picking an adapter manually is rarely needed. If data still does not arrive, click “Copy diagnostics” and send the text."),
        ["Autosave"] = ("Автозапись журнала", "Log autosave"),
        ["AutosaveHint"] = ("Каждая сессия пишется в отдельный CSV-файл сразу по мере лута — даже при вылете программы журнал сохранится. Формат совместим со Statistics Analysis Tool.",
            "Each session is written to its own CSV file as loot happens, so nothing is lost even if the app crashes. The format is compatible with Statistics Analysis Tool."),
        ["ValueTitle"] = ("Стоимость предметов", "Item value"),
        ["ValueHint"] = ("Стоимость — оценочная рыночная цена, которую сервер игры присылает вместе с данными предмета. Интернет не нужен. Цены запоминаются между запусками, поэтому со временем у большего числа предметов будет известна стоимость.",
            "Value is the estimated market value the game server sends along with item data. No internet is needed. Prices are remembered between runs, so over time more items will have a known value."),

        ["CaptureStarting"] = ("Запуск…", "Starting…"),
        ["CaptureStopped"] = ("Захват остановлен", "Capture stopped"),
        ["CaptureFailed"] = ("Захват не работает: {0}", "Capture is not working: {0}"),
        ["NoAdapters"] = ("Не удалось открыть ни один сетевой адаптер.", "Could not open any network adapter."),
        ["AdapterNotFound"] = ("Выбранный адаптер не найден или недоступен. Выберите другой или «Авто».", "The selected adapter is not available. Choose another one or “Auto”."),
        ["DataFlowing"] = ("Данные игры идут · {0} · пакетов: {1:N0}", "Game data is flowing · {0} · packets: {1:N0}"),
        ["WaitingTraffic"] = ("Жду трафик игры… (открыто адаптеров: {0})", "Waiting for game traffic… (adapters open: {0})"),
        ["DataFlowingProxy"] = ("Данные игры идут (через ускоритель/прокси) · {0} · пакетов: {1:N0}", "Game data is flowing (via booster/proxy) · {0} · packets: {1:N0}"),
        ["SearchingAllPorts"] = ("На стандартных портах игры тишина — ищу трафик игры на всех портах (GearUP, ExitLag, VPN)… Адаптеров: {0}, просмотрено UDP-пакетов: {1:N0}",
            "No traffic on the standard game ports — searching all ports (GearUP, ExitLag, VPN)… Adapters: {0}, UDP packets checked: {1:N0}"),
        ["DiagMode"] = ("Режим: {0} · открыто адаптеров: {1}", "Mode: {0} · adapters open: {1}"),
        ["DiagModePorts"] = ("стандартные порты игры", "standard game ports"),
        ["DiagModeDiscovery"] = ("поиск на всех портах", "searching all ports"),
        ["DiagNoPackets"] = ("Пакетов пока не было.", "No packets yet."),
        ["DiagRow"] = ("UDP {0,9:N0} · похоже на игру {1,7:N0} · принято {2,7:N0} · {3}", "UDP {0,9:N0} · game-like {1,7:N0} · accepted {2,7:N0} · {3}"),
        ["DiagFlows"] = ("Самые активные UDP-потоки (порты, пакетов, состояние, адаптер, первые байты):", "Most active UDP flows (ports, packets, state, adapter, first bytes):"),
        ["AuthorDiscordTip"] = ("Связь с автором: открыть профиль в Discord.", "Contact the author: open the Discord profile."),
        ["CopyNick"] = ("Копировать ник", "Copy username"),
        ["Copied"] = ("скопировано", "copied"),
        ["CopyDiagnostics"] = ("Копировать диагностику", "Copy diagnostics"),
        ["NoTrafficNow"] = ("Трафика игры сейчас нет", "No game traffic right now"),
        ["OpeningAdapters"] = ("Открываю сетевые адаптеры…", "Opening network adapters…"),
        ["PlayerUnknown"] = ("Персонаж: смените локацию или перезайдите, чтобы программа узнала ваше имя", "Character: change zone or relog so the app learns your name"),
        ["PlayerLine"] = ("Персонаж: {0}{1} · Локация: {2} · В группе: {3}", "Character: {0}{1} · Zone: {2} · Party: {3}"),
        ["AutosaveError"] = ("Ошибка автозаписи: {0}", "Autosave error: {0}"),
        ["AutosaveTo"] = ("Автозапись: {0}", "Autosave: {0}"),
        ["NoEntries"] = ("Записей пока нет", "No entries yet"),
        ["JournalSummary"] = ("Записей: {0:N0} · показано: {1:N0} · стоимость показанного: {2:N0} серебра", "Entries: {0:N0} · shown: {1:N0} · shown value: {2:N0} silver"),
        ["NpcapMissing"] = ("Npcap не найден. Установите Npcap (кнопка справа) и перезапустите программу. Подробности: {0}", "Npcap was not found. Install Npcap (button on the right) and restart the app. Details: {0}"),
        ["DownloadNpcap"] = ("Скачать Npcap", "Download Npcap"),

        ["AskGameFolder"] = ("Укажите папку, куда установлен Albion Online.\n\nИз файлов игры берутся названия предметов и локаций.", "Select the folder where Albion Online is installed.\n\nItem and zone names are read from the game files."),
        ["GameFolderDialog"] = ("Папка Albion Online (например, C:\\Program Files (x86)\\AlbionOnline)", "Albion Online folder (e.g. C:\\Program Files (x86)\\AlbionOnline)"),
        ["GameFolderInvalid"] = ("В этой папке не найдены файлы игры. Выберите папку, куда установлен Albion Online.", "No game files were found in this folder. Choose the folder where Albion Online is installed."),
        ["GameFolderMissing"] = ("Папка игры не указана — вместо названий предметов будут номера.", "The game folder is not set — items will be shown as numbers."),
        ["GameFolderWarning"] = ("Укажите папку игры во вкладке «Настройки».", "Set the game folder on the “Settings” tab."),
        ["LoadingGameData"] = ("Загрузка данных игры…", "Loading game data…"),
        ["LoadingNames"] = ("Читаю названия предметов из файлов игры…", "Reading item names from the game files…"),
        ["LoadingItems"] = ("Читаю список предметов…", "Reading the item list…"),
        ["LoadingClusters"] = ("Читаю список локаций…", "Reading the zone list…"),
        ["GameDataLoaded"] = ("Предметов: {0:N0}, локаций: {1:N0}, известных цен: {2:N0}", "Items: {0:N0}, zones: {1:N0}, known prices: {2:N0}"),
        ["GameDataError"] = ("Не удалось прочитать файлы игры: {0}", "Could not read the game files: {0}"),

        ["NewSessionConfirm"] = ("Очистить журнал и начать новый файл автозаписи?\nТекущий файл останется на диске.", "Clear the log and start a new autosave file?\nThe current file stays on disk."),
        ["ExportTitle"] = ("Экспорт журнала", "Export log"),
        ["Exported"] = ("Сохранено записей: {0}", "Entries saved: {0}"),
        ["ExportError"] = ("Ошибка экспорта", "Export error"),
        ["ImportTitle"] = ("Загрузить журналы лута", "Load loot logs"),
        ["AllFiles"] = ("Все файлы", "All files"),
        ["TextFiles"] = ("Текст", "Text"),
        ["NotALog"] = ("{0} — не похож на журнал лута", "{0} — not a loot log"),
        ["ImportResult"] = ("Добавлено записей: {0}\nПропущено дублей: {1}", "Entries added: {0}\nDuplicates skipped: {1}"),
        ["ImportFailed"] = ("Не загружены:", "Not loaded:"),
        ["LogFolderDialog"] = ("Папка для автозаписи журналов", "Folder for log autosave"),
        ["ChestFilesTitle"] = ("Логи сундука", "Chest logs"),
        ["CompareHint"] = ("Вставьте лог сундука и нажмите «Сравнить». Учитываются записи журнала и строки лога сундука с фильтрами выше.",
            "Paste the chest log and click “Compare”. Log entries and chest log lines are taken with the filters above."),
        ["CompareSummary"] = ("Лута в журнале: {0:N0} · строк лога сундука: {1:N0} · не сдано на {2:N0} серебра", "Loot in log: {0:N0} · chest log lines: {1:N0} · not deposited: {2:N0} silver"),
        ["UnknownItems"] = ("Не распознано предметов в логе сундука: {0:N0} (например: {1})", "Unrecognized items in the chest log: {0:N0} (e.g. {1})"),
        ["NoChestLines"] = ("В тексте не найдено ни одной строки лога сундука. Скопируйте лог из окна сундука в игре целиком.", "No chest log lines were found. Copy the whole log from the chest window in the game."),
        ["CompareError"] = ("Ошибка сверки: {0}", "Comparison error: {0}"),
        ["ReportTitle"] = ("Отчёт сверки", "Comparison report"),
        ["ReportHeader"] = ("Игрок;Гильдия;Предмет;Количество;Стоимость;С кого;Время лута;Время сдачи;Статус", "Player;Guild;Item;Quantity;Value;Looted from;Looted at;Deposited at;Status"),
        ["Error"] = ("Ошибка", "Error"),
        ["ClipboardTitle"] = ("Буфер обмена", "Clipboard"),

        ["Mob"] = ("Моб", "Mob"),
        ["Chest"] = ("Сундук", "Chest"),
        ["Instance"] = ("инстанс", "instance"),
        ["AlreadyRunning"] = ("Лутлогер уже запущен.", "The loot logger is already running."),
        ["UnhandledError"] = ("Произошла ошибка:\n{0}\n\nПодробности записаны в {1}", "An error occurred:\n{0}\n\nDetails were written to {1}")
    };
}

/// <summary>Перевод в XAML: Text="{local:Tr Ключ}". Обновляется при смене языка.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}

/// <summary>Служебные имена MOB/CHEST → «Моб»/«Mob» и т. п.</summary>
public sealed class SourceNameConverter : IValueConverter
{
    public static SourceNameConverter Instance { get; } = new();

    public static string Translate(string? name)
    {
        return name switch
        {
            LootTracker.MobName => Loc.T("Mob"),
            LootTracker.ChestName => Loc.T("Chest"),
            _ => name ?? string.Empty
        };
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Translate(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class LootStatusConverter : IValueConverter
{
    public static LootStatusConverter Instance { get; } = new();

    public static string Translate(LootStatus status)
    {
        return Loc.T(status switch
        {
            LootStatus.NotDeposited => "StatusNotDeposited",
            LootStatus.Deposited => "StatusDeposited",
            LootStatus.Lost => "StatusLost",
            LootStatus.DiedWithLoot => "StatusDied",
            LootStatus.DepositedWithoutLoot => "StatusExtra",
            LootStatus.DepositedByOther => "StatusByOther",
            LootStatus.DepositedForOther => "StatusForOther",
            _ => "StatusBeforeLog"
        });
    }

    /// <summary>Статус строки сверки с именем второго игрока («Сдал Коля», «Сдал за Васю»).</summary>
    public static string Translate(ComparisonItem item)
    {
        return item.Status switch
        {
            LootStatus.DepositedByOther when item.OtherPlayer.Length > 0 => Loc.F("StatusByOtherNamed", item.OtherPlayer),
            LootStatus.DepositedForOther when item.OtherPlayer.Length > 0 => Loc.F("StatusForOtherNamed", item.OtherPlayer),
            _ => Translate(item.Status)
        };
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ComparisonItem item => Translate(item),
        LootStatus status => Translate(status),
        _ => string.Empty
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed record LanguageOption(bool IsEnglish, string Name);
