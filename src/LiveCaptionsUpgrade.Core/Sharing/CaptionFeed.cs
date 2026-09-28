namespace LiveCaptionsUpgrade.Core.Sharing;

/// <summary>
/// The sending side of caption sharing: remembers recent finished sentences and passes every change
/// on to subscribers (the connected computer). A new subscriber first gets a snapshot of the recent
/// sentences, so a computer that connects mid-conversation (or reconnects) can fill in what it missed.
/// Thread-safe.
/// </summary>
public sealed class CaptionFeed
{
    /// <summary>How long sentences are kept for a computer that connects later (the longest scroll-back setting).</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(60);

    public const int MaxLines = Scrollback.MaxLines;

    /// <summary>Upper bound on the text in one snapshot, which keeps it well within one network frame.</summary>
    public const int MaxSnapshotChars = 200_000;

    private readonly object _lock = new();
    private readonly List<(long Id, string Text, DateTimeOffset Time)> _lines = new();
    private readonly List<Action<ControlMessage>> _subscribers = new();
    private readonly Func<DateTimeOffset> _clock;
    private long _nextId = 1;
    private string _pending = string.Empty;
    private string? _status;

    public CaptionFeed(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>Identifies this run of the app; the receiver starts counting lines afresh when it changes.</summary>
    public string Session { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Records new finished sentences and the sentence being spoken, and passes them on.</summary>
    public void Publish(IReadOnlyList<string> sentences, string pending)
    {
        lock (_lock)
        {
            var now = _clock();
            var added = new List<SharedLine>(sentences.Count);
            foreach (string sentence in sentences)
            {
                if (string.IsNullOrWhiteSpace(sentence))
                {
                    continue;
                }

                long id = _nextId++;
                _lines.Add((id, sentence, now));
                added.Add(new SharedLine(id, sentence, 0));
            }

            bool pendingChanged = !string.Equals(pending, _pending, StringComparison.Ordinal);
            _pending = pending;
            Prune(now);
            if (added.Count == 0 && !pendingChanged)
            {
                return;
            }

            Notify(new CaptionsMessage(Session, added, pending, Snapshot: false));
        }
    }

    /// <summary>Sets the problem message to pass on (e.g. "Waiting for Live Captions to start"), or null.</summary>
    public void SetStatus(string? status)
    {
        lock (_lock)
        {
            if (status == _status)
            {
                return;
            }

            _status = status;
            Notify(new SenderStatusMessage(status));
        }
    }

    /// <summary>
    /// Sends <paramref name="subscriber"/> a snapshot, then every later change until the returned object is
    /// disposed. The callback runs while the feed is locked, so it must be quick (e.g. queue the message).
    /// </summary>
    public IDisposable Subscribe(Action<ControlMessage> subscriber)
    {
        lock (_lock)
        {
            var now = _clock();
            Prune(now);
            subscriber(Snapshot(now));
            subscriber(new SenderStatusMessage(_status));
            _subscribers.Add(subscriber);
        }

        return new Subscription(this, subscriber);
    }

    private CaptionsMessage Snapshot(DateTimeOffset now)
    {
        // Newest first until the size limit, then back into spoken order.
        var lines = new List<SharedLine>();
        int chars = 0;
        for (int i = _lines.Count - 1; i >= 0; i--)
        {
            var (id, text, time) = _lines[i];
            chars += text.Length;
            if (chars > MaxSnapshotChars)
            {
                break;
            }

            lines.Add(new SharedLine(id, text, (long)Math.Max(0, (now - time).TotalMilliseconds)));
        }

        lines.Reverse();
        return new CaptionsMessage(Session, lines, _pending, Snapshot: true);
    }

    private void Prune(DateTimeOffset now)
    {
        int expired = 0;
        while (expired < _lines.Count && (now - _lines[expired].Time > Retention || _lines.Count - expired > MaxLines))
        {
            expired++;
        }

        _lines.RemoveRange(0, expired);
    }

    private void Notify(ControlMessage message)
    {
        foreach (var subscriber in _subscribers)
        {
            try
            {
                subscriber(message);
            }
            catch (Exception e)
            {
                Log.Error("Passing on captions failed", e);
            }
        }
    }

    private void Unsubscribe(Action<ControlMessage> subscriber)
    {
        lock (_lock)
        {
            _subscribers.Remove(subscriber);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private CaptionFeed? _feed;
        private readonly Action<ControlMessage> _subscriber;

        public Subscription(CaptionFeed feed, Action<ControlMessage> subscriber)
        {
            _feed = feed;
            _subscriber = subscriber;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _feed, null)?.Unsubscribe(_subscriber);
        }
    }
}

/// <summary>
/// The showing side of caption sharing: turns received messages into new lines, skipping any it
/// already has (a reconnect resends recent sentences). Not thread-safe; use from one thread.
/// </summary>
public sealed class CaptionFeedReceiver
{
    private const int MaxSessionLength = 64;
    private const int MaxTextLength = 4000;

    private string? _session;
    private long _lastId;

    /// <summary>Returns the sentences not received before (with the time they were finished) and the live text.</summary>
    public (IReadOnlyList<CaptionLine> Lines, string Pending) Accept(CaptionsMessage message, DateTimeOffset now)
    {
        string session = message.Session ?? string.Empty;
        if (session.Length > MaxSessionLength)
        {
            session = session[..MaxSessionLength];
        }

        if (!string.Equals(session, _session, StringComparison.Ordinal))
        {
            // The other computer's app was restarted: its lines are numbered from the start again.
            _session = session;
            _lastId = 0;
        }

        var lines = new List<CaptionLine>();
        foreach (var line in message.Lines ?? Array.Empty<SharedLine>())
        {
            if (line is null || line.Id <= _lastId)
            {
                continue;
            }

            _lastId = line.Id;
            string text = Clean(line.Text);
            if (text.Length == 0)
            {
                continue;
            }

            var age = TimeSpan.FromMilliseconds(Math.Clamp(line.AgeMs, 0, (long)CaptionFeed.Retention.TotalMilliseconds));
            lines.Add(new CaptionLine(text, now - age));
        }

        return (lines, Clean(message.Pending));
    }

    private static string Clean(string? text)
    {
        string cleaned = SentenceSplitter.NormalizeWhitespace(text);
        return cleaned.Length > MaxTextLength ? cleaned[..MaxTextLength] : cleaned;
    }
}
