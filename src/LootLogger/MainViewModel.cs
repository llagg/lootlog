using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using LootLogger.Core;
using LootLogger.Core.Capture;
using LootLogger.Core.Comparator;
using LootLogger.Core.Game;
using LootLogger.Core.Loot;
using LootLogger.Core.Storage;
using Microsoft.Win32;

namespace LootLogger;

public sealed class MainViewModel : ObservableObject
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private readonly AppSettings _settings;
    private readonly ConcurrentQueue<LootEntry> _incomingLoot = new();
    private readonly List<KillEntry> _kills = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _searchTimer;
    private readonly PriceBook _prices = new();

    private LootEngine? _engine;
    private AutoSaveWriter? _writer;
    private GameDatabase _game = GameDatabase.Empty;
    private int _statusTick;

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        _onlyParty = settings.OnlyParty;
        _showPlayerLoot = settings.ShowPlayerLoot;
        _showMobLoot = settings.ShowMobLoot;
        _chestTimesUtc = settings.ChestLogTimesUtc;
        _gameDirectory = settings.GameDirectory;

        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        EntriesView.Filter = o => o is LootEntry e && IsVisible(e, applySearch: true);

        _uiTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => OnUiTick(), Application.Current.Dispatcher);
        _searchTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) =>
        {
            _searchTimer!.Stop();
            RefreshFilter();
        }, Application.Current.Dispatcher);
        _searchTimer.Stop();

        ExportCsvCommand = new RelayCommand(ExportCsv);
        ImportLogCommand = new RelayCommand(ImportLog);
        NewSessionCommand = new RelayCommand(NewSession);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
        RefreshAdaptersCommand = new RelayCommand(RefreshAdapters);
        RestartCaptureCommand = new RelayCommand(() => _ = RestartCaptureAsync());
        BrowseGameFolderCommand = new RelayCommand(() => _ = BrowseGameFolderAsync());
        PasteChestLogCommand = new RelayCommand(PasteChestLog);
        LoadChestLogCommand = new RelayCommand(LoadChestLog);
        ClearChestLogCommand = new RelayCommand(() => ChestLogText = string.Empty);
        CompareCommand = new RelayCommand(() => _ = CompareAsync(), () => !IsComparing);
        ExportComparisonCommand = new RelayCommand(ExportComparison, () => ComparisonResults.Count > 0);
        OpenNpcapSiteCommand = new RelayCommand(() => OpenUrl("https://npcap.com/#download"));
        BrowseLogFolderCommand = new RelayCommand(BrowseLogFolder);
    }

    #region Журнал

    public ObservableCollection<LootEntry> Entries { get; } = new();
    public ICollectionView EntriesView { get; }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value))
            {
                _searchTimer.Stop();
                _searchTimer.Start();
            }
        }
    }

    private bool _onlyParty;
    public bool OnlyParty
    {
        get => _onlyParty;
        set
        {
            if (Set(ref _onlyParty, value))
            {
                _settings.OnlyParty = value;
                RefreshFilter();
            }
        }
    }

    private bool _showPlayerLoot;
    public bool ShowPlayerLoot
    {
        get => _showPlayerLoot;
        set
        {
            if (Set(ref _showPlayerLoot, value))
            {
                _settings.ShowPlayerLoot = value;
                RefreshFilter();
            }
        }
    }

    private bool _showMobLoot;
    public bool ShowMobLoot
    {
        get => _showMobLoot;
        set
        {
            if (Set(ref _showMobLoot, value))
            {
                _settings.ShowMobLoot = value;
                RefreshFilter();
            }
        }
    }

    private int _shownCount;
    private long _shownValue;
    private string _journalSummary = "Записей пока нет";
    public string JournalSummary
    {
        get => _journalSummary;
        private set => Set(ref _journalSummary, value);
    }

    /// <summary>Фильтры «группа / игроки / мобы» — общие для журнала, экспорта и сверки. Поиск — только для журнала.</summary>
    private bool IsVisible(LootEntry e, bool applySearch)
    {
        if (_onlyParty && !e.IsPartyRelated)
        {
            return false;
        }

        if (e.IsFromMob ? !_showMobLoot : !_showPlayerLoot)
        {
            return false;
        }

        if (!applySearch || string.IsNullOrWhiteSpace(_searchText))
        {
            return true;
        }

        var q = _searchText.Trim();
        return Contains(e.LootedBy, q) || Contains(e.LootedByGuild, q) || Contains(e.LootedByAlliance, q)
               || Contains(e.LootedFrom, q) || Contains(e.LootedFromGuild, q)
               || Contains(e.ItemName, q) || Contains(e.UniqueName, q) || Contains(e.Cluster, q);

        static bool Contains(string value, string q) => value.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshFilter()
    {
        EntriesView.Refresh();
        _shownCount = 0;
        _shownValue = 0;
        foreach (var e in Entries)
        {
            if (IsVisible(e, applySearch: true))
            {
                _shownCount++;
                _shownValue += e.TotalValue;
            }
        }

        UpdateJournalSummary();
    }

    private void UpdateJournalSummary()
    {
        JournalSummary = Entries.Count == 0
            ? "Записей пока нет"
            : string.Format(Ru, "Записей: {0:N0} · показано: {1:N0} · стоимость показанного: {2:N0} серебра", Entries.Count, _shownCount, _shownValue);
    }

    private void AddEntry(LootEntry entry)
    {
        Entries.Insert(0, entry);
        if (IsVisible(entry, applySearch: true))
        {
            _shownCount++;
            _shownValue += entry.TotalValue;
        }
    }

    #endregion

    #region Статус

    private string _captureStatus = "Запуск…";
    public string CaptureStatus { get => _captureStatus; private set => Set(ref _captureStatus, value); }

    private bool _isCaptureOk;
    public bool IsCaptureOk { get => _isCaptureOk; private set => Set(ref _isCaptureOk, value); }

    private bool _isGameDataSeen;
    public bool IsGameDataSeen { get => _isGameDataSeen; private set => Set(ref _isGameDataSeen, value); }

    private string _playerStatus = "Персонаж: войдите в игру или смените локацию";
    public string PlayerStatus { get => _playerStatus; private set => Set(ref _playerStatus, value); }

    private string _gameDataStatus = string.Empty;
    public string GameDataStatus { get => _gameDataStatus; private set => Set(ref _gameDataStatus, value); }

    private string _warning = string.Empty;
    public string Warning
    {
        get => _warning;
        private set
        {
            if (Set(ref _warning, value))
            {
                OnPropertyChanged(nameof(HasWarning));
            }
        }
    }

    public bool HasWarning => !string.IsNullOrEmpty(_warning);

    private bool _isNpcapMissing;
    public bool IsNpcapMissing { get => _isNpcapMissing; private set => Set(ref _isNpcapMissing, value); }

    private string _autosaveStatus = string.Empty;
    public string AutosaveStatus { get => _autosaveStatus; private set => Set(ref _autosaveStatus, value); }

    private void OnUiTick()
    {
        var added = 0;
        while (added < 2000 && _incomingLoot.TryDequeue(out var entry))
        {
            AddEntry(entry);
            added++;
        }

        if (added > 0)
        {
            UpdateJournalSummary();
        }

        // статус обновляем раз в секунду
        if (++_statusTick % 3 == 0)
        {
            UpdateStatus();
        }
    }

    private void UpdateStatus()
    {
        var engine = _engine;
        if (engine == null)
        {
            return;
        }

        var capture = engine.Capture;
        var tracker = engine.Tracker;
        var sinceLastPacket = DateTime.UtcNow - capture.LastGamePacketUtc;
        IsGameDataSeen = sinceLastPacket < TimeSpan.FromSeconds(10);

        if (!capture.IsRunning)
        {
            IsCaptureOk = false;
            CaptureStatus = string.IsNullOrEmpty(capture.LastError) ? "Захват остановлен" : $"Захват не работает: {capture.LastError}";
        }
        else if (IsGameDataSeen)
        {
            IsCaptureOk = true;
            CaptureStatus = $"Данные игры идут · {capture.ActiveAdapterName} · пакетов: {capture.GamePackets.ToString("N0", Ru)}";
        }
        else
        {
            IsCaptureOk = true;
            CaptureStatus = capture.GamePackets == 0
                ? $"Жду трафик игры… (открыто адаптеров: {capture.OpenedAdapters})"
                : "Трафика игры сейчас нет";
        }

        PlayerStatus = string.IsNullOrEmpty(tracker.LocalPlayerName)
            ? "Персонаж: смените локацию или перезайдите, чтобы программа узнала ваше имя"
            : $"Персонаж: {tracker.LocalPlayerName}{(string.IsNullOrEmpty(tracker.LocalGuild) ? string.Empty : $" [{tracker.LocalGuild}]")}"
              + $" · Локация: {(string.IsNullOrEmpty(tracker.CurrentCluster) ? "—" : tracker.CurrentCluster)}"
              + $" · В группе: {tracker.PartySize}";

        var writer = _writer;
        if (writer != null)
        {
            AutosaveStatus = !string.IsNullOrEmpty(writer.LastError)
                ? $"Ошибка автозаписи: {writer.LastError}"
                : string.IsNullOrEmpty(writer.CurrentFile)
                    ? $"Автозапись: {writer.Directory}"
                    : $"Автозапись: {writer.CurrentFile}";
        }
    }

    #endregion

    #region Запуск и остановка

    public async Task InitializeAsync(Window owner)
    {
        _uiTimer.Start();
        Directory.CreateDirectory(AppPaths.DataDirectory);

        // 1. Npcap
        if (!NpcapCapture.IsNpcapAvailable(out var npcapError))
        {
            IsNpcapMissing = true;
            Warning = "Npcap не найден. Установите Npcap (кнопка справа) и перезапустите программу. " +
                      $"Подробности: {npcapError}";
        }

        // 2. Папка игры
        var gameDirectory = GameDataLoader.ResolveGameDirectory(_settings.GameDirectory)
                            ?? await Task.Run(GameDataLoader.DetectGameDirectory);
        if (gameDirectory == null)
        {
            MessageBox.Show(owner,
                "Укажите папку, куда установлен Albion Online.\n\nИз файлов игры берутся названия предметов на русском и названия локаций.",
                "Лутлогер", MessageBoxButton.OK, MessageBoxImage.Information);
            gameDirectory = AskGameDirectory(owner);
        }

        await LoadGameDataAsync(gameDirectory);

        // 3. Захват и автозапись
        _writer = new AutoSaveWriter(_settings.EffectiveLogDirectory);
        _engine = new LootEngine(_game, _prices);
        _engine.Tracker.LootRecorded += OnLootRecorded;
        _engine.Tracker.KillRecorded += OnKillRecorded;

        RefreshAdapters();
        if (!IsNpcapMissing)
        {
            await StartCaptureAsync();
        }

        UpdateStatus();
    }

    public void Shutdown()
    {
        _uiTimer.Stop();
        try
        {
            _engine?.Dispose();
        }
        catch
        {
            // ignored
        }

        _writer?.Dispose();
        _prices.Save(AppPaths.PricesFile, _game.Items);
        _settings.Save();
    }

    private void OnLootRecorded(LootEntry entry)
    {
        // вызывается в потоке захвата: только кладём в очереди, ничего не ждём
        _incomingLoot.Enqueue(entry);
        _writer?.Add(entry);
    }

    private void OnKillRecorded(KillEntry entry)
    {
        _writer?.Add(entry);
        lock (_kills)
        {
            _kills.Add(entry);
        }
    }

    private async Task StartCaptureAsync()
    {
        var engine = _engine;
        if (engine == null)
        {
            return;
        }

        CaptureStatus = "Открываю сетевые адаптеры…";
        var adapterId = _selectedAdapter?.Id ?? _settings.AdapterId;
        await Task.Run(() =>
        {
            engine.Stop();
            engine.Start(adapterId);
        });
        UpdateStatus();
    }

    private async Task RestartCaptureAsync()
    {
        if (_selectedAdapter != null)
        {
            _settings.AdapterId = _selectedAdapter.Id;
        }

        await StartCaptureAsync();
    }

    #endregion

    #region Данные игры

    private string _gameDirectory;
    public string GameDirectory { get => _gameDirectory; private set => Set(ref _gameDirectory, value); }

    private async Task LoadGameDataAsync(string? gameDirectory)
    {
        if (gameDirectory == null)
        {
            GameDataStatus = "Папка игры не указана — вместо названий предметов будут номера.";
            Warning = string.IsNullOrEmpty(Warning) ? "Укажите папку игры во вкладке «Настройки»." : Warning;
            return;
        }

        GameDirectory = gameDirectory;
        _settings.GameDirectory = gameDirectory;
        GameDataStatus = "Загрузка данных игры…";

        try
        {
            var progress = new Progress<string>(text => GameDataStatus = text);
            _game = await GameDataLoader.LoadAsync(gameDirectory, AppPaths.CacheDirectory, progress);
            _prices.Load(AppPaths.PricesFile, _game.Items);
            if (_engine != null)
            {
                _engine.Tracker.Game = _game;
            }

            GameDataStatus = string.Format(Ru, "Предметов: {0:N0}, локаций: {1:N0}, известных цен: {2:N0}", _game.Items.Count, _game.ClusterNames.Count, _prices.Count);
        }
        catch (Exception ex)
        {
            GameDataStatus = $"Не удалось прочитать файлы игры: {ex.Message}";
        }
    }

    private static string? AskGameDirectory(Window? owner)
    {
        while (true)
        {
            var dialog = new OpenFolderDialog { Title = "Папка Albion Online (например, C:\\Program Files (x86)\\AlbionOnline)" };
            var ok = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
            if (ok != true)
            {
                return null;
            }

            var resolved = GameDataLoader.ResolveGameDirectory(dialog.FolderName);
            if (resolved != null)
            {
                return resolved;
            }

            MessageBox.Show(owner, "В этой папке не найдены файлы игры. Выберите папку, куда установлен Albion Online.",
                "Лутлогер", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task BrowseGameFolderAsync()
    {
        var directory = AskGameDirectory(Application.Current.MainWindow);
        if (directory != null)
        {
            await LoadGameDataAsync(directory);
        }
    }

    #endregion

    #region Адаптеры

    public ObservableCollection<CaptureAdapter> Adapters { get; } = new();

    private CaptureAdapter? _selectedAdapter;
    public CaptureAdapter? SelectedAdapter { get => _selectedAdapter; set => Set(ref _selectedAdapter, value); }

    private void RefreshAdapters()
    {
        Adapters.Clear();
        Adapters.Add(new CaptureAdapter(NpcapCapture.AutoAdapterId, "Авто (рекомендуется)"));
        try
        {
            foreach (var adapter in NpcapCapture.ListAdapters())
            {
                Adapters.Add(adapter);
            }
        }
        catch
        {
            // Npcap не установлен — останется только «Авто»
        }

        SelectedAdapter = Adapters.FirstOrDefault(x => x.Id == _settings.AdapterId) ?? Adapters[0];
    }

    #endregion

    #region Файлы журнала

    public string LogDirectory => _settings.EffectiveLogDirectory;

    private void NewSession()
    {
        if (Entries.Count > 0 && MessageBox.Show(Application.Current.MainWindow,
                "Очистить журнал и начать новый файл автозаписи?\nТекущий файл останется на диске.",
                "Новая сессия", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        while (_incomingLoot.TryDequeue(out _))
        {
        }

        Entries.Clear();
        lock (_kills)
        {
            _kills.Clear();
        }

        _writer?.StartNewFile();
        RefreshFilter();
    }

    private void ExportCsv()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Экспорт журнала",
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"loot_export_{DateTime.Now:yyyy-MM-dd_HH-mm}.csv",
            InitialDirectory = EnsureDirectory(LogDirectory)
        };

        if (dialog.ShowDialog(Application.Current.MainWindow) != true)
        {
            return;
        }

        try
        {
            var visible = Entries.Where(e => IsVisible(e, applySearch: true)).OrderBy(e => e.UtcTime).ToList();
            LootCsv.Write(dialog.FileName, visible);
            MessageBox.Show(Application.Current.MainWindow, $"Сохранено записей: {visible.Count}", "Экспорт", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Application.Current.MainWindow, ex.Message, "Ошибка экспорта", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Добавить в журнал логи других лутлогеров (свои прошлые файлы или экспорт SAT).</summary>
    private void ImportLog()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Загрузить журналы лута",
            Filter = "CSV (*.csv)|*.csv|Все файлы (*.*)|*.*",
            Multiselect = true,
            InitialDirectory = EnsureDirectory(LogDirectory)
        };

        if (dialog.ShowDialog(Application.Current.MainWindow) != true)
        {
            return;
        }

        var added = 0;
        var duplicates = 0;
        var errors = new List<string>();
        var imported = new List<LootEntry>();
        var known = Entries
            .GroupBy(DuplicateKey)
            .ToDictionary(g => g.Key, g => g.Select(x => x.UtcTime).ToList());

        foreach (var file in dialog.FileNames)
        {
            try
            {
                var result = LootCsv.Read(file, _game.Items);
                if (!result.IsValid)
                {
                    errors.Add($"{Path.GetFileName(file)} — не похож на журнал лута");
                    continue;
                }

                foreach (var entry in result.Loot.OrderBy(x => x.UtcTime))
                {
                    var key = DuplicateKey(entry);
                    if (!known.TryGetValue(key, out var times))
                    {
                        times = new List<DateTime>();
                        known[key] = times;
                    }

                    // одна и та же запись могла попасть в разные журналы (два лутлогера в одной группе)
                    if (times.Any(t => Math.Abs((t - entry.UtcTime).TotalSeconds) <= 10))
                    {
                        duplicates++;
                        continue;
                    }

                    times.Add(entry.UtcTime);
                    imported.Add(entry);
                    added++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(file)} — {ex.Message}");
            }
        }

        // импорт мог добавить старые записи — пересортируем журнал по времени
        var sorted = Entries.Concat(imported).OrderByDescending(x => x.UtcTime).ToList();
        using (EntriesView.DeferRefresh())
        {
            Entries.Clear();
            foreach (var entry in sorted)
            {
                Entries.Add(entry);
            }
        }

        RefreshFilter();

        var message = $"Добавлено записей: {added}\nПропущено дублей: {duplicates}";
        if (errors.Count > 0)
        {
            message += "\n\nНе загружены:\n" + string.Join('\n', errors);
        }

        MessageBox.Show(Application.Current.MainWindow, message, "Загрузка журналов", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static (string By, string From, int Item, int Quantity) DuplicateKey(LootEntry e)
    {
        return (e.LootedBy.ToLowerInvariant(), e.LootedFrom.ToLowerInvariant(), e.ItemIndex, e.Quantity);
    }

    private void OpenLogFolder()
    {
        OpenPath(EnsureDirectory(LogDirectory));
    }

    private void BrowseLogFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Папка для автозаписи журналов", InitialDirectory = EnsureDirectory(LogDirectory) };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true)
        {
            return;
        }

        _settings.LogDirectory = dialog.FolderName;
        OnPropertyChanged(nameof(LogDirectory));

        var old = _writer;
        _writer = new AutoSaveWriter(_settings.EffectiveLogDirectory);
        old?.Dispose();
    }

    private static string EnsureDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch
        {
            // ignored
        }

        return path;
    }

    #endregion

    #region Сверка с сундуком

    private string _chestLogText = string.Empty;
    public string ChestLogText { get => _chestLogText; set => Set(ref _chestLogText, value); }

    private bool _chestTimesUtc;
    public bool ChestTimesUtc
    {
        get => _chestTimesUtc;
        set
        {
            if (Set(ref _chestTimesUtc, value))
            {
                _settings.ChestLogTimesUtc = value;
            }
        }
    }

    private bool _isComparing;
    public bool IsComparing { get => _isComparing; private set => Set(ref _isComparing, value); }

    private string _comparisonSummary = "Вставьте лог сундука и нажмите «Сравнить». Учитываются записи журнала с текущими фильтрами (группа / игроки / мобы).";
    public string ComparisonSummary { get => _comparisonSummary; private set => Set(ref _comparisonSummary, value); }

    public ObservableCollection<PlayerComparison> ComparisonResults { get; } = new();

    private PlayerComparison? _selectedPlayer;
    public PlayerComparison? SelectedPlayer
    {
        get => _selectedPlayer;
        set
        {
            if (Set(ref _selectedPlayer, value))
            {
                UpdateSelectedPlayerItems();
            }
        }
    }

    private bool _onlyProblems = true;
    public bool OnlyProblems
    {
        get => _onlyProblems;
        set
        {
            if (Set(ref _onlyProblems, value))
            {
                UpdateSelectedPlayerItems();
            }
        }
    }

    public ObservableCollection<ComparisonItem> SelectedPlayerItems { get; } = new();

    private void UpdateSelectedPlayerItems()
    {
        SelectedPlayerItems.Clear();
        if (_selectedPlayer == null)
        {
            return;
        }

        var items = _selectedPlayer.Items
            .Where(x => !_onlyProblems || x.Status == LootStatus.NotDeposited)
            .OrderBy(x => x.Status)
            .ThenByDescending(x => x.Value)
            .ThenBy(x => x.LootUtc ?? x.DepositUtc);

        foreach (var item in items)
        {
            SelectedPlayerItems.Add(item);
        }
    }

    private void PasteChestLog()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                ChestLogText = string.IsNullOrWhiteSpace(ChestLogText) ? text : ChestLogText.TrimEnd() + Environment.NewLine + text;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(Application.Current.MainWindow, ex.Message, "Буфер обмена", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LoadChestLog()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Логи сундука",
            Filter = "Текст (*.txt;*.csv;*.tsv)|*.txt;*.csv;*.tsv|Все файлы (*.*)|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog(Application.Current.MainWindow) != true)
        {
            return;
        }

        var sb = new StringBuilder(ChestLogText.TrimEnd());
        foreach (var file in dialog.FileNames)
        {
            try
            {
                if (sb.Length > 0)
                {
                    sb.AppendLine();
                }

                sb.Append(File.ReadAllText(file).TrimEnd());
            }
            catch (Exception ex)
            {
                MessageBox.Show(Application.Current.MainWindow, ex.Message, Path.GetFileName(file), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        ChestLogText = sb.ToString();
    }

    private async Task CompareAsync()
    {
        IsComparing = true;
        try
        {
            var loot = Entries.Where(e => IsVisible(e, applySearch: false)).ToList();
            var text = ChestLogText;
            var utc = ChestTimesUtc;
            var items = _game.Items;

            var (parse, results) = await Task.Run(() =>
            {
                var parsed = ChestLogParser.Parse(text, items, utc);
                return (parsed, LootComparator.Compare(loot, parsed.Entries));
            });

            ComparisonResults.Clear();
            foreach (var player in results)
            {
                ComparisonResults.Add(player);
            }

            SelectedPlayer = ComparisonResults.FirstOrDefault();

            var notDeposited = results.Sum(x => x.NotDepositedValue);
            var summary = string.Format(Ru,
                "Лута в журнале: {0:N0} · строк лога сундука: {1:N0} · не сдано на {2:N0} серебра",
                loot.Count, parse.Entries.Count, notDeposited);
            if (parse.UnknownItems > 0)
            {
                summary += string.Format(Ru, "\nНе распознано предметов в логе сундука: {0:N0} (например: {1})",
                    parse.UnknownItems, string.Join(", ", parse.UnknownItemNames.Take(3)));
            }

            if (parse.Entries.Count == 0 && !string.IsNullOrWhiteSpace(text))
            {
                summary += "\nВ тексте не найдено ни одной строки лога сундука. Скопируйте лог из окна сундука в игре целиком.";
            }

            ComparisonSummary = summary;
        }
        catch (Exception ex)
        {
            ComparisonSummary = $"Ошибка сверки: {ex.Message}";
        }
        finally
        {
            IsComparing = false;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    private void ExportComparison()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Отчёт сверки",
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"chest_check_{DateTime.Now:yyyy-MM-dd_HH-mm}.csv",
            InitialDirectory = EnsureDirectory(LogDirectory)
        };

        if (dialog.ShowDialog(Application.Current.MainWindow) != true)
        {
            return;
        }

        try
        {
            using var writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true));
            writer.WriteLine("Игрок;Гильдия;Предмет;Количество;Стоимость;С кого;Время лута;Время сдачи;Статус");
            foreach (var player in ComparisonResults)
            {
                foreach (var item in player.Items.OrderBy(x => x.Status).ThenBy(x => x.LootUtc ?? x.DepositUtc))
                {
                    writer.WriteLine(string.Join(';',
                        Csv(player.Player), Csv(player.Guild), Csv(item.ItemName),
                        item.Quantity.ToString(CultureInfo.InvariantCulture),
                        item.Value.ToString(CultureInfo.InvariantCulture),
                        Csv(item.LootedFrom),
                        item.LootUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty,
                        item.DepositUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty,
                        Csv(item.StatusText)));
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(Application.Current.MainWindow, ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        static string Csv(string value) => value.Contains(';') || value.Contains('"') ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    #endregion

    #region Команды

    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand ImportLogCommand { get; }
    public RelayCommand NewSessionCommand { get; }
    public RelayCommand OpenLogFolderCommand { get; }
    public RelayCommand RefreshAdaptersCommand { get; }
    public RelayCommand RestartCaptureCommand { get; }
    public RelayCommand BrowseGameFolderCommand { get; }
    public RelayCommand PasteChestLogCommand { get; }
    public RelayCommand LoadChestLogCommand { get; }
    public RelayCommand ClearChestLogCommand { get; }
    public RelayCommand CompareCommand { get; }
    public RelayCommand ExportComparisonCommand { get; }
    public RelayCommand OpenNpcapSiteCommand { get; }
    public RelayCommand BrowseLogFolderCommand { get; }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // ignored
        }
    }

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch
        {
            // ignored
        }
    }

    #endregion
}
