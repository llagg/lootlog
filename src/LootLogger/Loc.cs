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
        ["TabSettings"] = ("Настройки", "Settings"),
        ["Language"] = ("Язык", "Language"),

        ["SearchTip"] = ("Поиск по игроку, гильдии, предмету или локации", "Search by player, guild, item or zone"),
        ["OnlyParty"] = ("Только моя группа", "Only my party"),
        ["OnlyPartyTip"] = ("Лут, который подобрал кто-то из вашей группы, или лут с тела члена вашей группы", "Loot picked up by your party members or looted from their bodies"),
        ["PlayerLoot"] = ("Лут с игроков", "Loot from players"),
        ["MobLoot"] = ("Лут с мобов и сундуков", "Loot from mobs and chests"),
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
        ["StatusExtra"] = ("Сдано, нет в журнале лута", "Deposited, not in loot log"),
        ["StatusBeforeLog"] = ("Сдано до начала журнала", "Deposited before the log started"),

        ["GameFolder"] = ("Папка игры", "Game folder"),
        ["Browse"] = ("Выбрать…", "Browse…"),
        ["Open"] = ("Открыть", "Open"),
        ["Adapter"] = ("Сетевой адаптер", "Network adapter"),
        ["Apply"] = ("Применить", "Apply"),
        ["RefreshList"] = ("Обновить список", "Refresh list"),
        ["AdapterAuto"] = ("Авто (рекомендуется)", "Auto (recommended)"),
        ["AdapterHint"] = ("«Авто» слушает все адаптеры и сам выбирает тот, где идёт трафик игры. С VPN или ExitLag это обычно работает без настройки. Если данные не идут дольше минуты в игре — выберите адаптер VPN/ExitLag вручную и нажмите «Применить».",
            "“Auto” listens on all adapters and picks the one carrying game traffic. With a VPN or ExitLag this usually works without setup. If no data arrives for more than a minute in game, select the VPN/ExitLag adapter manually and click “Apply”."),
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
        ["CompareHint"] = ("Вставьте лог сундука и нажмите «Сравнить». Учитываются записи журнала с текущими фильтрами (группа / игроки / мобы).",
            "Paste the chest log and click “Compare”. Log entries are taken with the current filters (party / players / mobs)."),
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
            LootStatus.DepositedWithoutLoot => "StatusExtra",
            _ => "StatusBeforeLog"
        });
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is LootStatus status ? Translate(status) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed record LanguageOption(bool IsEnglish, string Name);
