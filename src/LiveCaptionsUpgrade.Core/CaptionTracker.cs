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
/// its start when that scrolls away, and a moment of blank text doesn't lose it at all;</item>
/// <item>drops words it has already emitted when Live Captions changes its mind about where a sentence
/// ends ("I called yesterday." becoming "I called yesterday, and they said no.").</item>
/// </list>
/// </remarks>
public sealed class CaptionTracker
{
    private const int MaxRemembered = 64;

    // How many recently committed sentences to look for when locating where we left off. Generous, because
    // Live Captions may end a long sentence (an address, a card number) too early several times over.
    private const int AnchorDepth = 16;

    // Words from the start of the visible text used to find where it continues the unfinished sentence.
    // With this many words, one of them may differ (Live Captions corrected it as the line scrolled away).
    private const int ProbeWords = 5;
    private const int MinProbeWords = 2;
    private const int MinProbeWordsForOneMismatch = 4;

    // Upper bound on a reconstructed unfinished sentence, in case Live Captions stops punctuating for minutes.
    private const int MaxPendingLength = 4000;

    // Sentences shorter than this ("Yes.", "Maybe.") are too common to recognise without the sentence before them.
    private const int MinDistinctiveLength = 15;

    // A sentence at the top of the text may be several emitted sentences that Live Captions has since merged.
    private const int MaxMergedSentences = 3;

    // Longest run of already-emitted words looked for at the start of the text when nothing else matches.
    private const int MaxOverlapWords = 80;

    // Emitted sentences that Live Captions may have rewritten since, so they no longer appear as such
    // ("Riverside, Texas seven." becoming part of "Riverside TX 75231 in the main.").
    private const int MaxRewrittenSentences = 8;

    // Fewest words for the top line to be recognised as the end of an emitted sentence despite a rewritten word.
    private const int MinFuzzyTailWords = 5;

    // A distinctive sentence identical to one of this many just emitted is not emitted again.
    private const int RepeatWindow = 3;

    private readonly TimeSpan _idleFinalizeDelay;
    private readonly double _similarityThreshold;
    private readonly List<(string Text, string Normalized)> _committed = new();

    private string _text = string.Empty;
    private DateTimeOffset _lastChange = DateTimeOffset.MinValue;
    private bool _idleHandled;
    private string _pending = string.Empty;

    // The top line starts mid-sentence (lowercase), i.e. the start of its sentence has scrolled away.
    private bool _topIsCutOff;

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
        if (_text.Length == 0)
        {
            // Live Captions shows nothing, usually just for a moment: keep the unfinished sentence. If the text
            // stays blank (idle) or we're stopping, that sentence is over, so emit it.
            return includeTerminatedLast || includeUnterminatedLast ? CommitPending() : Array.Empty<string>();
        }

        var segments = SentenceSplitter.Split(_text);
        var normalized = NormalizeAll(segments);
        var (anchor, anchorCommitted) = FindAnchor(segments, normalized);

        string[] texts = segments.Select(segment => segment.Text).ToArray();
        if (anchor < 0)
        {
            // No finished sentence is visible. The top of the text is either the end of what was already emitted
            // (e.g. part of a sentence Live Captions has since merged with the next), or the unfinished sentence
            // after its start scrolled away. Go with whichever explanation more of the words support.
            // (A single overlapping word is only trusted if the text doesn't start like the pending sentence:
            // "... I think. Think again" merged into "... I think, think again" starts with a new "think".)
            var emittedOverlap = EmittedOverlap(texts, allowSingleWord: !StartsLikePending(segments));
            if (TryRestoreScrolledOffStart(segments, out string restored, out int restoredWords) && restoredWords > emittedOverlap.Count)
            {
                segments = SentenceSplitter.Split(restored);
                normalized = NormalizeAll(segments);
                (anchor, anchorCommitted) = FindAnchor(segments, normalized);
                texts = segments.Select(segment => segment.Text).ToArray();
            }
            else if (emittedOverlap.Count > 0)
            {
                DropWords(texts, normalized, 0, emittedOverlap[^1]);
            }
        }

        // Sentences emitted after the anchor that aren't visible as such any more were merged into (or rewritten
        // as) the text that follows it; don't emit their words again.
        if (anchor >= 0)
        {
            SkipMergedSentences(texts, normalized, anchor + 1, anchorCommitted + 1);
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
        int committedBefore = _committed.Count;
        for (int i = anchor + 1; i < finalCount; i++)
        {
            // A segment made only of punctuation (e.g. a stray "…") is not worth emitting. Nor is a distinctive
            // sentence identical to one just emitted: saying the very same thing again adds nothing, and it guards
            // against a repeated line if Live Captions ever rewrites its text in some way not handled above.
            if (normalized[i].Length == 0 || IsRecentRepeat(normalized[i], committedBefore))
            {
                continue;
            }

            Remember(texts[i], normalized[i]);
            added.Add(texts[i]);
        }

        int pendingStart = Math.Max(anchor + 1, finalCount);
        _pending = string.Join(" ", texts.Skip(pendingStart).Where(text => TextSimilarity.Normalize(text).Length > 0));
        return added;
    }

    /// <summary>Emits the remembered unfinished text as finished, e.g. after Live Captions went blank.</summary>
    private IReadOnlyList<string> CommitPending()
    {
        var added = new List<string>();
        foreach (var segment in SentenceSplitter.Split(_pending))
        {
            string normalized = TextSimilarity.Normalize(segment.Text);
            if (normalized.Length > 0)
            {
                Remember(segment.Text, normalized);
                added.Add(segment.Text);
            }
        }

        _pending = string.Empty;
        return added;
    }

    /// <summary>
    /// Removes from the start of <paramref name="texts"/> (from <paramref name="firstSegment"/>) the words of the
    /// sentences emitted after the anchor (from <paramref name="firstMerged"/>), if the text starts with them.
    /// </summary>
    private void SkipMergedSentences(string[] texts, string[] normalized, int firstSegment, int firstMerged)
    {
        var visible = MergedWords(texts, firstSegment, firstMerged);
        if (visible.Count > 0)
        {
            DropWords(texts, normalized, firstSegment, visible[^1]);
            return;
        }

        // Some of them may have been rewritten since, so they aren't visible as such any more: "Riverside, Texas seven."
        // became "Riverside TX 75231 in the main." (emitted after it), or the last one emitted was early and is now part
        // of the sentence being spoken. Skip the longest run of them that the text starts with, word for word and
        // ending where a finished sentence ends.
        for (int first = firstMerged; first < _committed.Count; first++)
        {
            for (int last = _committed.Count - 1; last >= first; last--)
            {
                var emitted = CommittedWords(first, last);
                var shown = VisibleWords(texts, firstSegment, emitted.Count);
                if (shown.Count == emitted.Count && EndsFinishedSentence(texts, shown[^1])
                    && shown.Select(word => word.Key).Zip(emitted).All(pair => pair.First == pair.Second || IsSameWord(pair.First, pair.Second)))
                {
                    DropWords(texts, normalized, firstSegment, shown[^1]);
                    return;
                }
            }
        }
    }

    /// <summary>The words of committed sentences <paramref name="first"/> to <paramref name="last"/>.</summary>
    private List<string> CommittedWords(int first, int last)
    {
        var words = new List<string>();
        for (int k = first; k <= last; k++)
        {
            words.AddRange(_committed[k].Normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        return words;
    }

    /// <summary>True if the word ends its sentence, and that sentence is finished (not the one still being spoken).</summary>
    private static bool EndsFinishedSentence(string[] texts, (int Segment, int Token, string Key) word)
    {
        string[] tokens = texts[word.Segment].Split(' ');
        for (int t = tokens.Length - 1; t > word.Token; t--)
        {
            if (TextSimilarity.Normalize(tokens[t]).Length > 0)
            {
                return false;
            }
        }

        return SentenceSplitter.Split(texts[word.Segment]) is [{ IsTerminated: true }];
    }

    /// <summary>How many words from <paramref name="firstSegment"/> on are the sentences emitted from <paramref name="firstMerged"/> on.</summary>
    private int MergedWordsAhead(string[] texts, int firstSegment, int firstMerged) =>
        MergedWords(texts, firstSegment, firstMerged).Count;

    private List<(int Segment, int Token, string Key)> MergedWords(string[] texts, int firstSegment, int firstMerged)
    {
        if (firstMerged >= _committed.Count || firstSegment >= texts.Length)
        {
            return new List<(int Segment, int Token, string Key)>();
        }

        var emittedWords = new List<string>();
        for (int k = firstMerged; k < _committed.Count; k++)
        {
            emittedWords.AddRange(_committed[k].Normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        var visible = VisibleWords(texts, firstSegment, emittedWords.Count);
        return visible.Count > 0 && WordsMatch(emittedWords, 0, visible.Select(word => word.Key).ToList(), 0, visible.Count)
            ? visible
            : new List<(int Segment, int Token, string Key)>();
    }

    /// <summary>
    /// With no anchor, the text may still start with the end of what was emitted (e.g. part of a sentence that was
    /// emitted in pieces before Live Captions merged them). Returns the words of the longest such overlap.
    /// </summary>
    private List<(int Segment, int Token, string Key)> EmittedOverlap(string[] texts, bool allowSingleWord)
    {
        var none = new List<(int Segment, int Token, string Key)>();
        if (_committed.Count == 0)
        {
            return none;
        }

        // Emitted words, with whether each was capitalised: the leftover of emitted text keeps its capitals, so
        // "Waiting about delivery ..." (a new sentence) isn't the "... card waiting about." emitted before it.
        var emittedWords = new List<string>();
        var emittedCapitalised = new List<bool>();
        for (int k = Math.Max(0, _committed.Count - AnchorDepth); k < _committed.Count; k++)
        {
            foreach (string token in _committed[k].Text.Split(' '))
            {
                string key = TextSimilarity.Normalize(token);
                if (key.Length > 0)
                {
                    emittedWords.Add(key);
                    emittedCapitalised.Add(StartsUpper(token));
                }
            }
        }

        var visible = VisibleWords(texts, 0, Math.Min(MaxOverlapWords, emittedWords.Count));
        var keys = visible.Select(word => word.Key).ToList();
        bool topCapitalised = visible.Count > 0 && StartsUpper(texts[visible[0].Segment].Split(' ')[visible[0].Token]);
        for (int length = keys.Count; length >= 2; length--)
        {
            int start = emittedWords.Count - length;
            if (emittedCapitalised[start] == topCapitalised && WordsMatch(emittedWords, start, keys, 0, length))
            {
                return visible.GetRange(0, length);
            }
        }

        // A single word is little to go on, but a lowercase one at the very top continues an earlier sentence
        // rather than starting a new one; if it's the last word emitted, it was emitted already.
        if (allowSingleWord && keys.Count > 0 && char.IsLower(texts[0].TrimStart('"', '\'', '(', '“', '‘').FirstOrDefault())
            && string.Equals(keys[0], emittedWords[^1], StringComparison.Ordinal))
        {
            return visible.GetRange(0, 1);
        }

        return none;
    }

    /// <summary>The first <paramref name="max"/> words from <paramref name="firstSegment"/> on, with their positions.</summary>
    private static List<(int Segment, int Token, string Key)> VisibleWords(string[] texts, int firstSegment, int max)
    {
        var words = new List<(int Segment, int Token, string Key)>();
        for (int s = firstSegment; s < texts.Length && words.Count < max; s++)
        {
            string[] tokens = texts[s].Split(' ');
            for (int t = 0; t < tokens.Length && words.Count < max; t++)
            {
                string key = TextSimilarity.Normalize(tokens[t]);
                if (key.Length > 0)
                {
                    words.Add((s, t, key));
                }
            }
        }

        return words;
    }

    /// <summary>
    /// True if <paramref name="count"/> words match from the given positions: the first exactly or as a small
    /// correction, and at most one in five of the rest different.
    /// </summary>
    private static bool WordsMatch(IList<string> a, int aStart, IList<string> b, int bStart, int count)
    {
        int allowedMismatches = count / 5;
        for (int w = 0; w < count; w++)
        {
            string x = a[aStart + w];
            string y = b[bStart + w];
            if (!string.Equals(x, y, StringComparison.Ordinal) && !IsSameWord(x, y) && (w == 0 || --allowedMismatches < 0))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Removes everything from <paramref name="firstSegment"/> up to and including the given word.</summary>
    private static void DropWords(string[] texts, string[] normalized, int firstSegment, (int Segment, int Token, string Key) lastDropped)
    {
        for (int s = firstSegment; s < lastDropped.Segment; s++)
        {
            texts[s] = string.Empty;
            normalized[s] = string.Empty;
        }

        string[] tokens = texts[lastDropped.Segment].Split(' ');
        texts[lastDropped.Segment] = string.Join(" ", tokens, lastDropped.Token + 1, tokens.Length - lastDropped.Token - 1);
        normalized[lastDropped.Segment] = TextSimilarity.Normalize(texts[lastDropped.Segment]);
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
    private bool TryRestoreScrolledOffStart(IReadOnlyList<CaptionSegment> segments, out string restored, out int matchedWords)
    {
        restored = string.Empty;
        matchedWords = 0;
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
                matchedWords = 1;
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
                matchedWords = probe.Length;
            }
        }

        // ...or where it overlaps just the end of it, when new words have been added since.
        for (int overlap = Math.Min(probe.Length - 1, pendingKeys.Length - 1); overlap >= MinProbeWords && start < 0; overlap--)
        {
            int j = pendingKeys.Length - overlap;
            if (StartsWithAt(pendingKeys, j, probe, overlap))
            {
                start = j;
                matchedWords = overlap;
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

    private static bool StartsUpper(string token)
    {
        foreach (char c in token)
        {
            if (char.IsLetter(c))
            {
                return char.IsUpper(c);
            }
        }

        return false;
    }

    /// <summary>True if the visible text starts with the first words of the pending sentence.</summary>
    private bool StartsLikePending(IReadOnlyList<CaptionSegment> segments)
    {
        if (_pending.Length == 0 || segments.Count == 0)
        {
            return false;
        }

        string[] pendingKeys = _pending.Split(' ').Select(TextSimilarity.Normalize).Where(word => word.Length > 0).ToArray();
        string[] probe = segments[0].Text.Split(' ')
            .Select(TextSimilarity.Normalize)
            .Where(word => word.Length > 0)
            .Take(ProbeWords)
            .ToArray();
        return probe.Length > 0 && StartsWithAt(pendingKeys, 0, probe, Math.Min(probe.Length, pendingKeys.Length));
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

    /// <summary>
    /// Finds the last visible segment that was already committed, and which committed sentence it is.
    /// Returns (-1, -1) if none is visible.
    /// </summary>
    private (int Segment, int Committed) FindAnchor(IReadOnlyList<CaptionSegment> segments, string[] normalized)
    {
        if (_committed.Count == 0 || normalized.Length == 0)
        {
            return (-1, -1);
        }

        _topIsCutOff = char.IsLower(segments[0].Text.TrimStart('"', '\'', '(', '“', '‘').FirstOrDefault());
        int oldest = Math.Max(0, _committed.Count - AnchorDepth);

        // First pass: the sentence before it must match too, so a repeated sentence ("Yes.") isn't mistaken
        // for an earlier one. A visible sentence may be several emitted ones that Live Captions has since
        // merged ("I called yesterday." + "and they said no."), in which case the one before those must match.
        for (int k = _committed.Count - 1; k >= oldest; k--)
        {
            for (int i = normalized.Length - 1; i > 0; i--)
            {
                if (!IsAnchorCandidate(segments, i))
                {
                    continue;
                }

                for (int merged = 1; merged <= MaxMergedSentences && k - merged >= 0; merged++)
                {
                    if (!MatchesText(normalized[i], Merged(k, merged), i))
                    {
                        continue;
                    }

                    if (MatchesBefore(normalized[i - 1], k - merged, i - 1))
                    {
                        return (i, k);
                    }

                    // The sentences emitted between the two may have been rewritten since, so they're no longer
                    // visible as such. Only for a distinctive sentence, which can't match here by coincidence.
                    if (normalized[i].Length >= MinDistinctiveLength)
                    {
                        for (int rewritten = 1; rewritten <= MaxRewrittenSentences && k - merged - rewritten >= 0; rewritten++)
                        {
                            if (MatchesBefore(normalized[i - 1], k - merged - rewritten, i - 1))
                            {
                                return (i, k);
                            }
                        }
                    }
                }
            }
        }

        // The oldest visible sentence has nothing before it to check, so it's only used when nothing later
        // matched. It may be several emitted sentences that Live Captions has since merged into one. A short
        // tail at the top ("phone.") can match several sentences: prefer the one after which the following text
        // carries on with words already emitted (a sentence merged since), else the latest.
        if (IsAnchorCandidate(segments, 0))
        {
            int best = -1;
            int bestEvidence = -1;
            string[] texts = segments.Select(segment => segment.Text).ToArray();
            for (int k = _committed.Count - 1; k >= oldest; k--)
            {
                if (MatchesOldest(normalized[0], k))
                {
                    int evidence = MergedWordsAhead(texts, 1, k + 1);
                    if (evidence > bestEvidence)
                    {
                        best = k;
                        bestEvidence = evidence;
                    }
                }
            }

            if (best >= 0)
            {
                return (0, best);
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
                    return (i, k);
                }
            }
        }

        return (-1, -1);
    }

    /// <summary>
    /// The segment before a candidate anchor matches committed sentence k, alone or merged with the ones before it
    /// (Live Captions may have joined them since: "Something while customer five." + "account really." became
    /// "Something while customer never account really.").
    /// </summary>
    private bool MatchesBefore(string candidate, int k, int segmentIndex)
    {
        if (Matches(candidate, k, segmentIndex))
        {
            return true;
        }

        for (int merged = 2; merged <= MaxMergedSentences && k - merged + 1 >= 0; merged++)
        {
            if (MatchesText(candidate, Merged(k, merged), segmentIndex))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The oldest visible segment matches committed sentence k, alone or merged with the ones before it.</summary>
    private bool MatchesOldest(string candidate, int k)
    {
        if (Matches(candidate, k, 0))
        {
            return true;
        }

        for (int merged = 2; merged <= MaxMergedSentences && k - merged + 1 >= 0; merged++)
        {
            if (MatchesText(candidate, Merged(k, merged), segmentIndex: 0))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Committed sentences k-count+1 to k, joined as one (normalized).</summary>
    private string Merged(int k, int count) => count == 1
        ? _committed[k].Normalized
        : string.Join(" ", _committed.Skip(k - count + 1).Take(count).Select(c => c.Normalized));

    // Only finished sentences are ever committed, so an unfinished one ("Maybe") can't be where we left off,
    // however much it looks like an earlier sentence ("Maybe.").
    private static bool IsAnchorCandidate(IReadOnlyList<CaptionSegment> segments, int index) => segments[index].IsTerminated;

    private bool Matches(string candidate, int committedIndex, int segmentIndex) =>
        MatchesText(candidate, _committed[committedIndex].Normalized, segmentIndex);

    private bool MatchesText(string candidate, string committed, int segmentIndex)
    {
        if (string.Equals(candidate, committed, StringComparison.Ordinal))
        {
            return true;
        }

        // Close enough to be the same sentence after a few corrections. Extra rules:
        // - numbers must be identical: "my number is 555 0134" and "... 555 0135" are different sentences
        //   (a customer correcting a number must not be dropped as a repeat);
        // - it must end on the same word and have about as many words, so a sentence that was extended
        //   ("...can I help." then "...can I help today.") or has another sentence merged on the end isn't
        //   taken for the shorter one.
        int shorter = Math.Min(candidate.Length, committed.Length);
        int longer = Math.Max(candidate.Length, committed.Length);
        if (longer > 0 && (double)shorter / longer >= _similarityThreshold
            && SameNumbers(candidate, committed)
            && SameLastWord(candidate, committed)
            && SimilarWordCount(candidate, committed)
            && TextSimilarity.Ratio(candidate, committed) >= _similarityThreshold)
        {
            return true;
        }

        // The first visible segment may be the tail of a sentence whose start scrolled off the top. The oldest
        // visible text has always been seen and committed on an earlier poll, so even a short tail counts.
        if (segmentIndex != 0 || candidate.Length == 0 || committed.Length <= candidate.Length)
        {
            return false;
        }

        if (committed[committed.Length - candidate.Length - 1] == ' ' && committed.EndsWith(candidate, StringComparison.Ordinal))
        {
            return true;
        }

        // Or the tail with a word or two rewritten since it was emitted ("... before seven through ... zero address
        // after card" is now "before phone through ... problem address after card"), if long enough to be sure and
        // visibly cut off (a whole sentence at the top that merely ends like an earlier one isn't its tail).
        string[] tail = candidate.Split(' ');
        string[] all = committed.Split(' ');
        return _topIsCutOff && tail.Length >= MinFuzzyTailWords && all.Length > tail.Length
            && WordsMatch(all, all.Length - tail.Length, tail, 0, tail.Length)
            && SameNumbers(candidate, string.Join(" ", all, all.Length - tail.Length, tail.Length));
    }

    private static bool SimilarWordCount(string a, string b)
    {
        int wordsA = a.Count(c => c == ' ') + 1;
        int wordsB = b.Count(c => c == ' ') + 1;
        return Math.Abs(wordsA - wordsB) <= Math.Max(1, Math.Max(wordsA, wordsB) / 10);
    }

    /// <summary>Same last word, allowing only a one-letter correction of a longer word ("service" / "services").</summary>
    private static bool SameLastWord(string a, string b)
    {
        string lastA = a[(a.LastIndexOf(' ') + 1)..];
        string lastB = b[(b.LastIndexOf(' ') + 1)..];
        return string.Equals(lastA, lastB, StringComparison.Ordinal)
            || (Math.Min(lastA.Length, lastB.Length) >= 5 && TextSimilarity.Levenshtein(lastA, lastB) == 1);
    }

    private static bool SameNumbers(string a, string b)
    {
        int i = 0;
        int j = 0;
        while (true)
        {
            while (i < a.Length && !char.IsDigit(a[i]))
            {
                i++;
            }

            while (j < b.Length && !char.IsDigit(b[j]))
            {
                j++;
            }

            if (i == a.Length || j == b.Length)
            {
                return i == a.Length && j == b.Length;
            }

            if (a[i++] != b[j++])
            {
                return false;
            }
        }
    }

    /// <summary>True for a distinctive sentence identical to one emitted just before this text.</summary>
    private bool IsRecentRepeat(string normalized, int committedBefore)
    {
        if (normalized.Length < MinDistinctiveLength)
        {
            return false;
        }

        // Only what was emitted before this text: two identical sentences arriving together were both said.
        for (int k = Math.Max(0, committedBefore - RepeatWindow); k < committedBefore; k++)
        {
            if (string.Equals(_committed[k].Normalized, normalized, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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
