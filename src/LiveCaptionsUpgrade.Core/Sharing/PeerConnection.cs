using System.Diagnostics;
using System.Net;
using System.Threading.Channels;

namespace LiveCaptionsUpgrade.Core.Sharing;

/// <summary>
/// An authenticated connection to the paired computer. Messages are sent in the order they are
/// posted; a heartbeat notices a computer that went away without closing the connection.
/// </summary>
public sealed class PeerConnection : IAsyncDisposable
{
    public const int ProtocolVersion = 1;

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(8);
    private static readonly long TicksPerMs = Math.Max(1, Stopwatch.Frequency / 1000);

    private readonly FramedConnection _connection;
    private readonly Channel<ControlMessage> _outgoing = Channel.CreateUnbounded<ControlMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private long _lastReceiveMs;
    private int _closed;

    public PeerConnection(FramedConnection connection, HelloMessage peerHello)
    {
        _connection = connection;
        PeerId = connection.PeerFingerprint;
        PeerName = DeviceNames.Sanitize(peerHello.DeviceName);
        PeerMode = Enum.IsDefined(peerHello.Mode) ? peerHello.Mode : CaptionSharingMode.Off;
        PeerAppVersion = peerHello.AppVersion is { Length: <= 32 } version ? version : "?";
        PeerEndPoint = new IPEndPoint(connection.RemoteEndPoint.Address, PairingSession.IsValidPort(peerHello.TcpPort) ? peerHello.TcpPort : FramedConnection.DefaultPort);
        _lastReceiveMs = NowMs;
    }

    public string PeerId { get; }

    public string PeerName { get; }

    /// <summary>Whether the other computer sends or shows captions.</summary>
    public CaptionSharingMode PeerMode { get; }

    public string PeerAppVersion { get; }

    /// <summary>Where the other computer accepts connections (for reconnecting later).</summary>
    public IPEndPoint PeerEndPoint { get; }

    public bool IsInitiator => _connection.IsInitiator;

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Monotonic milliseconds.</summary>
    public static long NowMs => Stopwatch.GetTimestamp() / TicksPerMs;

    /// <summary>Raised on the connection's reader for every message other than heartbeats, in order.</summary>
    public event Action<PeerConnection, ControlMessage>? MessageReceived;

    public event Action<PeerConnection>? Closed;

    /// <summary>Queues a message; messages go out in the order they were posted. Safe from any thread.</summary>
    public void Post(ControlMessage message) => _outgoing.Writer.TryWrite(message);

    /// <summary>Runs until the connection ends. Hello messages must already have been exchanged.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        var token = linked.Token;
        try
        {
            var writer = WriteLoopAsync(token);
            var heartbeat = HeartbeatLoopAsync(token);
            await ReadLoopAsync(token).ConfigureAwait(false);
            linked.Cancel();
            await Task.WhenAll(writer, heartbeat).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            Log.Warn($"Connection to {PeerName} ended: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            await CloseAsync().ConfigureAwait(false);
        }
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var message = await _connection.ReceiveControlAsync(token).ConfigureAwait(false);
            if (message is null)
            {
                return;
            }

            Volatile.Write(ref _lastReceiveMs, NowMs);
            switch (message)
            {
                case PingMessage ping:
                    Post(new PongMessage(ping.Timestamp));
                    break;
                case PongMessage:
                    break;
                default:
                    try
                    {
                        MessageReceived?.Invoke(this, message);
                    }
                    catch (Exception e)
                    {
                        Log.Error("Handling a message failed", e);
                    }

                    break;
            }
        }
    }

    private async Task WriteLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (var message in _outgoing.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                await _connection.SendAsync(message, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            // The connection broke: stop reading too.
            Log.Info($"Sending to {PeerName} failed: {e.GetType().Name}: {e.Message}");
            await DropAsync().ConfigureAwait(false);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, token).ConfigureAwait(false);
                if (NowMs - Volatile.Read(ref _lastReceiveMs) > HeartbeatTimeout.TotalMilliseconds)
                {
                    Log.Warn($"{PeerName} stopped responding");
                    await DropAsync().ConfigureAwait(false);
                    return;
                }

                Post(new PingMessage(NowMs));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Ends the connection from inside; the read loop then finishes and closes everything.</summary>
    private async Task DropAsync()
    {
        _cts.Cancel();
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _outgoing.Writer.TryComplete();
        _cts.Cancel();
        await _connection.DisposeAsync().ConfigureAwait(false);
        try
        {
            Closed?.Invoke(this);
        }
        catch (Exception e)
        {
            Log.Error("Handling a disconnect failed", e);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
    }
}
