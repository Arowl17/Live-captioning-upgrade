namespace LiveCaptionsUpgrade.Core;

/// <summary>The result of feeding one snapshot of Live Captions text to <see cref="CaptionTracker"/>.</summary>
/// <param name="NewSentences">Sentences that became final since the previous snapshot, in spoken order.</param>
/// <param name="Pending">Text that is still being recognised and may change.</param>
/// <param name="TextChanged">True when the Live Captions text differs from the previous snapshot.</param>
public sealed record CaptionUpdate(IReadOnlyList<string> NewSentences, string Pending, bool TextChanged);

/// <summary>
/// Turns the rolling text block shown by Windows Live Captions into a stream of finished sentences.
/// </summary>
/// <remarks>
/// Live Captions shows only the last few lines: old lines scroll off the top, new words are appended,
/// and the sentence being spoken is revised as recognition improves. The tracker therefore:
/// <list type="bullet">
/// <item>treats every sentence except the last as final (a later sentence exists, so the speaker moved on);</item>
/// <item>also finalises a punctuated last sentence once the text has been unchanged for the idle delay;</item>
/// <item>finds where it left off by locating recently committed sentences in the new text (fuzzily,
/// since Live Captions sometimes tweaks words), so nothing is emitted twice when the text scrolls.</item>
/// </list>
/// </remarks>
public sealed class CaptionTracker
{
    private const int MaxRemembered = 64;

    // How many recently committed sentences to look for when locating where we left off.
    private const int AnchorDepth = 8;

    // A leading fragment (the start of the sentence scrolled away) must be at least this long to count as a match.
    private const int MinFragmentLength = 12;

    private readonly TimeSpan _idleFinalizeDelay;
    private readonly double _similarityThreshold;
    private readonly List<(string Text, string Normalized)> _committed = new();

    private string _text = string.Empty;
    private DateTimeOffset _lastChange = DateTimeOffset.MinValue;
    private bool _idleHandled;
    private string _pending = string.Empty;

    public CaptionTracker(TimeSpan idleFinalizeDelay, double similarityThreshold = 0.8)
    {
        if (similarityThreshold is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(similarityThreshold));
        }

        _idleFinalizeDelay = idleFinalizeDelay;
        _similarityThreshold = similarityThreshold;
    }

    /// <summary>Feeds the current Live Captions text. Call this on every poll, even when the text has not changed.</summary>
    public CaptionUpdate Process(string? rawText, DateTimeOffset now)
    {
        string text = SentenceSplitter.NormalizeWhitespace(rawText);
        bool changed = !string.Equals(text, _text, StringComparison.Ordinal);

        if (changed)
        {
            _text = text;
            _lastChange = now;
            _idleHandled = false;
        }
        else if (_idleHandled || now - _lastChange < _idleFinalizeDelay)
        {
            return new CaptionUpdate(Array.Empty<string>(), _pending, TextChanged: false);
        }
        else
        {
            _idleHandled = true;
        }

        bool idle = !changed;
        var newSentences = Commit(includeTerminatedLast: idle, includeUnterminatedLast: false);
        return new CaptionUpdate(newSentences, _pending, changed);
    }

    /// <summary>Finalises everything still pending, including an unfinished last sentence. Call when stopping.</summary>
    public IReadOnlyList<string> Flush() => Commit(includeTerminatedLast: true, includeUnterminatedLast: true);

    /// <summary>Forgets all state, e.g. after reconnecting to a new Live Captions window.</summary>
    public void Reset()
    {
        _committed.Clear();
        _text = string.Empty;
        _lastChange = DateTimeOffset.MinValue;
        _idleHandled = false;
        _pending = string.Empty;
    }

    private IReadOnlyList<string> Commit(bool includeTerminatedLast, bool includeUnterminatedLast)
    {
        var segments = SentenceSplitter.Split(_text);
        var normalized = new string[segments.Count];
        for (int i = 0; i < segments.Count; i++)
        {
            normalized[i] = TextSimilarity.Normalize(segments[i].Text);
        }

        int finalCount = segments.Count - 1;
        if (segments.Count > 0)
        {
            bool lastIsTerminated = segments[^1].IsTerminated;
            if (lastIsTerminated ? includeTerminatedLast : includeUnterminatedLast)
            {
                finalCount = segments.Count;
            }
        }

        finalCount = Math.Max(finalCount, 0);

        int anchor = FindAnchor(normalized);
        var added = new List<string>();
        for (int i = anchor + 1; i < finalCount; i++)
        {
            // A segment made only of punctuation (e.g. a stray "…") is not worth emitting.
            if (normalized[i].Length == 0)
            {
                continue;
            }

            Remember(segments[i].Text, normalized[i]);
            added.Add(segments[i].Text);
        }

        int pendingStart = Math.Max(anchor + 1, finalCount);
        _pending = string.Join(" ", segments.Skip(pendingStart).Select(s => s.Text));
        return added;
    }

    /// <summary>Returns the index of the last segment that was already committed, or -1 if none is visible.</summary>
    private int FindAnchor(string[] normalized)
    {
        if (_committed.Count == 0 || normalized.Length == 0)
        {
            return -1;
        }

        int oldest = Math.Max(0, _committed.Count - AnchorDepth);

        // First pass: also require the sentence before it to match, so a repeated short
        // sentence ("Yes.") is not mistaken for an earlier one.
        for (int k = _committed.Count - 1; k >= oldest; k--)
        {
            for (int i = normalized.Length - 1; i >= 0; i--)
            {
                if (Matches(normalized[i], k, i) && (k == 0 || i == 0 || Matches(normalized[i - 1], k - 1, i - 1)))
                {
                    return i;
                }
            }
        }

        // Second pass: the preceding sentence may have been revised; accept a match on its own.
        for (int k = _committed.Count - 1; k >= oldest; k--)
        {
            for (int i = normalized.Length - 1; i >= 0; i--)
            {
                if (Matches(normalized[i], k, i))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private bool Matches(string candidate, int committedIndex, int segmentIndex)
    {
        string committed = _committed[committedIndex].Normalized;
        if (string.Equals(candidate, committed, StringComparison.Ordinal))
        {
            return true;
        }

        int shorter = Math.Min(candidate.Length, committed.Length);
        int longer = Math.Max(candidate.Length, committed.Length);
        if (longer > 0 && (double)shorter / longer >= _similarityThreshold
            && TextSimilarity.Ratio(candidate, committed) >= _similarityThreshold)
        {
            return true;
        }

        // The first visible segment may be the tail of a sentence whose start scrolled off the top.
        return segmentIndex == 0
            && candidate.Length >= MinFragmentLength
            && committed.EndsWith(candidate, StringComparison.Ordinal);
    }

    private void Remember(string text, string normalized)
    {
        _committed.Add((text, normalized));
        if (_committed.Count > MaxRemembered)
        {
            _committed.RemoveRange(0, _committed.Count - MaxRemembered);
        }
    }
}
