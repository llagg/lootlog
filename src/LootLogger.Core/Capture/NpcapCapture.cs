using System.Net.NetworkInformation;
using Libpcap;

namespace LootLogger.Core.Capture;

public sealed record CaptureAdapter(string Id, string DisplayName);

public enum CaptureProblem
{
    None,
    NoAdapters,
    AdapterNotFound
}

/// <summary>
/// Захват игрового трафика через Npcap. Работает в отдельном потоке и в ядре отбрасывает всё,
/// кроме UDP-пакетов игры (BPF-фильтр), поэтому загрузки, стримы и прочий трафик не мешают.
/// </summary>
public sealed class NpcapCapture : IDisposable
{
    public const string AutoAdapterId = "";

    /// <summary>Порты игровых серверов Albion (Photon).</summary>
    public static readonly HashSet<ushort> GamePorts = [5055, 5056, 5058];

    private const string Filter = "(udp and (port 5055 or port 5056 or port 5058)) or (ip proto 17 and (ip[6:2] & 0x3fff != 0))";
    private const int KernelBufferSize = 16 * 1024 * 1024;
    private static readonly TimeSpan AdapterEvaluationInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AdapterIdleTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Вызывается для каждой полезной нагрузки игрового UDP-пакета. incoming = от сервера к клиенту.</summary>
    private readonly PayloadHandler _onPayload;

    private readonly Lock _lifecycleLock = new();
    private Thread? _thread;
    private volatile bool _stopRequested;

    private readonly Dictionary<Pcap, AdapterStats> _stats = new();
    private Pcap? _activePcap;
    private DateTime _nextEvaluationUtc;
    private readonly Ipv4Reassembler _reassembler = new();

    public delegate void PayloadHandler(ReadOnlySpan<byte> payload, bool incoming);

    public NpcapCapture(PayloadHandler onPayload)
    {
        _onPayload = onPayload;
    }

    public bool IsRunning => _thread?.IsAlive == true;
    public string ActiveAdapterName { get; private set; } = string.Empty;
    public int OpenedAdapters { get; private set; }
    public string LastError { get; private set; } = string.Empty;
    public CaptureProblem Problem { get; private set; }
    public long GamePackets;
    public long HandlerErrors;
    public DateTime LastGamePacketUtc { get; private set; } = DateTime.MinValue;

    /// <summary>Есть ли в системе Npcap (wpcap.dll).</summary>
    public static bool IsNpcapAvailable(out string error)
    {
        try
        {
            _ = Pcap.ListDevices();
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = Unwrap(ex).Message;
            return false;
        }
    }

    public static IReadOnlyList<CaptureAdapter> ListAdapters()
    {
        var result = new List<CaptureAdapter>();
        var friendlyNames = GetFriendlyNames();

        var devices = Pcap.ListDevices();
        foreach (var device in devices)
        {
            if (!device.Flags.HasFlag(PcapDeviceFlags.Up))
            {
                continue;
            }

            var friendly = friendlyNames.FirstOrDefault(x => device.Name.EndsWith(x.Id, StringComparison.OrdinalIgnoreCase)).Name;
            var description = device.Description ?? device.Name;
            var display = string.IsNullOrWhiteSpace(friendly) ? description : $"{friendly} — {description}";
            if (device.Flags.HasFlag(PcapDeviceFlags.Loopback))
            {
                display += " (loopback)";
            }

            result.Add(new CaptureAdapter(device.Name, display));
        }

        return result;
    }

    public void Start(string adapterId)
    {
        lock (_lifecycleLock)
        {
            if (IsRunning)
            {
                return;
            }

            _stopRequested = false;
            LastError = string.Empty;
            Problem = CaptureProblem.None;
            var ready = new ManualResetEventSlim();
            _thread = new Thread(() => Worker(adapterId, ready))
            {
                IsBackground = true,
                Name = "Npcap capture",
                Priority = ThreadPriority.AboveNormal
            };
            _thread.Start();

            // дождаться открытия адаптеров, чтобы сразу показать ошибку, если что-то не так
            ready.Wait(TimeSpan.FromSeconds(10));
        }
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            _stopRequested = true;
            _thread?.Join(TimeSpan.FromSeconds(3));
            _thread = null;
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private void Worker(string adapterId, ManualResetEventSlim ready)
    {
        PcapDispatcher? dispatcher = null;
        try
        {
            dispatcher = new PcapDispatcher(OnPacket, rotateAfter: 64);

            // Фильтр задаём до открытия: тогда он ставится на каждый адаптер отдельно,
            // и адаптер, который фильтр не принял, просто пропускается, а не роняет весь захват.
            dispatcher.Filter = Filter;
            OpenedAdapters = OpenAdapters(dispatcher, adapterId);
            if (OpenedAdapters == 0)
            {
                Problem = string.IsNullOrEmpty(adapterId) ? CaptureProblem.NoAdapters : CaptureProblem.AdapterNotFound;
                return;
            }

            ready.Set();

            while (!_stopRequested)
            {
                int read;
                try
                {
                    read = dispatcher.Dispatch(512);
                }
                catch (PcapException ex)
                {
                    LastError = ex.Message;
                    Thread.Sleep(250);
                    continue;
                }

                EvaluateAdapters();

                if (read == 0)
                {
                    // Пакетов нет — коротко спим. 5 мс достаточно, чтобы не грузить процессор.
                    Thread.Sleep(5);
                }
            }
        }
        catch (Exception ex)
        {
            LastError = Unwrap(ex).Message;
        }
        finally
        {
            dispatcher?.Dispose();
            OpenedAdapters = 0;
            ActiveAdapterName = string.Empty;
            _stats.Clear();
            _activePcap = null;
            ready.Set();
        }
    }

    private int OpenAdapters(PcapDispatcher dispatcher, string adapterId)
    {
        var opened = 0;
        var devices = Pcap.ListDevices();
        foreach (var device in devices)
        {
            var isSelected = string.IsNullOrEmpty(adapterId)
                ? device.Flags.HasFlag(PcapDeviceFlags.Up)
                : string.Equals(device.Name, adapterId, StringComparison.OrdinalIgnoreCase);
            if (!isSelected)
            {
                continue;
            }

            try
            {
                dispatcher.OpenDevice(device, pcap =>
                {
                    pcap.BufferSize = KernelBufferSize;
                    pcap.SnapshotLength = 65535;
                    pcap.ImmediateMode = true;
                });
                opened++;
            }
            catch
            {
                // адаптер не открылся (выключен, без прав) — пропускаем
            }
        }

        return opened;
    }

    private void OnPacket(Pcap pcap, ref Packet packet)
    {
        try
        {
            if (!FrameParser.TryGetIpPacket((int) pcap.DataLink, packet.Data, out var ip))
            {
                return;
            }

            if (FrameParser.TryReadIpv4(ip, out var ipv4))
            {
                if (ipv4.Protocol != 17)
                {
                    return;
                }

                if (ipv4.IsFragment)
                {
                    var assembled = _reassembler.Add(ip, ipv4);
                    if (assembled != null)
                    {
                        HandleUdp(pcap, assembled);
                    }

                    return;
                }

                HandleUdp(pcap, ip.Slice(ipv4.HeaderLength, ipv4.TotalLength - ipv4.HeaderLength));
                return;
            }

            if (FrameParser.TryGetIpv6Udp(ip, out var udp6))
            {
                HandleUdp(pcap, udp6);
            }
        }
        catch
        {
            HandlerErrors++;
        }
    }

    private void HandleUdp(Pcap pcap, ReadOnlySpan<byte> udp)
    {
        if (!FrameParser.TryReadUdp(udp, out var sourcePort, out var destinationPort, out var payload) || payload.Length < 12)
        {
            return;
        }

        var incoming = GamePorts.Contains(sourcePort);
        if (!incoming && !GamePorts.Contains(destinationPort))
        {
            return;
        }

        if (!AcceptAdapter(pcap, incoming))
        {
            return;
        }

        GamePackets++;
        LastGamePacketUtc = DateTime.UtcNow;
        _onPayload(payload, incoming);
    }

    /// <summary>
    /// В режиме «Авто» открыты все адаптеры, но обрабатываем пакеты только одного — того, на котором
    /// идёт трафик от сервера игры. Иначе при VPN/ExitLag одни и те же пакеты пришли бы дважды.
    /// Если выбранный адаптер замолчал, а на другом идёт игра, — переключаемся.
    /// </summary>
    private bool AcceptAdapter(Pcap pcap, bool incoming)
    {
        if (!_stats.TryGetValue(pcap, out var stats))
        {
            stats = new AdapterStats();
            _stats[pcap] = stats;
        }

        if (incoming)
        {
            stats.IncomingInWindow++;
            stats.LastIncomingUtc = DateTime.UtcNow;
        }

        if (_activePcap is null)
        {
            if (!incoming)
            {
                return false;
            }

            SetActive(pcap);
        }

        return ReferenceEquals(_activePcap, pcap);
    }

    private void EvaluateAdapters()
    {
        var now = DateTime.UtcNow;
        if (now < _nextEvaluationUtc)
        {
            return;
        }

        _nextEvaluationUtc = now + AdapterEvaluationInterval;

        if (_activePcap is not null && _stats.TryGetValue(_activePcap, out var active))
        {
            var best = _stats.Where(x => !ReferenceEquals(x.Key, _activePcap)).MaxBy(x => x.Value.IncomingInWindow);
            if (active.IncomingInWindow == 0 && best.Value is { IncomingInWindow: > 0 })
            {
                SetActive(best.Key);
            }
            else if (now - active.LastIncomingUtc > AdapterIdleTimeout)
            {
                _activePcap = null;
                ActiveAdapterName = string.Empty;
            }
        }

        foreach (var stats in _stats.Values)
        {
            stats.IncomingInWindow = 0;
        }
    }

    private void SetActive(Pcap pcap)
    {
        _activePcap = pcap;
        ActiveAdapterName = pcap is DevicePcap device ? device.Device.Description ?? device.Device.Name : pcap.Name;
    }

    private static List<(string Id, string Name)> GetFriendlyNames()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Select(x => (x.Id, x.Name)).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static Exception Unwrap(Exception ex)
    {
        while (ex is TypeInitializationException && ex.InnerException != null)
        {
            ex = ex.InnerException;
        }

        return ex;
    }

    private sealed class AdapterStats
    {
        public int IncomingInWindow;
        public DateTime LastIncomingUtc;
    }
}
