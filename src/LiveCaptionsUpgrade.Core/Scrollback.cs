namespace LiveCaptionsUpgrade.Core;

/// <summary>A finished sentence in the scroll-back history.</summary>
/// <param name="Text">The sentence.</param>
/// <param name="Time">When it was finalised; used to expire it.</param>
public sealed record CaptionLine(string Text, DateTimeOffset Time);

/// <summary>Decides which scroll-back lines are old enough to discard, so memory use stays small.</summary>
public static class Scrollback
{
    /// <summary>Hard cap on remembered lines, whatever their age (a safety net for very long, fast speech).</summary>
    public const int MaxLines = 2000;

    /// <summary>
    /// Returns how many lines to remove from the start of <paramref name="lines"/> (which is oldest first):
    /// those older than <paramref name="retention"/>, plus any beyond <see cref="MaxLines"/>.
    /// </summary>
    public static int CountExpired(IReadOnlyList<CaptionLine> lines, DateTimeOffset now, TimeSpan retention)
    {
        int count = 0;
        while (count < lines.Count && (now - lines[count].Time > retention || lines.Count - count > MaxLines))
        {
            count++;
        }

        return count;
    }
}
