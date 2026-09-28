using System.Net;
using LiveCaptionsUpgrade.Core.Sharing;

namespace LiveCaptionsUpgrade.Core.Tests;

/// <summary>One computer's caption sharing, as the app runs it, on a free local port.</summary>
internal sealed class TestSharingComputer : IAsyncDisposable
{
    private readonly byte[] _identity = DeviceIdentity.Create().ExportPkcs12();
    private readonly object _lock = new();
    private readonly List<string> _lines = new();
    private string _pending = string.Empty;

    public TestSharingComputer(string name, string folder)
    {
        Name = name;
        PairingPath = Path.Combine(folder, name + "-pairing.json");
        Controller = Create();
    }

    public string Name { get; }

    public string PairingPath { get; }

    public SharingController Controller { get; private set; }

    public IPEndPoint EndPoint => new(IPAddress.Loopback, Controller.ListenPort ?? 0);

    public (List<string> Lines, string Pending) Received
    {
        get
        {
            lock (_lock)
            {
                return (_lines.ToList(), _pending);
            }
        }
    }

    /// <summary>Quits and starts the app again (same identity and pairing, fresh memory).</summary>
    public async Task RestartAsync(CaptionSharingMode mode, TimeSpan? newPairingGrace = null)
    {
        await Controller.DisposeAsync();
        lock (_lock)
        {
            _lines.Clear();
            _pending = string.Empty;
        }

        Controller = Create(newPairingGrace);
        await Controller.SetModeAsync(mode);
    }

    public async ValueTask DisposeAsync() => await Controller.DisposeAsync();

    private SharingController Create(TimeSpan? newPairingGrace = null)
    {
        var controller = new SharingController(new PairingStore(PairingPath), () => DeviceIdentity.FromPkcs12(_identity), Name, "test", tcpPort: 0, discoveryPort: 0)
        {
            DialInterval = TimeSpan.FromMilliseconds(200),
            SecondaryDialDelay = TimeSpan.FromMilliseconds(600),
            NewPairingGrace = newPairingGrace ?? TimeSpan.FromSeconds(30),
        };
        controller.CaptionsReceived += (lines, pending, replaced) =>
        {
            lock (_lock)
            {
                _lines.RemoveRange(_lines.Count - replaced, replaced);
                _lines.AddRange(lines.Select(l => l.Text));
                _pending = pending;
            }
        };
        return controller;
    }
}

public sealed class SharingControllerTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lcu-controller-" + Guid.NewGuid().ToString("N"));
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
    public async Task Before_pairing_the_laptop_says_how_to_pair()
    {
        Assert.Equal("Not paired with another computer yet", _laptop.Controller.Describe());
        Assert.StartsWith("Not paired", _laptop.Controller.DescribeProblemForReceiver());
        Assert.Null(_pc.Controller.DescribeProblemForReceiver());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task After_pairing_captions_flow_from_the_pc_to_the_laptop()
    {
        // Said before the computers were even paired: the laptop still gets it when it connects.
        _pc.Controller.Publish(new CaptionUpdate(new[] { "Good morning, thanks for calling." }, "How", true));
        await PairAsync(_laptop, _pc);

        await WaitAsync(() => _laptop.Received.Lines.Count == 1 && _laptop.Received.Pending == "How");
        _pc.Controller.Publish(new CaptionUpdate(new[] { "How can I help you today?" }, string.Empty, true));
        await WaitAsync(() => _laptop.Received.Lines.Count == 2);

        Assert.Equal(new[] { "Good morning, thanks for calling.", "How can I help you today?" }, _laptop.Received.Lines);
        Assert.Equal("Sending captions to LAPTOP", _pc.Controller.Describe());
        Assert.Equal("Showing captions from PC", _laptop.Controller.Describe());
        Assert.Null(_laptop.Controller.DescribeProblemForReceiver());
    }

    [Fact]
    public async Task A_number_rewritten_on_the_pc_is_replaced_on_the_laptop()
    {
        await PairAsync(_pc, _laptop);
        _pc.Controller.Publish(new CaptionUpdate(new[] { "Card number?", "Three, zero." }, string.Empty, true));
        await WaitAsync(() => _laptop.Received.Lines.Count == 2);

        _pc.Controller.Publish(new CaptionUpdate(new[] { "3032 5817 4802 8929." }, string.Empty, true, Replaced: 1));
        await WaitAsync(() => _laptop.Received.Lines.Contains("3032 5817 4802 8929."));

        Assert.Equal(new[] { "Card number?", "3032 5817 4802 8929." }, _laptop.Received.Lines);
    }

    [Fact]
    public async Task Live_Captions_problems_on_the_pc_are_shown_on_the_laptop()
    {
        await PairAsync(_pc, _laptop);
        await WaitAsync(() => _laptop.Controller.Describe() == "Showing captions from PC");

        _pc.Controller.SetSenderStatus("Waiting for Windows Live Captions to start…");
        await WaitAsync(() => _laptop.Controller.DescribeProblemForReceiver() == "PC: Waiting for Windows Live Captions to start…");

        _pc.Controller.SetSenderStatus(null);
        await WaitAsync(() => _laptop.Controller.DescribeProblemForReceiver() is null);
    }

    [Fact]
    public async Task Turning_sharing_off_and_on_again_reconnects_without_repeating_captions()
    {
        await PairAsync(_pc, _laptop);
        _pc.Controller.Publish(new CaptionUpdate(new[] { "One." }, "Tw", true));
        await WaitAsync(() => _laptop.Received.Lines.Count == 1 && _laptop.Received.Pending == "Tw");

        await _pc.Controller.SetModeAsync(CaptionSharingMode.Off);
        await WaitAsync(() => _laptop.Controller.DescribeProblemForReceiver()?.StartsWith("Waiting for PC", StringComparison.Ordinal) == true);
        Assert.Equal(string.Empty, _laptop.Received.Pending);

        // Recorded while sharing was off, so the laptop can catch up.
        _pc.Controller.Publish(new CaptionUpdate(new[] { "Two." }, string.Empty, true));
        await _pc.Controller.SetModeAsync(CaptionSharingMode.Send);
        await WaitAsync(() => _laptop.Received.Lines.Count == 2);
        _pc.Controller.Publish(new CaptionUpdate(new[] { "Three." }, string.Empty, true));
        await WaitAsync(() => _laptop.Received.Lines.Count == 3);

        Assert.Equal(new[] { "One.", "Two.", "Three." }, _laptop.Received.Lines);
    }

    [Fact]
    public async Task Restarting_the_laptop_app_fills_it_in_again()
    {
        await PairAsync(_pc, _laptop);
        _pc.Controller.Publish(new CaptionUpdate(new[] { "Before the restart." }, string.Empty, true));
        await WaitAsync(() => _laptop.Received.Lines.Count == 1);

        await _laptop.RestartAsync(CaptionSharingMode.Receive);
        await WaitAsync(() => _laptop.Received.Lines.Count == 1);

        Assert.Equal(new[] { "Before the restart." }, _laptop.Received.Lines);
        Assert.Equal("Showing captions from PC", _laptop.Controller.Describe());
    }

    [Fact]
    public async Task Both_set_to_show_explains_what_to_change()
    {
        await _pc.Controller.SetModeAsync(CaptionSharingMode.Receive);
        await PairAsync(_pc, _laptop);

        await WaitAsync(() => _laptop.Controller.DescribeProblemForReceiver()?.Contains("isn't set to send captions", StringComparison.Ordinal) == true);
        Assert.Equal("Connected to PC, but it isn't set to send captions", _laptop.Controller.Describe());
    }

    [Fact]
    public async Task Forgetting_on_one_computer_unpairs_both()
    {
        await PairAsync(_pc, _laptop);
        await WaitAsync(() => _laptop.Controller.Describe() == "Showing captions from PC");

        await _pc.Controller.ForgetAsync();

        await WaitAsync(() => _laptop.Controller.Paired is null);
        Assert.Null(_pc.Controller.Paired);
        await WaitAsync(() => _laptop.Controller.Describe() == "Not paired with another computer yet");
        Assert.False(File.Exists(_laptop.PairingPath));
    }

    [Fact]
    public async Task Pairing_needs_sharing_on()
    {
        await _pc.Controller.SetModeAsync(CaptionSharingMode.Off);

        Assert.False(_pc.Controller.IsRunning);
        Assert.Equal("Caption sharing is off", _pc.Controller.Describe());
        await Assert.ThrowsAsync<InvalidOperationException>(() => _pc.Controller.PairAsync(_laptop.EndPoint));
    }

    private static async Task PairAsync(TestSharingComputer initiator, TestSharingComputer responder)
    {
        var incoming = new TaskCompletionSource<PairingSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        responder.Controller.PairingRequested += s => incoming.TrySetResult(s);
        var outgoing = await initiator.Controller.PairAsync(responder.EndPoint).WaitAsync(Timeout);
        var request = await incoming.Task.WaitAsync(Timeout);
        Assert.Equal(outgoing.Code, request.Code);
        outgoing.Confirm();
        request.Confirm();
        Assert.True(await outgoing.Result.WaitAsync(Timeout));
        Assert.True(await request.Result.WaitAsync(Timeout));
    }

    private static async Task WaitAsync(Func<bool> condition)
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
