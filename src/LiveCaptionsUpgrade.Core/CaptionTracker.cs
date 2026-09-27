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
/// since Live Captions sometimes tweaks words), so nothing is emitted twice when the text scrolls;</item>
/// <item>remembers the unfinished sentence, so a sentence longer than the visible text doesn't lose
/// its start when that scrolls away.</item>
/// </list>
/// </remarks>
public sealed class CaptionTracker
{
    private const int MaxRemembered = 64;

    // How many recently committed sentences to look for when locating where we left off.
    private const int AnchorDepth = 8;

    // Words from the start of the visible text used to find where it continues the unfinished sentence.
    // With this many words, one of them may differ (Live Captions corrected it as the line scrolled away).
    private const int ProbeWords = 5;
    private const int MinProbeWords = 2;
    private const int MinProbeWordsForOneMismatch = 4;

    // Upper bound on a reconstructed unfinished sentence, in case Live Captions stops punctuating for minutes.
    private const int MaxPendingLength = 4000;

    // Sentences shorter than this ("Yes.", "Maybe.") are too common to recognise without the sentence before them.
    private const int MinDistinctiveLength = 15;

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
        var normalized = NormalizeAll(segments);
        int anchor = FindAnchor(segments, normalized);

        // No finished sentence is visible any more: the unfinished one may be so long that its start has
        // scrolled off the top. Put the start back from what was seen on earlier polls.
        if (anchor < 0 && TryRestoreScrolledOffStart(segments, out string restored))
        {
            segments = SentenceSplitter.Split(restored);
            normalized = NormalizeAll(segments);
            anchor = FindAnchor(segments, normalized);
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

    private static string[] NormalizeAll(IReadOnlyList<CaptionSegment> segments)
    {
        var normalized = new string[segments.Count];
        for (int i = 0; i < segments.Count; i++)
        {
            normalized[i] = TextSimilarity.Normalize(segments[i].Text);
        }

        return normalized;
    }

    /// <summary>
    /// If the visible text continues the pending sentence from part-way through (its start scrolled away),
    /// returns the visible text with the missing start put back in front.
    /// </summary>
    private bool TryRestoreScrolledOffStart(IReadOnlyList<CaptionSegment> segments, out string restored)
    {
        restored = string.Empty;
        if (_pending.Length == 0 || segments.Count == 0)
        {
            return false;
        }

        // Compare words ignoring case and punctuation, which Live Captions often adjusts as it goes.
        string[] pendingWords = _pending.Split(' ');
        string[] pendingKeys = pendingWords.Select(TextSimilarity.Normalize).ToArray();
        string[] firstWords = segments[0].Text.Split(' ');
        string[] probe = firstWords
            .Select(TextSimilarity.Normalize)
            .Where(word => word.Length > 0)
            .Take(ProbeWords)
            .ToArray();
        if (probe.Length == 0)
        {
            return false;
        }

        if (probe.Length < MinProbeWords)
        {
            // Only the last word of the pending sentence is left (e.g. "today?"). Too little to search for,
            // but if it's exactly the word the pending sentence ended on, the rest scrolled away.
            if (firstWords.Length == 1 && pendingWords.Length > 1
                && string.Equals(TrimPunctuation(pendingWords[^1]), TrimPunctuation(firstWords[0]), StringComparison.Ordinal))
            {
                restored = string.Join(" ", pendingWords, 0, pendingWords.Length - 1) + " " + _text;
                return true;
            }

            return false;
        }

        // Visible text still starts where the pending sentence starts: nothing has scrolled away.
        if (StartsWithAt(pendingKeys, 0, probe, Math.Min(probe.Length, pendingKeys.Length)))
        {
            return false;
        }

        // Best place in the pending sentence where the visible text picks up (fewest differences; the
        // latest one on a tie, as repeated words like "no no no" match in several places)...
        int start = -1;
        int bestCost = int.MaxValue;
        for (int j = pendingKeys.Length - probe.Length; j > 0; j--)
        {
            int cost = AlignmentCost(pendingKeys, j, probe, probe.Length);
            if (cost >= 0 && cost < bestCost)
            {
                start = j;
                bestCost = cost;
            }
        }

        // ...or where it overlaps just the end of it, when new words have been added since.
        for (int overlap = Math.Min(probe.Length - 1, pendingKeys.Length - 1); overlap >= MinProbeWords && start < 0; overlap--)
        {
            int j = pendingKeys.Length - overlap;
            if (StartsWithAt(pendingKeys, j, probe, overlap))
            {
                start = j;
            }
        }

        if (start < 0)
        {
            return false;
        }

        restored = string.Join(" ", pendingWords, 0, start) + " " + _text;
        if (restored.Length > MaxPendingLength)
        {
            int cut = restored.IndexOf(' ', restored.Length - MaxPendingLength);
            restored = cut > 0 ? restored[(cut + 1)..] : restored[^MaxPendingLength..];
        }

        return true;
    }

    private static string TrimPunctuation(string word) => word.Trim('.', ',', '!', '?', '…', '。', '！', '？', '"', '\'', '”', '’', ')', '(');

    /// <summary>True if <paramref name="probe"/>'s first <paramref name="count"/> words appear at <paramref name="index"/>.</summary>
    private static bool StartsWithAt(string[] words, int index, string[] probe, int count) =>
        AlignmentCost(words, index, probe, count) >= 0;

    /// <summary>
    /// How well <paramref name="probe"/>'s first <paramref name="count"/> words match the words at <paramref name="index"/>:
    /// 0 for identical, higher for each corrected or different word, or -1 if they don't match.
    /// </summary>
    private static int AlignmentCost(string[] words, int index, string[] probe, int count)
    {
        if (count <= 0 || index + count > words.Length)
        {
            return -1;
        }

        int allowedMismatches = count >= MinProbeWordsForOneMismatch ? 1 : 0;
        int cost = 0;
        for (int k = 0; k < count; k++)
        {
            string word = words[index + k];
            if (string.Equals(word, probe[k], StringComparison.Ordinal))
            {
                continue;
            }

            if (IsSameWord(word, probe[k]))
            {
                cost += 1;
            }
            else if (--allowedMismatches >= 0)
            {
                cost += 10;
            }
            else
            {
                return -1;
            }
        }

        return cost;
    }

    /// <summary>Similar enough to be the same word after a small correction ("services" / "service", "their" / "there").</summary>
    private static bool IsSameWord(string a, string b)
    {
        int longer = Math.Max(a.Length, b.Length);
        if (Math.Min(a.Length, b.Length) < 3 || Math.Abs(a.Length - b.Length) > 3)
        {
            return false;
        }

        int distance = TextSimilarity.Levenshtein(a, b);
        return distance <= 3 && distance * 5 <= longer * 2;
    }

    /// <summary>Returns the index of the last segment that was already committed, or -1 if none is visible.</summary>
    private int FindAnchor(IReadOnlyList<CaptionSegment> segments, string[] normalized)
    {
        if (_committed.Count == 0 || normalized.Length == 0)
        {
            return -1;
        }

        int oldest = Math.Max(0, _committed.Count - AnchorDepth);

        // First pass: the sentence before it must match too, so a repeated sentence ("Yes.") isn't mistaken
        // for an earlier one. The oldest visible sentence has nothing before it, so it's accepted as is.
        for (int k = _committed.Count - 1; k >= oldest; k--)
        {
            for (int i = normalized.Length - 1; i >= 0; i--)
            {
                if (IsAnchorCandidate(segments, i) && Matches(normalized[i], k, i)
                    && (i == 0 || (k > 0 && Matches(normalized[i - 1], k - 1, i - 1))))
                {
                    return i;
                }
            }
        }

        // Second pass: the sentence before it may have been revised since. Accept a match on its own,
        // but only for a sentence distinctive enough not to be a coincidence.
        for (int k = _committed.Count - 1; k >= oldest; k--)
        {
            for (int i = normalized.Length - 1; i >= 0; i--)
            {
                if (IsAnchorCandidate(segments, i) && normalized[i].Length >= MinDistinctiveLength && Matches(normalized[i], k, i))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    // Only finished sentences are ever committed, so an unfinished one ("Maybe") can't be where we left off,
    // however much it looks like an earlier sentence ("Maybe.").
    private static bool IsAnchorCandidate(IReadOnlyList<CaptionSegment> segments, int index) => segments[index].IsTerminated;

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

        // The first visible segment may be the tail of a sentence whose start scrolled off the top. The oldest
        // visible text has always been seen and committed on an earlier poll, so even a short tail counts.
        return segmentIndex == 0
            && candidate.Length > 0
            && committed.Length > candidate.Length
            && committed[committed.Length - candidate.Length - 1] == ' '
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
