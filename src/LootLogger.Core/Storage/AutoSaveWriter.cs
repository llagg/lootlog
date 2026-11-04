using System.Collections.Concurrent;
using System.Text;
using LootLogger.Core.Loot;

namespace LootLogger.Core.Storage;

/// <summary>
/// Пишет журнал на диск по мере поступления записей. Работает в своём потоке:
/// захват пакетов и интерфейс только кладут строку в очередь и ничего не ждут.
/// Файл создаётся при первой записи, так что пустых файлов не остаётся.
/// </summary>
public sealed class AutoSaveWriter : IDisposable
{
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>());
    private readonly string _directory;
    private readonly Thread _thread;
    private string? _pendingNewFile;
    private volatile string _currentFile = string.Empty;

    public AutoSaveWriter(string directory)
    {
        _directory = directory;
        StartNewFile();
        _thread = new Thread(Run) { IsBackground = true, Name = "Loot autosave" };
        _thread.Start();
    }

    /// <summary>Путь к текущему файлу (пусто, пока не было ни одной записи).</summary>
    public string CurrentFile => _currentFile;

    public string Directory => _directory;

    public string LastError { get; private set; } = string.Empty;

    public void Add(LootEntry entry)
    {
        TryAdd(LootCsv.Format(entry));
    }

    public void Add(KillEntry entry)
    {
        TryAdd(LootCsv.Format(entry));
    }

    /// <summary>Следующие записи пойдут в новый файл.</summary>
    public void StartNewFile()
    {
        _pendingNewFile = Path.Combine(_directory, $"loot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv");
        _currentFile = string.Empty;
        TryAdd(NewFileMarker);
    }

    private const string NewFileMarker = "\u0000new-file";

    private void TryAdd(string line)
    {
        if (!_queue.IsAddingCompleted)
        {
            try
            {
                _queue.Add(line);
            }
            catch (InvalidOperationException)
            {
                // writer уже закрыт
            }
        }
    }

    private void Run()
    {
        StreamWriter? writer = null;
        string? targetFile = null;

        try
        {
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                if (line == NewFileMarker)
                {
                    writer?.Dispose();
                    writer = null;
                    targetFile = _pendingNewFile;
                    continue;
                }

                try
                {
                    if (writer == null && targetFile != null)
                    {
                        System.IO.Directory.CreateDirectory(_directory);
                        var exists = File.Exists(targetFile);
                        writer = new StreamWriter(new FileStream(targetFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(!exists));
                        if (!exists)
                        {
                            writer.WriteLine(LootCsv.Header);
                        }

                        _currentFile = targetFile;
                    }

                    writer?.WriteLine(line);

                    // пишем на диск, как только очередь опустела — при вылете программы ничего не потеряется
                    if (_queue.Count == 0)
                    {
                        writer?.Flush();
                    }
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    writer?.Dispose();
                    writer = null;
                }
            }
        }
        finally
        {
            writer?.Dispose();
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
    }
}
