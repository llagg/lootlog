using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LootLogger;

/// <summary>
/// Иконки предметов с официального сервера render.albiononline.com.
/// Каждая картинка скачивается один раз и дальше берётся с диска.
/// Иконки, которые нужны на экране, качаются вне очереди; остальные — в фоне.
/// </summary>
public sealed class IconCache
{
    private const int IconSize = 64;
    private const int Workers = 3;
    private const int ConsecutiveErrorsToPause = 25;
    private static readonly TimeSpan RetryMissingAfter = TimeSpan.FromDays(7);

    public static IconCache Instance { get; } = new();

    private readonly HttpClient _http;
    private readonly string _directory = AppPaths.IconsDirectory;
    private readonly string _missingFile;

    // доступ только из потока интерфейса
    private readonly Dictionary<string, ImageSource> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Action<ImageSource>>> _waiting = new(StringComparer.OrdinalIgnoreCase);

    // очереди загрузки (общие для потоков)
    private readonly Lock _queueLock = new();
    private readonly LinkedList<string> _priority = new();
    private readonly LinkedList<string> _background = new();
    private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Dictionary<string, DateTime> _missing = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _onDisk = new(StringComparer.OrdinalIgnoreCase);
    private int _consecutiveErrors;
    private bool _started;

    private IconCache()
    {
        _missingFile = Path.Combine(_directory, "_missing.txt");
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = Workers,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AlbionLootLogger/1.0");
    }

    public event Action? ProgressChanged;

    public int TotalItems { get; private set; }
    public int OnDiskCount { get; private set; }
    public bool IsPaused { get; private set; }
    public bool IsDownloading { get; private set; }

    /// <summary>Запускает фоновую загрузку недостающих иконок для всех предметов игры.</summary>
    public void StartBackgroundDownload(IEnumerable<string> uniqueNames)
    {
        var names = uniqueNames.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(_directory);
                LoadMissingList();
                var onDisk = new HashSet<string>(
                    Directory.EnumerateFiles(_directory, "*.png").Select(Path.GetFileNameWithoutExtension).OfType<string>(),
                    StringComparer.OrdinalIgnoreCase);

                lock (_queueLock)
                {
                    _onDisk = onDisk;
                    TotalItems = names.Count;
                    OnDiskCount = names.Count(onDisk.Contains);
                    IsPaused = false;
                    _consecutiveErrors = 0;

                    foreach (var name in names)
                    {
                        if (!onDisk.Contains(name) && !IsKnownMissing(name) && _queued.Add(name))
                        {
                            _background.AddLast(name);
                            _signal.Release();
                        }
                    }
                }

                EnsureWorkers();
                RaiseProgress();
            }
            catch
            {
                // нет доступа к папке — останемся без иконок
            }
        });
    }

    /// <summary>Получить иконку. Вызывать из потока интерфейса; onReady вызывается тоже в нём.</summary>
    public void Request(string uniqueName, Action<ImageSource> onReady)
    {
        if (string.IsNullOrWhiteSpace(uniqueName))
        {
            return;
        }

        if (_memory.TryGetValue(uniqueName, out var cached))
        {
            onReady(cached);
            return;
        }

        if (_waiting.TryGetValue(uniqueName, out var waiters))
        {
            waiters.Add(onReady);
            return;
        }

        _waiting[uniqueName] = [onReady];
        var path = FilePath(uniqueName);

        _ = Task.Run(() =>
        {
            if (File.Exists(path))
            {
                var image = Load(path);
                if (image != null)
                {
                    Deliver(uniqueName, image);
                    return;
                }
            }

            // на диске нет — качаем вне очереди
            lock (_queueLock)
            {
                if (IsKnownMissing(uniqueName))
                {
                    return;
                }

                _background.Remove(uniqueName);
                _queued.Add(uniqueName);
                _priority.AddFirst(uniqueName);
                _signal.Release();
            }

            EnsureWorkers();
        });
    }

    private void EnsureWorkers()
    {
        lock (_queueLock)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        for (var i = 0; i < Workers; i++)
        {
            _ = Task.Run(WorkerAsync);
        }
    }

    private async Task WorkerAsync()
    {
        while (true)
        {
            await _signal.WaitAsync().ConfigureAwait(false);

            string? name = null;
            var fromBackground = false;
            lock (_queueLock)
            {
                if (_priority.First != null)
                {
                    name = _priority.First.Value;
                    _priority.RemoveFirst();
                }
                else if (_background.First != null && !IsPaused)
                {
                    name = _background.First.Value;
                    _background.RemoveFirst();
                    fromBackground = true;
                }
            }

            if (name == null)
            {
                continue;
            }

            IsDownloading = true;
            await DownloadAsync(name, fromBackground).ConfigureAwait(false);

            lock (_queueLock)
            {
                _queued.Remove(name);
                IsDownloading = _priority.Count > 0 || (_background.Count > 0 && !IsPaused);
            }

            RaiseProgress();
        }
    }

    private async Task DownloadAsync(string name, bool fromBackground)
    {
        var path = FilePath(name);
        try
        {
            if (File.Exists(path))
            {
                MarkOnDisk(name);
                DeliverIfWaiting(name, path);
                return;
            }

            var url = $"https://render.albiononline.com/v1/item/{name}.png?size={IconSize}";
            using var response = await _http.GetAsync(url).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                RememberMissing(name);
                _consecutiveErrors = 0;
                return;
            }

            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes.Length < 8 || bytes[0] != 0x89 || bytes[1] != 0x50)
            {
                // не PNG — считаем, что картинки нет
                RememberMissing(name);
                return;
            }

            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
            File.Move(temp, path, true);
            _consecutiveErrors = 0;
            MarkOnDisk(name);
            DeliverIfWaiting(name, path);
        }
        catch
        {
            // сеть недоступна или сервер не отвечает: при многих ошибках подряд фоновую загрузку ставим на паузу
            if (fromBackground && Interlocked.Increment(ref _consecutiveErrors) >= ConsecutiveErrorsToPause)
            {
                lock (_queueLock)
                {
                    IsPaused = true;
                }
            }
        }
    }

    private void MarkOnDisk(string name)
    {
        lock (_queueLock)
        {
            if (_onDisk.Add(name))
            {
                OnDiskCount++;
            }
        }
    }

    private void DeliverIfWaiting(string name, string path)
    {
        var image = Load(path);
        if (image != null)
        {
            Deliver(name, image);
        }
    }

    private void Deliver(string name, ImageSource image)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            _memory[name] = image;
            if (_waiting.Remove(name, out var waiters))
            {
                foreach (var waiter in waiters)
                {
                    waiter(image);
                }
            }
        });
    }

    private static ImageSource? Load(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 48;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private string FilePath(string uniqueName)
    {
        return Path.Combine(_directory, uniqueName + ".png");
    }

    private bool IsKnownMissing(string name)
    {
        return _missing.TryGetValue(name, out var when) && DateTime.UtcNow - when < RetryMissingAfter;
    }

    private void RememberMissing(string name)
    {
        lock (_queueLock)
        {
            _missing[name] = DateTime.UtcNow;
            try
            {
                File.AppendAllText(_missingFile, $"{name}\t{DateTime.UtcNow.Ticks}\n");
            }
            catch
            {
                // не критично
            }
        }
    }

    private void LoadMissingList()
    {
        try
        {
            if (!File.Exists(_missingFile))
            {
                return;
            }

            lock (_queueLock)
            {
                foreach (var line in File.ReadAllLines(_missingFile))
                {
                    var parts = line.Split('\t');
                    if (parts.Length == 2 && long.TryParse(parts[1], out var ticks))
                    {
                        _missing[parts[0]] = new DateTime(ticks, DateTimeKind.Utc);
                    }
                }
            }
        }
        catch
        {
            // ignored
        }
    }

    private void RaiseProgress()
    {
        var handler = ProgressChanged;
        if (handler != null)
        {
            Application.Current?.Dispatcher.InvokeAsync(handler);
        }
    }
}
