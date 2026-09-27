using System.Text;

namespace LiveCaptionsUpgrade.Core;

/// <summary>A sentence (or trailing fragment) of caption text.</summary>
/// <param name="Text">The trimmed text of the segment.</param>
/// <param name="IsTerminated">True when the segment ends with sentence punctuation.</param>
public readonly record struct CaptionSegment(string Text, bool IsTerminated);

/// <summary>Splits the Live Captions text block into sentences.</summary>
public static class SentenceSplitter
{
    private const string Terminators = ".!?…。！？";

    // CJK full-width terminators are not followed by a space, so they always end a sentence.
    private const string CjkTerminators = "。！？";

    // Characters that may trail a terminator and still belong to the sentence, e.g. `"Stop!"` or `(yes.)`.
    private const string Closers = "\"'”’)]」』";

    // Titles are followed by a name, never the end of a sentence: "Mr. Smith".
    private static readonly HashSet<string> Titles = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "mt", "vs", "approx", "dept", "apt",
    };

    /// <summary>Collapses all runs of whitespace (including the line breaks Live Captions inserts) into single spaces.</summary>
    public static string NormalizeWhitespace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Splits text into sentences. Every segment except possibly the last is terminated;
    /// the last one is unterminated when the speaker is mid-sentence.
    /// </summary>
    public static IReadOnlyList<CaptionSegment> Split(string? text)
    {
        string s = NormalizeWhitespace(text);
        var segments = new List<CaptionSegment>();
        int start = 0;
        int i = 0;

        while (i < s.Length)
        {
            char c = s[i];
            if (Terminators.IndexOf(c) < 0)
            {
                i++;
                continue;
            }

            int end = i + 1;
            while (end < s.Length && (Terminators.IndexOf(s[end]) >= 0 || Closers.IndexOf(s[end]) >= 0))
            {
                end++;
            }

            // "3.5" or "example.com" are not sentence ends: a terminator must be followed by a space or the end of text.
            bool isBoundary = (end == s.Length || s[end] == ' ' || CjkTerminators.IndexOf(c) >= 0)
                && !(c == '.' && end == i + 1 && IsAbbreviation(s, start, i, end));
            if (isBoundary)
            {
                AddSegment(segments, s[start..end], isTerminated: true);
                start = end;
            }

            i = end;
        }

        if (start < s.Length)
        {
            AddSegment(segments, s[start..], isTerminated: false);
        }

        return segments;
    }

    /// <summary>True if the full stop at <paramref name="dot"/> belongs to an abbreviation rather than ending a sentence.</summary>
    private static bool IsAbbreviation(string s, int sentenceStart, int dot, int end)
    {
        int wordStart = s.LastIndexOf(' ', dot - 1, dot - sentenceStart) + 1;
        wordStart = Math.Max(wordStart, sentenceStart);
        string word = s[wordStart..dot].TrimStart('"', '\'', '(', '“', '‘');
        if (word.Length == 0)
        {
            return false;
        }

        // "Mr. Smith", "Dr. Jones".
        if (Titles.Contains(word))
        {
            return true;
        }

        // An initial: "John A. Smith". ("I." does end sentences: "So did I.")
        if (word.Length == 1 && char.IsUpper(word[0]) && word[0] != 'I')
        {
            return true;
        }

        // "3 p.m. today", "e.g. this": only a sentence end once a new sentence (capital letter) follows.
        // At the very end of the text it's too early to tell, so wait for the next word.
        if (word.Contains('.'))
        {
            int next = end;
            while (next < s.Length && s[next] == ' ')
            {
                next++;
            }

            return next == s.Length || !char.IsUpper(s[next]);
        }

        return false;
    }

    private static void AddSegment(List<CaptionSegment> segments, string raw, bool isTerminated)
    {
        string trimmed = raw.Trim();
        if (trimmed.Length > 0)
        {
            segments.Add(new CaptionSegment(trimmed, isTerminated));
        }
    }
}
