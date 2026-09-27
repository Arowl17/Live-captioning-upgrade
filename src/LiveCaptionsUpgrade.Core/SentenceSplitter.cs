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
            bool isBoundary = end == s.Length || s[end] == ' ' || CjkTerminators.IndexOf(c) >= 0;
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

    private static void AddSegment(List<CaptionSegment> segments, string raw, bool isTerminated)
    {
        string trimmed = raw.Trim();
        if (trimmed.Length > 0)
        {
            segments.Add(new CaptionSegment(trimmed, isTerminated));
        }
    }
}
