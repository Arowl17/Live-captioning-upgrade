using System.Net;
using System.Net.Sockets;
using System.Text;
using LiveCaptionsUpgrade.Core.Sharing;

namespace LiveCaptionsUpgrade.Core.Tests;

/// <summary>A TCP relay that can stop passing data while keeping the connections open, like Wi-Fi dropping out.</summary>
internal sealed class FreezableRelay : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly IPEndPoint _target;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TcpClient> _clients = new();
    private volatile bool _frozen;

    public FreezableRelay(IPEndPoint target)
    {
        _target = target;
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public IPEndPoint EndPoint => new(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port);

    public bool Frozen
    {
        get => _frozen;
        set => _frozen = value;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        lock (_clients)
        {
            foreach (var client in _clients)
            {
                client.Dispose();
            }
        }

        await Task.CompletedTask;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient inbound;
            try
            {
                inbound = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception)
            {
                return;
            }

            var outbound = new TcpClient();
            try
            {
                await outbound.ConnectAsync(_target);
            }
            catch (SocketException)
            {
                inbound.Dispose();
                outbound.Dispose();
                continue;
            }

            lock (_clients)
            {
                _clients.Add(inbound);
                _clients.Add(outbound);
            }

            _ = PumpAsync(inbound, outbound);
            _ = PumpAsync(outbound, inbound);
        }
    }

    private async Task PumpAsync(TcpClient from, TcpClient to)
    {
        byte[] buffer = new byte[16384];
        try
        {
            var source = from.GetStream();
            var destination = to.GetStream();
            while (true)
            {
                int n = await source.ReadAsync(buffer, _cts.Token);
                if (n == 0)
                {
                    break;
                }

                // While frozen, data is swallowed: neither side hears from the other, but nothing is closed.
                if (!_frozen)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, n), _cts.Token);
                }
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            from.Dispose();
            to.Dispose();
        }
    }
}

public class SharingResilienceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_connection_that_goes_silent_is_dropped_on_both_sides_and_can_be_made_again()
    {
        await using var pc = new TestComputer("PC", CaptionSharingMode.Send);
        await using var laptop = new TestComputer("Laptop", CaptionSharingMode.Receive);
        await PairAsync(pc, laptop);
        await using var relay = new FreezableRelay(pc.EndPoint);

        laptop.Host.EnsureConnected(pc.Identity.Fingerprint, new[] { relay.EndPoint });
        await WaitAsync(() => pc.Host.Links.Count == 1 && laptop.Host.Links.Count == 1);

        // The Wi-Fi drops without either computer closing anything.
        relay.Frozen = true;
        await WaitAsync(() => pc.Host.Links.Count == 0 && laptop.Host.Links.Count == 0);

        relay.Frozen = false;
        laptop.Host.EnsureConnected(pc.Identity.Fingerprint, new[] { relay.EndPoint });
        await WaitAsync(() => pc.Host.Links.Count == 1 && laptop.Host.Links.Count == 1);
    }

    [Fact]
    public async Task Garbage_from_the_paired_computer_only_drops_that_connection()
    {
        await using var pc = new TestComputer("PC", CaptionSharingMode.Send);
        await using var laptop = new TestComputer("Laptop", CaptionSharingMode.Receive);
        await PairAsync(pc, laptop);

        // A misbehaving copy of the laptop's app: right identity, broken message.
        await using (var connection = await FramedConnection.ConnectAsync(pc.EndPoint, laptop.Identity, Timeout, CancellationToken.None))
        {
            await connection.SendAsync(new HelloMessage(PeerConnection.ProtocolVersion, "Laptop", ConnectPurpose.Session, CaptionSharingMode.Receive, laptop.Host.TcpPort, "test"));
            Assert.IsType<HelloMessage>(await connection.ReceiveControlAsync(CancellationToken.None));
            await WaitAsync(() => pc.Host.Links.Count == 1);
            await connection.SendFrameAsync(FrameType.Control, Encoding.UTF8.GetBytes("{\"t\":\"captions\",\"lines\":\"not a list\"}"));
            await WaitAsync(() => pc.Host.Links.Count == 0);
        }

        // The real laptop still connects fine afterwards.
        laptop.Host.EnsureConnected(pc.Identity.Fingerprint, new[] { pc.EndPoint });
        await WaitAsync(() => pc.Host.Links.Count == 1 && laptop.Host.Links.Count == 1);
    }

    [Fact]
    public async Task A_computer_that_was_forgotten_hears_so_when_it_tries_to_connect()
    {
        await using var pc = new TestComputer("PC", CaptionSharingMode.Send);
        await using var laptop = new TestComputer("Laptop", CaptionSharingMode.Receive);
        await PairAsync(pc, laptop);
        string? toldBy = null;
        laptop.Host.PeerNoLongerPaired += id => toldBy = id;

        // Forgotten on the PC while the laptop wasn't connected.
        pc.Pairing.Forget();
        laptop.Host.EnsureConnected(pc.Identity.Fingerprint, new[] { pc.EndPoint });

        await WaitAsync(() => toldBy is not null);
        Assert.Equal(pc.Identity.Fingerprint, toldBy);
        Assert.Empty(pc.Host.Links);
    }

    [Fact]
    public async Task Frames_larger_than_the_limit_are_refused()
    {
        await using var pc = new TestComputer("PC", CaptionSharingMode.Send);
        await using var stranger = new TestComputer("Stranger", CaptionSharingMode.Receive);
        await using var connection = await FramedConnection.ConnectAsync(pc.EndPoint, stranger.Identity, Timeout, CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() => connection.SendFrameAsync(FrameType.Control, new byte[2 * 1024 * 1024]));
    }

    private static async Task PairAsync(TestComputer initiator, TestComputer responder)
    {
        var incoming = new TaskCompletionSource<PairingSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        responder.Host.PairingRequested += s => incoming.TrySetResult(s);
        var outgoing = await initiator.Host.PairAsync(responder.EndPoint).WaitAsync(Timeout);
        var request = await incoming.Task.WaitAsync(Timeout);
        outgoing.Confirm();
        request.Confirm();
        Assert.True(await outgoing.Result.WaitAsync(Timeout));
        Assert.True(await request.Result.WaitAsync(Timeout));
        await Task.Delay(100);
    }

    internal static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(20);
        }
    }
}

public class CaptionFeedConcurrencyTests
{
    [Fact]
    public async Task Every_subscriber_sees_each_sentence_once_in_order_while_others_come_and_go()
    {
        var feed = new CaptionFeed();
        const int Sentences = 3000;
        var speaker = Task.Run(() =>
        {
            for (int i = 1; i <= Sentences; i++)
            {
                feed.Publish(new[] { $"S{i}." }, i % 3 == 0 ? string.Empty : "…");
                if (i % 100 == 0)
                {
                    feed.SetStatus(i % 200 == 0 ? null : "Problem");
                }
            }
        });

        var listeners = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            for (int round = 0; round < 20; round++)
            {
                var ids = new List<long>();
                using (feed.Subscribe(m =>
                {
                    if (m is CaptionsMessage c)
                    {
                        ids.AddRange(c.Lines.Select(l => l.Id));
                    }
                }))
                {
                    await Task.Delay(1);
                }

                // Whatever window this subscriber saw, it's contiguous: nothing skipped, nothing repeated.
                for (int i = 1; i < ids.Count; i++)
                {
                    Assert.Equal(ids[i - 1] + 1, ids[i]);
                }
            }
        })).ToList();

        await Task.WhenAll(listeners.Append(speaker));

        var final = new List<ControlMessage>();
        using var _ = feed.Subscribe(final.Add);
        var snapshot = (CaptionsMessage)final[0];
        Assert.Equal(CaptionFeed.MaxLines, snapshot.Lines.Count);
        Assert.Equal(Sentences, snapshot.Lines[^1].Id);
    }
}

public class SharingControllerResilienceTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lcu-resilience-" + Guid.NewGuid().ToString("N"));
    private TestSharingComputer _pc = null!;
    private TestSharingComputer _laptop = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _pc = new TestSharingComputer("PC", _folder);
        _laptop = new TestSharingComputer("LAPTOP", _folder);
        await _pc.Controller.SetModeAsync(CaptionSharingMode.Send);
        await _laptop.Controller.SetModeAsync(CaptionSharingMode.Receive);
    }

    public async Task DisposeAsync()
    {
        await _pc.DisposeAsync();
        await _laptop.DisposeAsync();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task Rapid_mode_switching_ends_up_working_without_repeats()
    {
        await PairAsync();
        _pc.Controller.Publish(new CaptionUpdate(new[] { "First." }, string.Empty, true));
        await SharingResilienceTests.WaitAsync(() => _laptop.Received.Lines.Count == 1);

        var modes = new[] { CaptionSharingMode.Off, CaptionSharingMode.Receive, CaptionSharingMode.Send, CaptionSharingMode.Off, CaptionSharingMode.Send };
        var switching = Task.Run(async () =>
        {
            for (int i = 0; i < 25; i++)
            {
                await _pc.Controller.SetModeAsync(modes[i % modes.Length]);
            }
        });
        for (int i = 2; i <= 40; i++)
        {
            _pc.Controller.Publish(new CaptionUpdate(new[] { $"Sentence {i}." }, string.Empty, true));
            await Task.Delay(2);
        }

        await switching;
        await _pc.Controller.SetModeAsync(CaptionSharingMode.Send);
        await SharingResilienceTests.WaitAsync(() => _laptop.Received.Lines.Count == 40);

        Assert.Equal(new[] { "First." }.Concat(Enumerable.Range(2, 39).Select(i => $"Sentence {i}.")), _laptop.Received.Lines);
    }

    [Fact]
    public async Task Forgetting_while_the_other_computer_is_off_reaches_it_later()
    {
        await PairAsync();
        await SharingResilienceTests.WaitAsync(() => _laptop.Controller.Describe() == "Showing captions from PC");

        // The laptop app is closed; meanwhile the PC forgets it.
        await _laptop.Controller.SetModeAsync(CaptionSharingMode.Off);
        await _pc.Controller.ForgetAsync();
        await _laptop.RestartAsync(CaptionSharingMode.Receive, newPairingGrace: TimeSpan.Zero);

        await SharingResilienceTests.WaitAsync(() => _laptop.Controller.Paired is null);
        Assert.Equal("Not paired with another computer yet", _laptop.Controller.Describe());
    }

    [Fact]
    public async Task Closing_the_app_while_it_keeps_trying_to_connect_is_quick()
    {
        await PairAsync();
        await _pc.Controller.SetModeAsync(CaptionSharingMode.Off);
        await Task.Delay(500);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await _laptop.Controller.SetModeAsync(CaptionSharingMode.Off);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"Took {watch.Elapsed}");
    }

    private async Task PairAsync()
    {
        var incoming = new TaskCompletionSource<PairingSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        _laptop.Controller.PairingRequested += s => incoming.TrySetResult(s);
        var outgoing = await _pc.Controller.PairAsync(_laptop.EndPoint);
        var request = await incoming.Task.WaitAsync(TimeSpan.FromSeconds(20));
        outgoing.Confirm();
        request.Confirm();
        Assert.True(await outgoing.Result.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.True(await request.Result.WaitAsync(TimeSpan.FromSeconds(20)));
    }
}

public class SharingShutdownTests
{
    [Fact]
    public async Task The_last_sentence_reaches_the_other_computer_when_sharing_is_turned_off()
    {
        string folder = Path.Combine(Path.GetTempPath(), "lcu-shutdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var pc = new TestSharingComputer("PC", folder);
        var laptop = new TestSharingComputer("LAPTOP", folder);
        try
        {
            await pc.Controller.SetModeAsync(CaptionSharingMode.Send);
            await laptop.Controller.SetModeAsync(CaptionSharingMode.Receive);
            var incoming = new TaskCompletionSource<PairingSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            laptop.Controller.PairingRequested += s => incoming.TrySetResult(s);
            var outgoing = await pc.Controller.PairAsync(laptop.EndPoint);
            var request = await incoming.Task.WaitAsync(TimeSpan.FromSeconds(20));
            outgoing.Confirm();
            request.Confirm();
            Assert.True(await outgoing.Result.WaitAsync(TimeSpan.FromSeconds(20)));
            await SharingResilienceTests.WaitAsync(() => laptop.Controller.Describe() == "Showing captions from PC");

            // The PC app closes mid-sentence: it finishes the sentence and sends it on its way out.
            pc.Controller.Publish(new CaptionUpdate(new[] { "Thanks, bye." }, string.Empty, true));
            await pc.Controller.SetModeAsync(CaptionSharingMode.Off);

            await SharingResilienceTests.WaitAsync(() => laptop.Received.Lines.Contains("Thanks, bye."));
        }
        finally
        {
            await pc.DisposeAsync();
            await laptop.DisposeAsync();
            Directory.Delete(folder, recursive: true);
        }
    }
}
