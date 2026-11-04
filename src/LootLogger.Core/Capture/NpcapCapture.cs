using System.Net.NetworkInformation;
using Libpcap;

namespace LootLogger.Core.Capture;

public sealed record CaptureAdapter(string Id, string DisplayName);

public sealed record AdapterDiagnostics(string Name, long UdpPackets, long PhotonPackets, long GamePackets);

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

    /// <summary>Обычный режим: в драйвере пропускаются только порты игры.</summary>
    private const string PortsFilter = "(udp and (port 5055 or port 5056 or port 5058)) or (ip proto 17 and (ip[6:2] & 0x3fff != 0))";

    /// <summary>Режим поиска: весь UDP, игру узнаём по содержимому пакетов (ускорители, VPN с прокси).</summary>
    private const string DiscoveryFilter = "udp";

    private const int KernelBufferSize = 16 * 1024 * 1024;
    private const int MaxFlows = 4096;
    private const int PhotonPacketsToConfirmFlow = 3;
    private const int NonPhotonPacketsToIgnoreFlow = 30;
    private static readonly TimeSpan DiscoveryDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan EvaluationInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FlowIdleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Вызывается для каждой полезной нагрузки игрового UDP-пакета. incoming = от сервера к клиенту.</summary>
    private readonly PayloadHandler _onPayload;

    private readonly Lock _lifecycleLock = new();
    private Thread? _thread;
    private volatile bool _stopRequested;

    private readonly Dictionary<Pcap, AdapterStats> _stats = new();
    private readonly Dictionary<FlowKey, Flow> _flows = new();
    private readonly Ipv4Reassembler _reassembler = new();
    private PcapDispatcher? _dispatcher;
    private DateTime _startedUtc;
    private DateTime _nextEvaluationUtc;
    private DateTime _lastCustomPortGameUtc = DateTime.MinValue;

    public delegate void PayloadHandler(ReadOnlySpan<byte> payload, bool incoming);

    public NpcapCapture(PayloadHandler onPayload)
    {
        _onPayload = onPayload;
    }

    /// <summary>Идёт поиск игры на любых портах (стандартные порты молчат).</summary>
    public bool IsDiscoveryMode { get; private set; }

    /// <summary>Игра найдена на нестандартном порту (через ускоритель или прокси).</summary>
    public bool IsCustomPortDetected => DateTime.UtcNow - _lastCustomPortGameUtc < FlowIdleTimeout;

    public long UdpPackets;

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
            _dispatcher = dispatcher;
            _startedUtc = DateTime.UtcNow;
            IsDiscoveryMode = false;

            // Фильтр задаём до открытия: тогда он ставится на каждый адаптер отдельно,
            // и адаптер, который фильтр не принял, просто пропускается, а не роняет весь захват.
            dispatcher.Filter = PortsFilter;
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

                Evaluate();

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
            _dispatcher = null;
            OpenedAdapters = 0;
            ActiveAdapterName = string.Empty;
            lock (_stats)
            {
                _stats.Clear();
            }

            _flows.Clear();
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
                || string.Equals(device.Name, adapterId, StringComparison.OrdinalIgnoreCase);
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

        UdpPackets++;
        var stats = GetStats(pcap);
        stats.UdpPackets++;

        var now = DateTime.UtcNow;
        var key = FlowKey.Create(sourcePort, destinationPort);
        if (!_flows.TryGetValue(key, out var flow))
        {
            if (_flows.Count >= MaxFlows)
            {
                _flows.Clear();
            }

            flow = new Flow();
            _flows[key] = flow;
        }

        flow.LastSeenUtc = now;

        bool incoming;
        if (GamePorts.Contains(sourcePort) || GamePorts.Contains(destinationPort))
        {
            incoming = GamePorts.Contains(sourcePort);
        }
        else
        {
            if (!IsDiscoveryMode || !TryClassifyCustomFlow(flow, payload, sourcePort, destinationPort, stats))
            {
                return;
            }

            incoming = sourcePort == flow.ServerPort;
            _lastCustomPortGameUtc = now;
        }

        // Один и тот же поток может быть виден на двух адаптерах (виртуальные коммутаторы Hyper-V/WSL).
        // Закрепляем поток за адаптером, где его увидели первым, чтобы не задвоить лут.
        if (flow.Owner is null || now - flow.OwnerLastSeenUtc > FlowIdleTimeout)
        {
            flow.Owner = pcap;
        }

        if (!ReferenceEquals(flow.Owner, pcap))
        {
            return;
        }

        flow.OwnerLastSeenUtc = now;
        stats.GamePackets++;
        GamePackets++;
        LastGamePacketUtc = now;
        ActiveAdapterName = AdapterName(pcap);
        _onPayload(payload, incoming);
    }

    /// <summary>Решает, игровой ли это поток на нестандартных портах, по содержимому первых пакетов.</summary>
    private static bool TryClassifyCustomFlow(Flow flow, ReadOnlySpan<byte> payload, ushort sourcePort, ushort destinationPort, AdapterStats stats)
    {
        if (flow.IsIgnored)
        {
            return false;
        }

        if (flow.IsGame)
        {
            return true;
        }

        var probe = PhotonProbe.Inspect(payload);
        if (probe == PhotonProbeResult.NotPhoton)
        {
            flow.NonPhotonPackets++;
            if (flow.NonPhotonPackets >= NonPhotonPacketsToIgnoreFlow && flow.PhotonPackets == 0)
            {
                flow.IsIgnored = true;
            }

            return false;
        }

        flow.PhotonPackets++;
        stats.PhotonPackets++;
        if (probe == PhotonProbeResult.FromServer)
        {
            flow.ServerPort = sourcePort;
        }
        else if (probe == PhotonProbeResult.FromClient)
        {
            flow.ServerPort = destinationPort;
        }

        return flow.IsGame;
    }

    /// <summary>
    /// Раз в несколько секунд: включаем поиск по содержимому, если на стандартных портах тишина,
    /// и возвращаемся к экономному фильтру, если игра снова идёт по стандартным портам.
    /// </summary>
    private void Evaluate()
    {
        var now = DateTime.UtcNow;
        if (now < _nextEvaluationUtc)
        {
            return;
        }

        _nextEvaluationUtc = now + EvaluationInterval;

        foreach (var expired in _flows.Where(x => now - x.Value.LastSeenUtc > FlowIdleTimeout).Select(x => x.Key).ToList())
        {
            _flows.Remove(expired);
        }

        var silent = now - LastGamePacketUtc > DiscoveryDelay && now - _startedUtc > DiscoveryDelay;
        if (!IsDiscoveryMode && silent)
        {
            SetFilter(DiscoveryFilter, discovery: true);
        }
        else if (IsDiscoveryMode && !IsCustomPortDetected && now - LastGamePacketUtc < TimeSpan.FromSeconds(5)
                 && now - _lastCustomPortGameUtc > TimeSpan.FromMinutes(2))
        {
            // игра идёт по стандартным портам — фильтр в драйвере снова отсекает лишнее
            SetFilter(PortsFilter, discovery: false);
        }
    }

    private void SetFilter(string filter, bool discovery)
    {
        try
        {
            if (_dispatcher != null)
            {
                _dispatcher.Filter = filter;
                IsDiscoveryMode = discovery;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private AdapterStats GetStats(Pcap pcap)
    {
        if (!_stats.TryGetValue(pcap, out var stats))
        {
            stats = new AdapterStats { Name = AdapterName(pcap) };
            lock (_stats)
            {
                _stats[pcap] = stats;
            }
        }

        return stats;
    }

    private static string AdapterName(Pcap pcap)
    {
        return pcap is DevicePcap device ? device.Device.Description ?? device.Device.Name : pcap.Name;
    }

    /// <summary>Сводка по адаптерам для диагностики: сколько UDP, сколько похожего на игру, сколько принято.</summary>
    public IReadOnlyList<AdapterDiagnostics> GetDiagnostics()
    {
        lock (_stats)
        {
            return _stats.Values
                .Select(x => new AdapterDiagnostics(x.Name, x.UdpPackets, x.PhotonPackets, x.GamePackets))
                .OrderByDescending(x => x.GamePackets)
                .ThenByDescending(x => x.UdpPackets)
                .ToList();
        }
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
        public string Name = string.Empty;
        public long UdpPackets;
        public long PhotonPackets;
        public long GamePackets;
    }

    private readonly record struct FlowKey(ushort Low, ushort High)
    {
        public static FlowKey Create(ushort a, ushort b) => a <= b ? new FlowKey(a, b) : new FlowKey(b, a);
    }

    private sealed class Flow
    {
        public DateTime LastSeenUtc;
        public Pcap? Owner;
        public DateTime OwnerLastSeenUtc;
        public int PhotonPackets;
        public int NonPhotonPackets;
        public bool IsIgnored;
        public ushort? ServerPort;
        public bool IsGame => ServerPort != null && PhotonPackets >= PhotonPacketsToConfirmFlow;
    }
}
