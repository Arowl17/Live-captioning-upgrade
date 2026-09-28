using System.Net;
using System.Net.Sockets;

namespace LiveCaptionsUpgrade.Core.Sharing;

/// <summary>
/// Caption sharing between two computers on the same network: this computer either sends the captions
/// Live Captions produces here, or shows the captions the paired computer sends. Keeps the connection to
/// the paired computer up, reconnecting as needed. Events are raised on background threads.
/// </summary>
public sealed class SharingController : IAsyncDisposable
{
    private const int MaxStatusLength = 300;

    private readonly PairingStore _pairing;
    private readonly Func<DeviceIdentity> _loadIdentity;
    private readonly string _appVersion;
    private readonly int _tcpPort;
    private readonly int _discoveryPort;
    private readonly CaptionFeed _feed = new();
    private readonly CaptionFeedReceiver _receiver = new();
    private readonly object _lock = new();

    // Held while turning received messages into caption events, so they are raised in the order accepted,
    // even for a moment when a new connection is replacing the old one.
    private readonly object _receiveLock = new();
    private readonly SemaphoreSlim _modeLock = new(1, 1);
    private DeviceIdentity? _identity;
    private SharingHost? _host;
    private DiscoveryService? _discovery;
    private Timer? _dialTimer;
    private CaptionSharingMode _mode;
    private string? _startError;
    private PeerConnection? _peer;
    private IDisposable? _subscription;
    private string? _peerStatus;
    private long _offlineSinceMs;
    private long _reachableSinceMs = -1;
    private long _pairingChangedMs = long.MinValue / 2;

    /// <param name="pairing">Where the paired computer is remembered.</param>
    /// <param name="loadIdentity">Loads (or creates) this computer's certificate; called when sharing first starts.</param>
    /// <param name="computerName">This computer's name, as the other computer shows it.</param>
    /// <param name="appVersion">This app's version, for the other computer's log.</param>
    public SharingController(
        PairingStore pairing,
        Func<DeviceIdentity> loadIdentity,
        string computerName,
        string appVersion,
        int tcpPort = FramedConnection.DefaultPort,
        int discoveryPort = DiscoveryService.DefaultPort)
    {
        _pairing = pairing;
        _loadIdentity = loadIdentity;
        ComputerName = DeviceNames.Sanitize(computerName);
        _appVersion = appVersion;
        _tcpPort = tcpPort;
        _discoveryPort = discoveryPort;
        _pairing.Changed += OnPairingChanged;
    }

    public string ComputerName { get; }

    /// <summary>How often a lost connection is retried.</summary>
    public TimeSpan DialInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Both computers try to connect; the one with the larger id waits this long so they rarely both succeed.</summary>
    public TimeSpan SecondaryDialDelay { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Right after pairing, the other computer may not have saved the pairing yet and would say it isn't paired;
    /// that isn't believed for this long.
    /// </summary>
    public TimeSpan NewPairingGrace { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>In show mode: new sentences from the other computer (already de-duplicated) and its live text.</summary>
    public event Action<IReadOnlyList<CaptionLine>, string>? CaptionsReceived;

    /// <summary>The connection, pairing or the other computer's status changed.</summary>
    public event Action? StateChanged;

    /// <summary>Another computer asks to pair: show the code and let the user confirm.</summary>
    public event Action<PairingSession>? PairingRequested;

    public CaptionSharingMode Mode
    {
        get
        {
            lock (_lock)
            {
                return _mode;
            }
        }
    }

    public PairedComputer? Paired => _pairing.Paired;

    /// <summary>Computers with caption sharing on, found on the network (empty while sharing is off).</summary>
    public IReadOnlyList<DiscoveredDevice> Nearby => _discovery?.Devices ?? Array.Empty<DiscoveredDevice>();

    /// <summary>The port this computer accepts connections on, or null while sharing is off.</summary>
    public int? ListenPort => _host?.TcpPort;

    public bool IsRunning => _host is not null;

    /// <summary>Passes an update from Live Captions on to the other computer (when sending).</summary>
    public void Publish(CaptionUpdate update) => _feed.Publish(update.NewSentences, update.Pending);

    /// <summary>Passes this computer's Live Captions problem message on to the other computer, or null when all is well.</summary>
    public void SetSenderStatus(string? status) => _feed.SetStatus(status);

    /// <summary>Asks computers on the network to announce themselves now.</summary>
    public void AnnounceNow() => _discovery?.AnnounceNow();

    /// <summary>Turns sharing on in the given mode, or off. Safe to call repeatedly.</summary>
    public async Task SetModeAsync(CaptionSharingMode mode)
    {
        await _modeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (mode == Mode && (mode == CaptionSharingMode.Off || _host is not null))
            {
                return;
            }

            await StopAsync().ConfigureAwait(false);
            lock (_lock)
            {
                _mode = mode;
                _startError = null;
                _peerStatus = null;
            }

            if (mode != CaptionSharingMode.Off)
            {
                Start(mode);
            }
        }
        finally
        {
            _modeLock.Release();
        }

        RaiseStateChanged();
    }

    /// <summary>Starts pairing with the computer at <paramref name="endPoint"/>; the session carries the code to compare.</summary>
    public Task<PairingSession> PairAsync(IPEndPoint endPoint)
    {
        var host = _host ?? throw new InvalidOperationException("Caption sharing is off.");
        return host.PairAsync(endPoint);
    }

    /// <summary>Forgets the paired computer, telling it to forget this one too if it's connected.</summary>
    public async Task ForgetAsync()
    {
        PeerConnection? peer;
        lock (_lock)
        {
            peer = _peer;
        }

        if (peer is not null)
        {
            peer.Post(new UnpairMessage());

            // Give the message a moment to go out before the connection is closed.
            await Task.Delay(300).ConfigureAwait(false);
        }

        _pairing.Forget();
    }

    /// <summary>One line describing the state, for the menu and the settings window.</summary>
    public string Describe()
    {
        lock (_lock)
        {
            var paired = _pairing.Paired;
            if (_mode == CaptionSharingMode.Off)
            {
                return "Caption sharing is off";
            }

            if (_startError is not null)
            {
                return "Caption sharing isn't working: " + _startError;
            }

            if (paired is null)
            {
                return "Not paired with another computer yet";
            }

            if (_peer is null)
            {
                return $"Waiting for {paired.Name}…";
            }

            return (_mode, _peer.PeerMode) switch
            {
                (CaptionSharingMode.Send, CaptionSharingMode.Receive) => $"Sending captions to {_peer.PeerName}",
                (CaptionSharingMode.Receive, CaptionSharingMode.Send) => $"Showing captions from {_peer.PeerName}",
                (CaptionSharingMode.Send, _) => $"Connected to {_peer.PeerName}, but it isn't set to show captions",
                _ => $"Connected to {_peer.PeerName}, but it isn't set to send captions",
            };
        }
    }

    /// <summary>In show mode, the message for the caption bar when captions can't arrive (null when they can).</summary>
    public string? DescribeProblemForReceiver()
    {
        lock (_lock)
        {
            var paired = _pairing.Paired;
            if (_mode != CaptionSharingMode.Receive)
            {
                return null;
            }

            if (_startError is not null)
            {
                return "Caption sharing isn't working: " + _startError;
            }

            if (paired is null)
            {
                return "Not paired with another computer yet. Right-click here > Caption sharing > Pair with a computer…";
            }

            if (_peer is null)
            {
                return $"Waiting for {paired.Name}… Make sure Live Captions Upgrade is running there, set to send captions.";
            }

            if (_peer.PeerMode != CaptionSharingMode.Send)
            {
                return $"{_peer.PeerName} is connected but isn't set to send captions. On {_peer.PeerName}: right-click the captions > "
                    + "Caption sharing > Send captions to another computer.";
            }

            return _peerStatus is null ? null : $"{_peer.PeerName}: {_peerStatus}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        _pairing.Changed -= OnPairingChanged;
        await SetModeAsync(CaptionSharingMode.Off).ConfigureAwait(false);
        _identity?.Dispose();
    }

    private void Start(CaptionSharingMode mode)
    {
        SharingHost host;
        try
        {
            _identity ??= _loadIdentity();
            host = new SharingHost(_identity, () => ComputerName, _pairing, mode, _appVersion, _tcpPort);
        }
        catch (Exception e)
        {
            Log.Error("Caption sharing couldn't start", e);
            lock (_lock)
            {
                _startError = e.Message;
            }

            return;
        }

        host.PeerConnected += OnPeerConnected;
        host.PeerDisconnected += OnPeerDisconnected;
        host.PairingRequested += OnPairingRequested;
        host.PeerNoLongerPaired += OnPeerNoLongerPaired;
        host.Start();
        _host = host;

        try
        {
            var discovery = new DiscoveryService(host.OwnId, () => ComputerName, mode, host.TcpPort, _discoveryPort);
            discovery.Start();
            _discovery = discovery;
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            // Another program has the discovery port: pairing and connecting by IP address still work.
            Log.Error("Network discovery unavailable", e);
        }

        _offlineSinceMs = PeerConnection.NowMs;
        _reachableSinceMs = -1;
        _dialTimer = new Timer(_ => DialPairedComputer(), null, TimeSpan.FromMilliseconds(300), DialInterval);
        Log.Info($"Caption sharing on ({mode}) as \"{ComputerName}\" (id {DeviceIdentity.ShortFingerprint(host.OwnId)}, port {host.TcpPort})");
    }

    private async Task StopAsync()
    {
        _dialTimer?.Dispose();
        _dialTimer = null;
        _discovery?.Dispose();
        _discovery = null;
        var host = _host;
        _host = null;
        if (host is not null)
        {
            host.PairingRequested -= OnPairingRequested;
            host.PeerNoLongerPaired -= OnPeerNoLongerPaired;
            await host.DisposeAsync().ConfigureAwait(false);
            host.PeerConnected -= OnPeerConnected;
            host.PeerDisconnected -= OnPeerDisconnected;
            Log.Info("Caption sharing off");
        }

        lock (_lock)
        {
            _peer = null;
            _subscription?.Dispose();
            _subscription = null;
        }
    }

    private void DialPairedComputer()
    {
        try
        {
            var host = _host;
            var paired = _pairing.Paired;
            bool connected;
            lock (_lock)
            {
                connected = _peer is not null;
            }

            if (host is null || paired is null || connected)
            {
                _reachableSinceMs = -1;
                return;
            }

            var found = Nearby.Where(d => d.Id == paired.Id).ToList();
            long now = PeerConnection.NowMs;
            if (found.Count == 0)
            {
                _reachableSinceMs = -1;
            }
            else if (_reachableSinceMs < 0)
            {
                _reachableSinceMs = now;
            }

            // Give the other computer the first go, measured from when it (re)appeared: it dials as soon as it starts.
            bool firstToDial = string.CompareOrdinal(host.OwnId, paired.Id) < 0;
            if (!firstToDial && now - Math.Max(_offlineSinceMs, _reachableSinceMs) < (long)SecondaryDialDelay.TotalMilliseconds)
            {
                return;
            }

            var candidates = found.Select(d => new IPEndPoint(d.Address, d.TcpPort)).ToList();
            if (IPAddress.TryParse(paired.Address, out var address))
            {
                candidates.Add(new IPEndPoint(address, paired.Port));
            }

            if (candidates.Count > 0)
            {
                host.EnsureConnected(paired.Id, candidates);
            }
        }
        catch (Exception e)
        {
            Log.Error("Connecting to the paired computer failed", e);
        }
    }

    private void OnPeerConnected(PeerConnection link)
    {
        lock (_lock)
        {
            _subscription?.Dispose();
            _subscription = null;
            _peer = link;
            _peerStatus = null;
            link.MessageReceived += OnMessage;

            // The snapshot goes out first, then every change, in order.
            if (_mode == CaptionSharingMode.Send && link.PeerMode == CaptionSharingMode.Receive)
            {
                _subscription = _feed.Subscribe(link.Post);
            }
        }

        _pairing.UpdateLastSeen(link.PeerId, link.PeerName, link.PeerEndPoint);
        RaiseStateChanged();
    }

    private void OnPeerDisconnected(PeerConnection link)
    {
        link.MessageReceived -= OnMessage;
        bool wasCurrent;
        CaptionSharingMode mode;
        lock (_lock)
        {
            wasCurrent = _peer == link;
            mode = _mode;
            if (wasCurrent)
            {
                _peer = null;
                _peerStatus = null;
                _subscription?.Dispose();
                _subscription = null;
                _offlineSinceMs = PeerConnection.NowMs;
            }
        }

        if (!wasCurrent)
        {
            return;
        }

        if (mode == CaptionSharingMode.Receive)
        {
            // What was being said is out of date now; the reconnect brings the finished sentences.
            lock (_receiveLock)
            {
                CaptionsReceived?.Invoke(Array.Empty<CaptionLine>(), string.Empty);
            }
        }

        RaiseStateChanged();
    }

    private void OnMessage(PeerConnection link, ControlMessage message)
    {
        switch (message)
        {
            case CaptionsMessage captions:
                lock (_receiveLock)
                {
                    lock (_lock)
                    {
                        if (_mode != CaptionSharingMode.Receive || link.PeerMode != CaptionSharingMode.Send || _peer != link)
                        {
                            return;
                        }
                    }

                    var (lines, pending) = _receiver.Accept(captions, DateTimeOffset.Now);
                    CaptionsReceived?.Invoke(lines, pending);
                }

                break;

            case SenderStatusMessage status:
                lock (_lock)
                {
                    string cleaned = SentenceSplitter.NormalizeWhitespace(status.Status);
                    _peerStatus = cleaned.Length == 0 ? null : cleaned.Length > MaxStatusLength ? cleaned[..MaxStatusLength] + "…" : cleaned;
                }

                RaiseStateChanged();
                break;

            case UnpairMessage:
                Log.Info($"{link.PeerName} forgot this computer");
                if (_pairing.IsTrusted(link.PeerId))
                {
                    _pairing.Forget();
                }

                break;
        }
    }

    private void OnPairingChanged()
    {
        // Paired with another computer, or forgotten: drop a connection to one that's no longer trusted.
        PeerConnection? peer;
        lock (_lock)
        {
            peer = _peer;
        }

        if (peer is not null && !_pairing.IsTrusted(peer.PeerId))
        {
            _ = peer.DisposeAsync().AsTask();
        }

        // Newly paired: the computer with the smaller id connects right away and the other waits its turn as
        // usual, so they don't both connect at once (one of the two connections would be dropped again).
        long now = PeerConnection.NowMs;
        Volatile.Write(ref _pairingChangedMs, now);
        _offlineSinceMs = now;
        RaiseStateChanged();
    }

    private void OnPeerNoLongerPaired(string peerId)
    {
        // Forgotten there while this computer was off, or it was paired with another computer since.
        if (PeerConnection.NowMs - Volatile.Read(ref _pairingChangedMs) < (long)NewPairingGrace.TotalMilliseconds)
        {
            return;
        }

        if (_pairing.IsTrusted(peerId))
        {
            Log.Info("The paired computer is no longer paired with this one; forgetting it here too");
            _pairing.Forget();
        }
    }

    private void OnPairingRequested(PairingSession session) => PairingRequested?.Invoke(session);

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception e)
        {
            Log.Error("Updating the sharing state failed", e);
        }
    }
}
