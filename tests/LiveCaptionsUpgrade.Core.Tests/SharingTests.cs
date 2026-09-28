using System.Collections.Concurrent;
using System.Net;
using System.Text;
using LiveCaptionsUpgrade.Core.Sharing;

namespace LiveCaptionsUpgrade.Core.Tests;

/// <summary>A computer with caption sharing on, listening on a free local port.</summary>
internal sealed class TestComputer : IAsyncDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lcu-sharing-" + Guid.NewGuid().ToString("N"));

    public TestComputer(string name, CaptionSharingMode mode)
    {
        Name = name;
        Identity = DeviceIdentity.Create();
        Pairing = new PairingStore(Path.Combine(_folder, "pairing.json"));
        Host = new SharingHost(Identity, () => Name, Pairing, mode, "test", tcpPort: 0);
        Host.Start();
    }

    public string Name { get; }

    public DeviceIdentity Identity { get; }

    public PairingStore Pairing { get; }

    public SharingHost Host { get; }

    public IPEndPoint EndPoint => new(IPAddress.Loopback, Host.TcpPort);

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        Identity.Dispose();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}

public class SharingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public void Identity_round_trips_through_pkcs12()
    {
        using var identity = DeviceIdentity.Create();
        using var loaded = DeviceIdentity.FromPkcs12(identity.ExportPkcs12());

        Assert.Equal(identity.Fingerprint, loaded.Fingerprint);
        Assert.True(loaded.Certificate.HasPrivateKey);
        Assert.True(DiscoveryService.IsValidId(identity.Fingerprint));
    }

    [Fact]
    public async Task Pairs_connects_and_delivers_captions_including_what_was_said_before_connecting()
    {
        await using var pc = new TestComputer("Home PC", CaptionSharingMode.Send);
        await using var laptop = new TestComputer("Work Laptop", CaptionSharingMode.Receive);
        await PairAsync(pc, laptop);

        Assert.True(pc.Pairing.IsTrusted(laptop.Identity.Fingerprint));
        Assert.True(laptop.Pairing.IsTrusted(pc.Identity.Fingerprint));
        Assert.Equal("Work Laptop", pc.Pairing.Paired!.Name);
        Assert.Equal(laptop.Host.TcpPort, pc.Pairing.Paired.Port);
        Assert.Equal(pc.Host.TcpPort, laptop.Pairing.Paired!.Port);

        var feed = new CaptionFeed();
        feed.Publish(new[] { "Hello, this is Anna from billing.", "How can I help?" }, "I'm calling about");
        using var sender = new SenderSide(pc.Host, feed);
        var receiver = new ReceiverSide(laptop.Host);

        laptop.Host.EnsureConnected(pc.Identity.Fingerprint, new[] { pc.EndPoint });
        await receiver.WaitForAsync(r => r.Lines.Count == 2 && r.Pending == "I'm calling about");

        feed.Publish(new[] { "I'm calling about my invoice number 4471." }, string.Empty);
        feed.Publish(Array.Empty<string>(), "It says");
        feed.SetStatus("Waiting for Windows Live Captions to start…");
        await receiver.WaitForAsync(r => r.Lines.Count == 3 && r.Pending == "It says" && r.Status == "Waiting for Windows Live Captions to start…");

        Assert.Equal(
            new[] { "Hello, this is Anna from billing.", "How can I help?", "I'm calling about my invoice number 4471." },
            receiver.Snapshot().Lines);
        Assert.Equal(CaptionSharingMode.Send, receiver.Link!.PeerMode);
        Assert.Equal("Home PC", receiver.Link.PeerName);
    }

    [Fact]
    public async Task Reconnecting_fills_in_what_was_missed_without_repeating_anything()
    {
        await using var pc = new TestComputer("PC", CaptionSharingMode.Send);
        await using var laptop = new TestComputer("Laptop", CaptionSharingMode.Receive);
        await PairAsync(pc, laptop);

        var feed = new CaptionFeed();
        using var sender = new SenderSide(pc.Host, feed);
        var receiver = new ReceiverSide(laptop.Host);

        laptop.Host.EnsureConnected(pc.Identity.Fingerprint, new[] { pc.EndPoint });
        feed.Publish(new[] { "One." }, string.Empty);
        await receiver.WaitForAsync(r => r.Lines.Count == 1);

        // The Wi-Fi drops; meanwhile the conversation goes on.
        await laptop.Host.DisconnectAsync(pc.Identity.Fingerprint);
        await receiver.WaitForAsync(r => r.Link is null);
        feed.Publish(new[] { "Two." }, string.Empty);
        feed.Publish(new[] { "Three." }, "Fo");

        // This time the PC dials, once it has noticed the old connection is gone.
        var deadline = DateTime.UtcNow + Timeout;
        while (pc.Host.Links.Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Empty(pc.Host.Links);
        pc.Host.EnsureConnected(laptop.Identity.Fingerprint, new[] { laptop.EndPoint });
        await receiver.WaitForAsync(r => r.Lines.Count == 3 && r.Pending == "Fo");
        feed.Publish(new[] { "Four." }, string.Empty);
        await receiver.WaitForAsync(r => r.Lines.Count == 4);

        Assert.Equal(new[] { "One.", "Two.", "Three.", "Four." }, receiver.Snapshot().Lines);
    }

    [Fact]
    public async Task Captions_published_while_connecting_arrive_exactly_once_and_in_order()
    {
        await using var pc = new TestComputer("PC", CaptionSharingMode.Send);
        await using var laptop = new TestComputer("Laptop", CaptionSharingMode.Receive);
        await PairAsync(pc, laptop);

        var feed = new CaptionFeed();
        using var sender = new SenderSide(pc.Host, feed);
        var receiver = new ReceiverSide(laptop.Host);

        // Speech keeps coming in on another thread while the connection (and its snapshot) is set up.
        using var stop = new CancellationTokenSource();
        int published = 0;
        var speaker = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                feed.Publish(new[] { $"Sentence {++published}." }, string.Empty);
                await Task.Delay(1);
            }
        });

        await Task.Delay(50);
        laptop.Host.EnsureConnected(pc.Identity.Fingerprint, new[] { pc.EndPoint });
        await receiver.WaitForAsync(r => r.Lines.Count >= 50);
        stop.Cancel();
        await speaker;
        await receiver.WaitForAsync(r => r.Lines.Count == published);

        Assert.Equal(Enumerable.Range(1, published).Select(i => $"Sentence {i}."), receiver.Snapshot().Lines);
    }

    [Fact]
    public async Task Rejected_pairing_trusts_nobody()
    {
        await using var a = new TestComputer("A", CaptionSharingMode.Send);
        await using var b = new TestComputer("B", CaptionSharingMode.Receive);
        var incoming = new TaskCompletionSource<PairingSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Host.PairingRequested += s => incoming.TrySetResult(s);

        var outgoing = await a.Host.PairAsync(b.EndPoint).WaitAsync(Timeout);
        var request = await incoming.Task.WaitAsync(Timeout);
        Assert.Equal(outgoing.Code, request.Code);
        request.Reject();

        Assert.False(await outgoing.Result.WaitAsync(Timeout)); // A finds out without having to decide
        Assert.False(await request.Result.WaitAsync(Timeout));
        Assert.Null(a.Pairing.Paired);
        Assert.Null(b.Pairing.Paired);
    }

    [Fact]
    public async Task Unpaired_computer_cannot_connect()
    {
        await using var intruder = new TestComputer("Intruder", CaptionSharingMode.Receive);
        await using var pc = new TestComputer("PC", CaptionSharingMode.Send);

        // The intruder trusts the PC, but the PC never paired with it.
        intruder.Pairing.Trust(pc.Identity.Fingerprint, "PC", pc.EndPoint);
        bool connected = false;
        pc.Host.PeerConnected += _ => connected = true;
        intruder.Host.PeerConnected += _ => connected = true;

        intruder.Host.EnsureConnected(pc.Identity.Fingerprint, new[] { pc.EndPoint });
        await Task.Delay(1500);

        Assert.False(connected);
        Assert.Empty(pc.Host.Links);
    }

    [Fact]
    public async Task Dialling_an_address_that_now_belongs_to_another_computer_is_refused()
    {
        await using var pc = new TestComputer("PC", CaptionSharingMode.Send);
        await using var laptop = new TestComputer("Laptop", CaptionSharingMode.Receive);
        await using var stranger = new TestComputer("Stranger", CaptionSharingMode.Send);
        await PairAsync(pc, laptop);

        bool connected = false;
        laptop.Host.PeerConnected += _ => connected = true;
        laptop.Host.EnsureConnected(pc.Identity.Fingerprint, new[] { stranger.EndPoint });
        await Task.Delay(1500);

        Assert.False(connected);
    }

    [Fact]
    public async Task Pairing_again_replaces_the_earlier_computer()
    {
        await using var pc = new TestComputer("PC", CaptionSharingMode.Send);
        await using var oldLaptop = new TestComputer("Old laptop", CaptionSharingMode.Receive);
        await using var newLaptop = new TestComputer("New laptop", CaptionSharingMode.Receive);

        await PairAsync(pc, oldLaptop);
        await PairAsync(newLaptop, pc);

        Assert.Equal(newLaptop.Identity.Fingerprint, pc.Pairing.Paired!.Id);
        Assert.False(pc.Pairing.IsTrusted(oldLaptop.Identity.Fingerprint));
    }

    [Fact]
    public async Task Unpair_message_reaches_the_other_computer()
    {
        await using var pc = new TestComputer("PC", CaptionSharingMode.Send);
        await using var laptop = new TestComputer("Laptop", CaptionSharingMode.Receive);
        await PairAsync(pc, laptop);
        var receiver = new ReceiverSide(laptop.Host);

        var pcLink = new TaskCompletionSource<PeerConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        pc.Host.PeerConnected += l => pcLink.TrySetResult(l);
        laptop.Host.EnsureConnected(pc.Identity.Fingerprint, new[] { pc.EndPoint });
        (await pcLink.Task.WaitAsync(Timeout)).Post(new UnpairMessage());

        await receiver.WaitForAsync(r => r.Unpaired);
    }

    private static async Task PairAsync(TestComputer initiator, TestComputer responder)
    {
        var incoming = new TaskCompletionSource<PairingSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRequest(PairingSession s) => incoming.TrySetResult(s);
        responder.Host.PairingRequested += OnRequest;
        try
        {
            var outgoing = await initiator.Host.PairAsync(responder.EndPoint).WaitAsync(Timeout);
            var request = await incoming.Task.WaitAsync(Timeout);

            Assert.Equal(outgoing.Code, request.Code);
            Assert.Matches("^[0-9]{6}$", outgoing.Code);
            Assert.Equal(responder.Name, outgoing.PeerName);
            Assert.Equal(initiator.Name, request.PeerName);
            Assert.Equal(responder.Host.Mode, outgoing.PeerMode);

            outgoing.Confirm();
            request.Confirm();
            Assert.True(await outgoing.Result.WaitAsync(Timeout));
            Assert.True(await request.Result.WaitAsync(Timeout));
        }
        finally
        {
            responder.Host.PairingRequested -= OnRequest;
        }

        // Let both sides finish recording the pairing before connecting.
        await Task.Delay(100);
    }

    /// <summary>What the sending computer's app does: pass the feed on to each computer that connects.</summary>
    private sealed class SenderSide : IDisposable
    {
        private readonly SharingHost _host;
        private readonly CaptionFeed _feed;
        private readonly ConcurrentDictionary<PeerConnection, IDisposable> _subscriptions = new();

        public SenderSide(SharingHost host, CaptionFeed feed)
        {
            _host = host;
            _feed = feed;
            host.PeerConnected += OnConnected;
            host.PeerDisconnected += OnDisconnected;
        }

        public void Dispose()
        {
            _host.PeerConnected -= OnConnected;
            _host.PeerDisconnected -= OnDisconnected;
        }

        private void OnConnected(PeerConnection link) => _subscriptions[link] = _feed.Subscribe(link.Post);

        private void OnDisconnected(PeerConnection link)
        {
            if (_subscriptions.TryRemove(link, out var subscription))
            {
                subscription.Dispose();
            }
        }
    }

    /// <summary>What the showing computer's app does: collect what arrives.</summary>
    private sealed class ReceiverSide
    {
        private readonly object _lock = new();
        private readonly CaptionFeedReceiver _receiver = new();
        private readonly List<string> _lines = new();
        private string _pending = string.Empty;
        private string? _status;
        private bool _unpaired;

        public ReceiverSide(SharingHost host)
        {
            host.PeerConnected += link =>
            {
                lock (_lock)
                {
                    Link = link;
                }

                link.MessageReceived += OnMessage;
            };
            host.PeerDisconnected += link =>
            {
                lock (_lock)
                {
                    if (Link == link)
                    {
                        Link = null;
                    }
                }
            };
        }

        public PeerConnection? Link { get; private set; }

        public (List<string> Lines, string Pending, string? Status, bool Unpaired, PeerConnection? Link) Snapshot()
        {
            lock (_lock)
            {
                return (_lines.ToList(), _pending, _status, _unpaired, Link);
            }
        }

        public async Task WaitForAsync(Func<(List<string> Lines, string Pending, string? Status, bool Unpaired, PeerConnection? Link), bool> condition)
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (!condition(Snapshot()))
            {
                if (DateTime.UtcNow > deadline)
                {
                    var s = Snapshot();
                    throw new TimeoutException($"Condition not met. Lines: [{string.Join(" | ", s.Lines)}], pending: \"{s.Pending}\", status: {s.Status}");
                }

                await Task.Delay(20);
            }
        }

        private void OnMessage(PeerConnection link, ControlMessage message)
        {
            lock (_lock)
            {
                switch (message)
                {
                    case CaptionsMessage captions:
                        var (lines, pending, replaced) = _receiver.Accept(captions, DateTimeOffset.Now);
                        _lines.RemoveRange(_lines.Count - replaced, replaced);
                        _lines.AddRange(lines.Select(l => l.Text));
                        _pending = pending;
                        break;
                    case SenderStatusMessage status:
                        _status = status.Status;
                        break;
                    case UnpairMessage:
                        _unpaired = true;
                        break;
                }
            }
        }
    }
}

public class CaptionFeedTests
{
    [Fact]
    public void Snapshot_holds_recent_sentences_with_their_age_and_the_live_text()
    {
        var now = new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);
        var feed = new CaptionFeed(() => now);
        feed.Publish(new[] { "First." }, string.Empty);
        now = now.AddSeconds(30);
        feed.Publish(new[] { "Second." }, "Third");
        now = now.AddSeconds(10);

        var messages = new List<ControlMessage>();
        using var _ = feed.Subscribe(messages.Add);

        var snapshot = Assert.IsType<CaptionsMessage>(messages[0]);
        Assert.True(snapshot.Snapshot);
        Assert.Equal(new[] { "First.", "Second." }, snapshot.Lines.Select(l => l.Text));
        Assert.Equal(new long[] { 40_000, 10_000 }, snapshot.Lines.Select(l => l.AgeMs));
        Assert.Equal("Third", snapshot.Pending);
        Assert.Equal(new SenderStatusMessage(null), messages[1]);
    }

    [Fact]
    public void Sentences_older_than_an_hour_are_dropped()
    {
        var now = DateTimeOffset.Now;
        var feed = new CaptionFeed(() => now);
        feed.Publish(new[] { "Old." }, string.Empty);
        now = now.AddMinutes(61);
        feed.Publish(new[] { "New." }, string.Empty);

        var messages = new List<ControlMessage>();
        using var _ = feed.Subscribe(messages.Add);

        Assert.Equal(new[] { "New." }, ((CaptionsMessage)messages[0]).Lines.Select(l => l.Text));
    }

    [Fact]
    public void Snapshot_is_limited_in_size_keeping_the_newest_sentences()
    {
        var feed = new CaptionFeed();
        string sentence = new string('a', 999) + ".";
        for (int i = 0; i < 1000; i++)
        {
            feed.Publish(new[] { sentence }, string.Empty);
        }

        var messages = new List<ControlMessage>();
        using var _ = feed.Subscribe(messages.Add);
        var lines = ((CaptionsMessage)messages[0]).Lines;

        Assert.Equal(CaptionFeed.MaxSnapshotChars / 1000, lines.Count);
        Assert.Equal(1000, lines[^1].Id);

        // Even in a script where every character takes three bytes, it fits in one frame.
        Assert.True(ControlJson.Serialize(new CaptionsMessage("s", lines.Select(l => l with { Text = new string('ş', 1000) }).ToList(), "", true)).Length < 1024 * 1024);
    }

    [Fact]
    public void Only_changes_are_passed_on_and_not_after_unsubscribing()
    {
        var feed = new CaptionFeed();
        var messages = new List<ControlMessage>();
        var subscription = feed.Subscribe(messages.Add);
        messages.Clear();

        feed.Publish(Array.Empty<string>(), "Hel");
        feed.Publish(Array.Empty<string>(), "Hel");
        feed.SetStatus(null);
        feed.SetStatus("Problem");
        feed.SetStatus("Problem");
        subscription.Dispose();
        feed.Publish(new[] { "Hello." }, string.Empty);

        Assert.Equal(2, messages.Count);
        Assert.Equal("Hel", ((CaptionsMessage)messages[0]).Pending);
        Assert.Equal(new SenderStatusMessage("Problem"), messages[1]);
    }

    [Fact]
    public void Receiver_skips_lines_it_has_and_starts_over_for_a_new_session()
    {
        var receiver = new CaptionFeedReceiver();
        var now = DateTimeOffset.Now;

        var first = receiver.Accept(new CaptionsMessage("a", new[] { new SharedLine(1, "One.", 0), new SharedLine(2, "Two.", 0) }, "Th", false), now);
        var again = receiver.Accept(new CaptionsMessage("a", new[] { new SharedLine(1, "One.", 5000), new SharedLine(2, "Two.", 4000), new SharedLine(3, "Three.", 1000) }, "", true), now);
        var restarted = receiver.Accept(new CaptionsMessage("b", new[] { new SharedLine(1, "Fresh start.", 0) }, "", true), now);

        Assert.Equal(new[] { "One.", "Two." }, first.Lines.Select(l => l.Text));
        Assert.Equal("Th", first.Pending);
        Assert.Equal(new[] { "Three." }, again.Lines.Select(l => l.Text));
        Assert.Equal(now.AddSeconds(-1), again.Lines[0].Time);
        Assert.Equal(new[] { "Fresh start." }, restarted.Lines.Select(l => l.Text));
    }

    [Fact]
    public void A_rewritten_number_replaces_its_first_digits_for_the_other_computer_too()
    {
        var feed = new CaptionFeed();
        var messages = new List<ControlMessage>();
        using var _ = feed.Subscribe(messages.Add);
        feed.Publish(new[] { "My card is.", "Four, one." }, string.Empty);
        feed.Publish(new[] { "One, one." }, string.Empty);
        feed.Publish(new[] { "4111 1111 2222 3333.", "Expiry?" }, string.Empty, replaced: 2);

        var update = (CaptionsMessage)messages[^1];
        Assert.Equal(new[] { 2L, 0L }, update.Lines.Select(l => l.Replaces));

        // One connecting later only gets the number as it is now.
        var later = new List<ControlMessage>();
        using var __ = feed.Subscribe(later.Add);
        Assert.Equal(new[] { "My card is.", "4111 1111 2222 3333.", "Expiry?" }, ((CaptionsMessage)later[0]).Lines.Select(l => l.Text));
    }

    [Fact]
    public void Receiver_takes_back_replaced_lines_whether_shown_already_or_not()
    {
        var receiver = new CaptionFeedReceiver();
        var now = DateTimeOffset.Now;
        receiver.Accept(new CaptionsMessage("a", new[] { new SharedLine(1, "Card.", 0), new SharedLine(2, "Four.", 0) }, "", false), now);

        // Replacing a line returned before, and one that came in the same message.
        var (lines, _, replaced) = receiver.Accept(
            new CaptionsMessage("a", new[] { new SharedLine(3, "One.", 0), new SharedLine(4, "41.", 0, Replaces: 2), new SharedLine(5, "Yes.", 0) }, "", false),
            now);

        Assert.Equal(new[] { "41.", "Yes." }, lines.Select(l => l.Text));
        Assert.Equal(1, replaced);

        // After reconnecting: a snapshot with the line that replaced ones it had.
        (lines, _, replaced) = receiver.Accept(
            new CaptionsMessage("a", new[] { new SharedLine(1, "Card.", 0), new SharedLine(4, "41.", 0, 2), new SharedLine(5, "Yes.", 0), new SharedLine(6, "4111.", 0, 4) }, "", true),
            now);
        Assert.Equal(new[] { "4111." }, lines.Select(l => l.Text));
        Assert.Equal(2, replaced);
    }

    [Fact]
    public void Lines_from_before_replacements_existed_still_arrive_and_the_other_way_round()
    {
        // As an older version sends a line, and receives one (it ignores what it doesn't know).
        var old = (CaptionsMessage)ControlJson.Deserialize(System.Text.Encoding.UTF8.GetBytes(
            "{\"t\":\"captions\",\"session\":\"a\",\"lines\":[{\"id\":1,\"text\":\"Hi.\",\"ageMs\":0}],\"pending\":\"\",\"snapshot\":false}"))!;
        Assert.Equal(new SharedLine(1, "Hi.", 0), old.Lines[0]);

        string json = System.Text.Encoding.UTF8.GetString(ControlJson.Serialize(new CaptionsMessage("a", new[] { new SharedLine(2, "12.", 0, 1) }, "", false)));
        Assert.Contains("\"replaces\":1", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Receiver_cleans_up_what_it_is_given()
    {
        var receiver = new CaptionFeedReceiver();
        var (lines, pending, _) = receiver.Accept(
            new CaptionsMessage("a", new[] { new SharedLine(1, "  Two \r\n lines. ", -50), new SharedLine(2, "   ", 0), new SharedLine(3, new string('x', 5000), long.MaxValue) }, new string('y', 5000), false),
            DateTimeOffset.Now);

        Assert.Equal("Two lines.", lines[0].Text);
        Assert.Equal(2, lines.Count);
        Assert.Equal(4000, lines[1].Text.Length);
        Assert.Equal(4000, pending.Length);
    }
}

public class SharingStorageTests
{
    [Fact]
    public void Pairing_is_remembered_updated_and_forgotten()
    {
        string folder = Path.Combine(Path.GetTempPath(), "lcu-pairing-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "pairing.json");
        try
        {
            string id = new string('a', 64);
            var store = new PairingStore(path);
            int changes = 0;
            store.Changed += () => changes++;
            store.Trust(id, "LAPTOP", new IPEndPoint(IPAddress.Parse("192.168.1.20"), 47820));

            var reloaded = new PairingStore(path);
            Assert.True(reloaded.IsTrusted(id));
            Assert.False(reloaded.IsTrusted(new string('b', 64)));
            Assert.Equal(new PairedComputer(id, "LAPTOP", "192.168.1.20", 47820), reloaded.Paired);

            reloaded.UpdateLastSeen(id, "LAPTOP-2", new IPEndPoint(IPAddress.Parse("192.168.1.31"), 47820));
            Assert.Equal("192.168.1.31", new PairingStore(path).Paired!.Address);

            store.Forget();
            Assert.Null(new PairingStore(path).Paired);
            Assert.Equal(2, changes);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void Damaged_pairing_file_means_not_paired()
    {
        string path = Path.Combine(Path.GetTempPath(), "lcu-pairing-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{ not json");
            Assert.Null(new PairingStore(path).Paired);

            File.WriteAllText(path, "{\"Id\":\"not-a-fingerprint\",\"Name\":\"X\",\"Address\":null,\"Port\":1}");
            Assert.Null(new PairingStore(path).Paired);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Discovery_beacons_are_validated()
    {
        string id = new string('c', 64);
        var good = new DiscoveryService.Beacon("live-captions-upgrade", 1, id, "LAPTOP‮", CaptionSharingMode.Receive, 47820, false);

        Assert.True(DiscoveryService.TryDecode(DiscoveryService.EncodeBeacon(good), out var decoded));
        Assert.Equal("LAPTOP", decoded.Name);
        Assert.Equal(CaptionSharingMode.Receive, decoded.Mode);
        Assert.False(DiscoveryService.TryDecode(DiscoveryService.EncodeBeacon(good with { App = "lan-connect" }), out _));
        Assert.False(DiscoveryService.TryDecode(DiscoveryService.EncodeBeacon(good with { Id = "abc" }), out _));
        Assert.False(DiscoveryService.TryDecode(DiscoveryService.EncodeBeacon(good with { Port = 0 }), out _));
        Assert.False(DiscoveryService.TryDecode(Encoding.UTF8.GetBytes("garbage"), out _));
    }

    [Fact]
    public void Messages_round_trip_and_keep_non_english_text_readable()
    {
        var message = new CaptionsMessage("s", new[] { new SharedLine(7, "Merhaba, nasılsınız? Çok iyiyim.", 12) }, "Görüşürüz", true);
        byte[] json = ControlJson.Serialize(message);

        Assert.Contains("nasılsınız", Encoding.UTF8.GetString(json));
        var decoded = Assert.IsType<CaptionsMessage>(ControlJson.Deserialize(json));
        Assert.Equal(message.Lines, decoded.Lines);
        Assert.Equal(message.Pending, decoded.Pending);
    }

    [Theory]
    [InlineData("123456", "123 456")]
    [InlineData("12345", "12345")]
    public void Pairing_codes_are_shown_in_two_groups(string code, string shown)
    {
        Assert.Equal(shown, PairingCode.Format(code));
    }
}
