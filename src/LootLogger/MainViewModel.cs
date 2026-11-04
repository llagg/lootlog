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

        Loc.Instance.LanguageChanged += OnLanguageChanged;
        _journalSummary = Loc.T("NoEntries");

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

    #region Язык

    public IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new LanguageOption(false, "Русский"),
        new LanguageOption(true, "English")
    ];

    public LanguageOption SelectedLanguage
    {
        get => Languages.First(x => x.IsEnglish == Loc.Instance.IsEnglish);
        set
        {
            if (value == null || value.IsEnglish == Loc.Instance.IsEnglish)
            {
                return;
            }

            _settings.Language = value.IsEnglish ? "en" : "ru";
            Loc.Instance.SetLanguage(value.IsEnglish);
            OnPropertyChanged();
        }
    }

    /// <summary>После смены языка перерисовываем всё, что собирается из кода.</summary>
    private void OnLanguageChanged()
    {
        UpdateJournalSummary();
        UpdateStatus();
        RefreshAdapters();
        OnPropertyChanged(nameof(Warning));
        OnPropertyChanged(nameof(GameDataStatus));
        OnPropertyChanged(nameof(ComparisonSummary));
        OnPropertyChanged(nameof(SelectedPlayerTitle));
        OnPropertyChanged(nameof(SelectedLanguage));

        // строки таблиц пересоздаются — названия предметов и статусы берутся на новом языке
        EntriesView.Refresh();
        UpdateSelectedPlayerItems();
    }

    #endregion

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
    private string _journalSummary = string.Empty;
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
               || Contains(e.DisplayItemName, q) || Contains(SourceNameConverter.Translate(e.LootedFrom), q) || Contains(e.UniqueName, q) || Contains(e.Cluster, q);

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
            ? Loc.T("NoEntries")
            : Loc.F("JournalSummary", Entries.Count, _shownCount, _shownValue);
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

    private string _captureStatus = Loc.T("CaptureStarting");
    public string CaptureStatus { get => _captureStatus; private set => Set(ref _captureStatus, value); }

    private bool _isCaptureOk;
    public bool IsCaptureOk { get => _isCaptureOk; private set => Set(ref _isCaptureOk, value); }

    private bool _isGameDataSeen;
    public bool IsGameDataSeen { get => _isGameDataSeen; private set => Set(ref _isGameDataSeen, value); }

    private string _playerStatus = string.Empty;
    public string PlayerStatus { get => _playerStatus; private set => Set(ref _playerStatus, value); }

    private string _gameDataKey = string.Empty;
    private object[] _gameDataArgs = [];

    public string GameDataStatus => string.IsNullOrEmpty(_gameDataKey) ? string.Empty : Loc.F(_gameDataKey, _gameDataArgs);

    private void SetGameDataStatus(string key, params object[] args)
    {
        _gameDataKey = key;
        _gameDataArgs = args;
        OnPropertyChanged(nameof(GameDataStatus));
    }

    private string _warningKey = string.Empty;
    private string _warningArg = string.Empty;

    public string Warning => string.IsNullOrEmpty(_warningKey) ? string.Empty : Loc.F(_warningKey, _warningArg);

    public bool HasWarning => !string.IsNullOrEmpty(_warningKey);

    private void SetWarning(string key, string arg = "")
    {
        _warningKey = key;
        _warningArg = arg;
        OnPropertyChanged(nameof(Warning));
        OnPropertyChanged(nameof(HasWarning));
    }

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
            var problem = capture.Problem switch
            {
                CaptureProblem.NoAdapters => Loc.T("NoAdapters"),
                CaptureProblem.AdapterNotFound => Loc.T("AdapterNotFound"),
                _ => capture.LastError
            };
            CaptureStatus = string.IsNullOrEmpty(problem) ? Loc.T("CaptureStopped") : Loc.F("CaptureFailed", problem);
        }
        else if (IsGameDataSeen)
        {
            IsCaptureOk = true;
            CaptureStatus = Loc.F("DataFlowing", capture.ActiveAdapterName, capture.GamePackets);
        }
        else
        {
            IsCaptureOk = true;
            CaptureStatus = capture.GamePackets == 0
                ? Loc.F("WaitingTraffic", capture.OpenedAdapters)
                : Loc.T("NoTrafficNow");
        }

        PlayerStatus = string.IsNullOrEmpty(tracker.LocalPlayerName)
            ? Loc.T("PlayerUnknown")
            : Loc.F("PlayerLine",
                tracker.LocalPlayerName,
                string.IsNullOrEmpty(tracker.LocalGuild) ? string.Empty : $" [{tracker.LocalGuild}]",
                string.IsNullOrEmpty(tracker.CurrentCluster) ? "—" : tracker.CurrentCluster,
                tracker.PartySize);

        var writer = _writer;
        if (writer != null)
        {
            AutosaveStatus = !string.IsNullOrEmpty(writer.LastError)
                ? Loc.F("AutosaveError", writer.LastError)
                : Loc.F("AutosaveTo", string.IsNullOrEmpty(writer.CurrentFile) ? writer.Directory : writer.CurrentFile);
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
            SetWarning("NpcapMissing", npcapError);
        }

        // 2. Папка игры
        var gameDirectory = GameDataLoader.ResolveGameDirectory(_settings.GameDirectory)
                            ?? await Task.Run(GameDataLoader.DetectGameDirectory);
        if (gameDirectory == null)
        {
            MessageBox.Show(owner,
                Loc.T("AskGameFolder"), Loc.T("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
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

        CaptureStatus = Loc.T("OpeningAdapters");
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
            SetGameDataStatus("GameFolderMissing");
            if (!HasWarning)
            {
                SetWarning("GameFolderWarning");
            }

            return;
        }

        GameDirectory = gameDirectory;
        _settings.GameDirectory = gameDirectory;
        SetGameDataStatus("LoadingGameData");

        try
        {
            var progress = new Progress<string>(key => SetGameDataStatus(key));
            _game = await GameDataLoader.LoadAsync(gameDirectory, AppPaths.CacheDirectory, progress);
            _prices.Load(AppPaths.PricesFile, _game.Items);
            if (_engine != null)
            {
                _engine.Tracker.Game = _game;
            }

            SetGameDataStatus("GameDataLoaded", _game.Items.Count, _game.ClusterNames.Count, _prices.Count);
            if (_warningKey == "GameFolderWarning")
            {
                SetWarning(string.Empty);
            }
        }
        catch (Exception ex)
        {
            SetGameDataStatus("GameDataError", ex.Message);
        }
    }

    private static string? AskGameDirectory(Window? owner)
    {
        while (true)
        {
            var dialog = new OpenFolderDialog { Title = Loc.T("GameFolderDialog") };
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

            MessageBox.Show(owner, Loc.T("GameFolderInvalid"), Loc.T("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
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
        var selectedId = _selectedAdapter?.Id ?? _settings.AdapterId;
        Adapters.Clear();
        Adapters.Add(new CaptureAdapter(NpcapCapture.AutoAdapterId, Loc.T("AdapterAuto")));
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

        SelectedAdapter = Adapters.FirstOrDefault(x => x.Id == selectedId) ?? Adapters[0];
    }

    #endregion

    #region Файлы журнала

    public string LogDirectory => _settings.EffectiveLogDirectory;

    private void NewSession()
    {
        if (Entries.Count > 0 && MessageBox.Show(Application.Current.MainWindow,
                Loc.T("NewSessionConfirm"),
                Loc.T("NewSession"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
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
            Title = Loc.T("ExportTitle"),
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
            MessageBox.Show(Application.Current.MainWindow, Loc.F("Exported", visible.Count), Loc.T("ExportTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Application.Current.MainWindow, ex.Message, Loc.T("ExportError"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Добавить в журнал логи других лутлогеров (свои прошлые файлы или экспорт SAT).</summary>
    private void ImportLog()
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T("ImportTitle"),
            Filter = $"CSV (*.csv)|*.csv|{Loc.T("AllFiles")} (*.*)|*.*",
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
                    errors.Add(Loc.F("NotALog", Path.GetFileName(file)));
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

        var message = Loc.F("ImportResult", added, duplicates);
        if (errors.Count > 0)
        {
            message += "\n\n" + Loc.T("ImportFailed") + "\n" + string.Join('\n', errors);
        }

        MessageBox.Show(Application.Current.MainWindow, message, Loc.T("ImportTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
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
        var dialog = new OpenFolderDialog { Title = Loc.T("LogFolderDialog"), InitialDirectory = EnsureDirectory(LogDirectory) };
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

    private ComparisonStats? _lastComparison;
    private string _comparisonError = string.Empty;

    private sealed record ComparisonStats(int LootCount, int ChestLines, long NotDepositedValue, int UnknownItems, string UnknownExamples, bool NoChestLines);

    public string ComparisonSummary
    {
        get
        {
            if (!string.IsNullOrEmpty(_comparisonError))
            {
                return Loc.F("CompareError", _comparisonError);
            }

            if (_lastComparison is not { } c)
            {
                return Loc.T("CompareHint");
            }

            var summary = Loc.F("CompareSummary", c.LootCount, c.ChestLines, c.NotDepositedValue);
            if (c.UnknownItems > 0)
            {
                summary += "\n" + Loc.F("UnknownItems", c.UnknownItems, c.UnknownExamples);
            }

            if (c.NoChestLines)
            {
                summary += "\n" + Loc.T("NoChestLines");
            }

            return summary;
        }
    }

    public string SelectedPlayerTitle => _selectedPlayer == null ? string.Empty : Loc.F("PlayerItems", _selectedPlayer.Player);

    public ObservableCollection<PlayerComparison> ComparisonResults { get; } = new();

    private PlayerComparison? _selectedPlayer;
    public PlayerComparison? SelectedPlayer
    {
        get => _selectedPlayer;
        set
        {
            if (Set(ref _selectedPlayer, value))
            {
                OnPropertyChanged(nameof(SelectedPlayerTitle));
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
            MessageBox.Show(Application.Current.MainWindow, ex.Message, Loc.T("ClipboardTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LoadChestLog()
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T("ChestFilesTitle"),
            Filter = $"{Loc.T("TextFiles")} (*.txt;*.csv;*.tsv)|*.txt;*.csv;*.tsv|{Loc.T("AllFiles")} (*.*)|*.*",
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

            _comparisonError = string.Empty;
            _lastComparison = new ComparisonStats(
                loot.Count,
                parse.Entries.Count,
                results.Sum(x => x.NotDepositedValue),
                parse.UnknownItems,
                string.Join(", ", parse.UnknownItemNames.Take(3)),
                parse.Entries.Count == 0 && !string.IsNullOrWhiteSpace(text));
            OnPropertyChanged(nameof(ComparisonSummary));
        }
        catch (Exception ex)
        {
            _comparisonError = ex.Message;
            OnPropertyChanged(nameof(ComparisonSummary));
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
            Title = Loc.T("ReportTitle"),
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
            writer.WriteLine(Loc.T("ReportHeader"));
            foreach (var player in ComparisonResults)
            {
                foreach (var item in player.Items.OrderBy(x => x.Status).ThenBy(x => x.LootUtc ?? x.DepositUtc))
                {
                    writer.WriteLine(string.Join(';',
                        Csv(player.Player), Csv(player.Guild), Csv(item.ItemName),
                        item.Quantity.ToString(CultureInfo.InvariantCulture),
                        item.Value.ToString(CultureInfo.InvariantCulture),
                        Csv(SourceNameConverter.Translate(item.LootedFrom)),
                        item.LootUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty,
                        item.DepositUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty,
                        Csv(LootStatusConverter.Translate(item.Status))));
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(Application.Current.MainWindow, ex.Message, Loc.T("Error"), MessageBoxButton.OK, MessageBoxImage.Error);
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
