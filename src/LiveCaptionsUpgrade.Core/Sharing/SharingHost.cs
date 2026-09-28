using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;

namespace LiveCaptionsUpgrade.Core.Sharing;

/// <summary>
/// Accepts and dials connections, runs pairing, and keeps at most one live
/// <see cref="PeerConnection"/> per paired computer.
/// </summary>
public sealed class SharingHost : IAsyncDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(8);

    // On a home network a live computer answers in milliseconds; don't wait long on stale addresses.
    private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(4);

    // After a declined or failed pairing request, ignore that address for a while.
    private const long PairingCooldownMs = 60_000;

    private readonly DeviceIdentity _identity;
    private readonly Func<string> _deviceName;
    private readonly ITrustStore _trust;
    private readonly CaptionSharingMode _mode;
    private readonly string _appVersion;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, PeerConnection> _links = new();
    private readonly ConcurrentDictionary<string, byte> _dialing = new();
    private readonly ConcurrentDictionary<IPAddress, long> _pairingCooldown = new();
    private int _pairingInProgress;

    public SharingHost(DeviceIdentity identity, Func<string> deviceName, ITrustStore trust, CaptionSharingMode mode, string appVersion, int tcpPort = FramedConnection.DefaultPort)
    {
        _identity = identity;
        _deviceName = deviceName;
        _trust = trust;
        _mode = mode;
        _appVersion = appVersion;
        _listener = new TcpListener(IPAddress.Any, tcpPort);
        try
        {
            _listener.Start();
        }
        catch (SocketException) when (tcpPort != 0)
        {
            // Another program has the usual port: take any free one (discovery tells the other computer).
            Log.Warn($"Port {tcpPort} is in use; listening on another port");
            _listener = new TcpListener(IPAddress.Any, 0);
            _listener.Start();
        }

        TcpPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int TcpPort { get; }

    public string OwnId => _identity.Fingerprint;

    public CaptionSharingMode Mode => _mode;

    /// <summary>Accept incoming pairing requests (the user still has to confirm each one).</summary>
    public bool AllowIncomingPairing { get; set; } = true;

    /// <summary>Raised on a network thread; subscribe to the connection's events here, before messages arrive.</summary>
    public event Action<PeerConnection>? PeerConnected;

    public event Action<PeerConnection>? PeerDisconnected;

    /// <summary>Another computer wants to pair; show the code and call Confirm or Reject.</summary>
    public event Action<PairingSession>? PairingRequested;

    /// <summary>
    /// The paired computer answered a connection attempt by saying it isn't paired with this one (it was
    /// forgotten or paired with another computer there). Raised with the paired computer's id.
    /// </summary>
    public event Action<string>? PeerNoLongerPaired;

    public IReadOnlyCollection<PeerConnection> Links => _links.Values.ToList();

    public PeerConnection? GetLink(string peerId) => _links.TryGetValue(peerId, out var link) ? link : null;

    public void Start() => _ = Task.Run(() => AcceptLoopAsync(_cts.Token));

    /// <summary>Starts pairing with another computer; this side shows the code too.</summary>
    public async Task<PairingSession> PairAsync(IPEndPoint endPoint, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _pairingInProgress, 1) != 0)
        {
            throw new InvalidOperationException("Another pairing is already in progress.");
        }

        FramedConnection? connection = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            connection = await FramedConnection.ConnectAsync(endPoint, _identity, HandshakeTimeout, cts.Token).ConfigureAwait(false);
            cts.CancelAfter(HandshakeTimeout);
            await connection.SendAsync(Hello(ConnectPurpose.Pair), cts.Token).ConfigureAwait(false);
            var hello = await connection.ReceiveControlAsync(cts.Token).ConfigureAwait(false) as HelloMessage
                ?? throw new InvalidDataException("The other computer declined the pairing request.");

            // The dialled port is where it listens, whatever its hello says.
            hello = hello with { TcpPort = endPoint.Port };
            var session = await PairingSession.RunInitiatorAsync(connection, OwnId, hello, cts.Token).ConfigureAwait(false);
            _ = FinishPairingAsync(session);
            return session;
        }
        catch
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            Volatile.Write(ref _pairingInProgress, 0);
            throw;
        }
    }

    /// <summary>Connects to a paired computer unless already connected. Safe to call repeatedly.</summary>
    public void EnsureConnected(string peerId, IEnumerable<IPEndPoint> candidates)
    {
        if (_links.ContainsKey(peerId) || !_trust.IsTrusted(peerId) || !_dialing.TryAdd(peerId, 0))
        {
            return;
        }

        var list = candidates.Distinct().ToList();
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var endPoint in list)
                {
                    if (_links.ContainsKey(peerId) || _cts.IsCancellationRequested)
                    {
                        return;
                    }

                    if (await TryDialAsync(peerId, endPoint).ConfigureAwait(false))
                    {
                        return;
                    }
                }
            }
            finally
            {
                _dialing.TryRemove(peerId, out _);
            }
        });
    }

    public async Task DisconnectAsync(string peerId)
    {
        if (_links.TryGetValue(peerId, out var link))
        {
            await link.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task FinishPairingAsync(PairingSession session)
    {
        try
        {
            await session.CompleteAsync(_trust, _cts.Token).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _pairingInProgress, 0);
        }
    }

    private async Task<bool> TryDialAsync(string peerId, IPEndPoint endPoint)
    {
        FramedConnection? connection = null;
        try
        {
            connection = await FramedConnection.ConnectAsync(endPoint, _identity, DialTimeout, _cts.Token).ConfigureAwait(false);
            if (connection.PeerFingerprint != peerId)
            {
                // Something else answered at that address (e.g. the router gave it to another computer).
                Log.Warn($"Unexpected device at {endPoint}; expected {DeviceIdentity.ShortFingerprint(peerId)}");
                await connection.DisposeAsync().ConfigureAwait(false);
                return false;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cts.CancelAfter(HandshakeTimeout);
            await connection.SendAsync(Hello(ConnectPurpose.Session), cts.Token).ConfigureAwait(false);
            var reply = await connection.ReceiveControlAsync(cts.Token).ConfigureAwait(false);
            if (reply is not HelloMessage { Purpose: ConnectPurpose.Session } hello)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                if (reply is UnpairMessage)
                {
                    // Its identity was checked above, so this really is the paired computer saying so.
                    Log.Info($"{DeviceIdentity.ShortFingerprint(peerId)} at {endPoint} is no longer paired with this computer");
                    PeerNoLongerPaired?.Invoke(peerId);
                }

                return false;
            }

            StartLink(connection, hello with { TcpPort = endPoint.Port });
            return true;
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or InvalidDataException
            or AuthenticationException or JsonException or ObjectDisposedException)
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            return false;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            // Not cancellable: the handler must run to dispose the client, even when shutting down.
            _ = Task.Run(() => HandleIncomingAsync(client, token));
        }
    }

    private async Task HandleIncomingAsync(TcpClient client, CancellationToken token)
    {
        FramedConnection? connection = null;
        bool handedOff = false;
        try
        {
            connection = await FramedConnection.AcceptAsync(client, _identity, HandshakeTimeout, token).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(HandshakeTimeout);
            if (await connection.ReceiveControlAsync(cts.Token).ConfigureAwait(false) is not HelloMessage hello)
            {
                return;
            }

            switch (hello.Purpose)
            {
                case ConnectPurpose.Session when _trust.IsTrusted(connection.PeerFingerprint):
                    await connection.SendAsync(Hello(ConnectPurpose.Session), cts.Token).ConfigureAwait(false);
                    StartLink(connection, hello);
                    handedOff = true;
                    break;

                case ConnectPurpose.Pair when AllowIncomingPairing && PairingRequested is not null:
                    var address = connection.RemoteEndPoint.Address;
                    if (_pairingCooldown.TryGetValue(address, out long until) && PeerConnection.NowMs < until)
                    {
                        Log.Info($"Ignoring repeated pairing request from {address}");
                        return;
                    }

                    if (Interlocked.Exchange(ref _pairingInProgress, 1) != 0)
                    {
                        return; // one at a time
                    }

                    try
                    {
                        await connection.SendAsync(Hello(ConnectPurpose.Pair), cts.Token).ConfigureAwait(false);
                        var session = await PairingSession.RunResponderAsync(connection, OwnId, hello, cts.Token).ConfigureAwait(false);
                        handedOff = true;
                        _ = FinishPairingAsync(session);
                        _ = session.Result.ContinueWith(
                            t =>
                            {
                                if (!t.Result)
                                {
                                    _pairingCooldown[address] = PeerConnection.NowMs + PairingCooldownMs;
                                }
                            },
                            TaskScheduler.Default);
                        PairingRequested?.Invoke(session);
                    }
                    finally
                    {
                        if (!handedOff)
                        {
                            Volatile.Write(ref _pairingInProgress, 0);
                        }
                    }

                    break;

                case ConnectPurpose.Session:
                    // A computer that still thinks it's paired with this one: tell it, so it stops waiting for us.
                    Log.Info($"Refused connection from {connection.RemoteEndPoint.Address}: not paired with it");
                    await connection.SendAsync(new UnpairMessage(), cts.Token).ConfigureAwait(false);
                    break;

                default:
                    Log.Info($"Refused pairing request from {connection.RemoteEndPoint.Address}");
                    break;
            }
        }
        catch (Exception e)
        {
            Log.Info($"Incoming connection failed: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            if (!handedOff)
            {
                if (connection is not null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    client.Dispose();
                }
            }
        }
    }

    private void StartLink(FramedConnection connection, HelloMessage hello)
    {
        if (hello.ProtocolVersion != PeerConnection.ProtocolVersion)
        {
            Log.Warn($"{DeviceNames.Sanitize(hello.DeviceName)} uses protocol {hello.ProtocolVersion} (this computer: {PeerConnection.ProtocolVersion}); update Live Captions Upgrade on both computers if anything misbehaves");
        }

        var link = new PeerConnection(connection, hello);

        // Both computers may dial each other at the same moment. Both sides keep the connection
        // started by the computer with the smaller id, so they always agree on which one survives.
        string preferredInitiator = string.CompareOrdinal(OwnId, link.PeerId) < 0 ? OwnId : link.PeerId;
        string InitiatorOf(PeerConnection l) => l.IsInitiator ? OwnId : l.PeerId;

        PeerConnection? replaced = null;
        bool accepted = true;
        _links.AddOrUpdate(link.PeerId, link, (_, existing) =>
        {
            if (!existing.IsClosed && InitiatorOf(existing) == preferredInitiator && InitiatorOf(link) != preferredInitiator)
            {
                accepted = false;
                return existing;
            }

            replaced = existing;
            accepted = true;
            return link;
        });

        if (!accepted)
        {
            _ = link.DisposeAsync().AsTask();
            return;
        }

        link.Closed += OnLinkClosed;
        if (replaced is not null)
        {
            replaced.Closed -= OnLinkClosed;
            _ = replaced.DisposeAsync().AsTask();
            PeerDisconnected?.Invoke(replaced);
        }

        Log.Info($"Connected to {link.PeerName} ({link.PeerEndPoint.Address})");

        // Let listeners subscribe to the connection's events before any message can arrive.
        try
        {
            PeerConnected?.Invoke(link);
        }
        catch (Exception e)
        {
            Log.Error("PeerConnected handler failed", e);
        }

        _ = Task.Run(() => link.RunAsync(_cts.Token));
    }

    private void OnLinkClosed(PeerConnection link)
    {
        if (_links.TryRemove(new KeyValuePair<string, PeerConnection>(link.PeerId, link)))
        {
            Log.Info($"Disconnected from {link.PeerName}");
            PeerDisconnected?.Invoke(link);
        }
    }

    private HelloMessage Hello(ConnectPurpose purpose) =>
        new(PeerConnection.ProtocolVersion, _deviceName(), purpose, _mode, TcpPort, _appVersion);

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();

        // Let the last messages go out (e.g. the sentence that was being spoken when the app was closed).
        await Task.WhenAll(_links.Values.Select(l => l.CloseGracefullyAsync(TimeSpan.FromMilliseconds(500)))).ConfigureAwait(false);

        // Also ends any connection that came up in the meantime.
        _cts.Cancel();
        foreach (var link in _links.Values)
        {
            await link.DisposeAsync().ConfigureAwait(false);
        }

        // _cts is not disposed: connection attempts still finishing in the background read its token.
    }
}
