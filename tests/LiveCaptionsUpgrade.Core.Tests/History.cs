namespace LiveCaptionsUpgrade.Core.Tests;

internal static class History
{
    /// <summary>Updates a history of emitted sentences the way the overlay does, replacing any rewritten ones.</summary>
    public static void Apply(this List<string> history, CaptionUpdate update)
    {
        Assert.InRange(update.Replaced, 0, history.Count);
        history.RemoveRange(history.Count - update.Replaced, update.Replaced);
        history.AddRange(update.NewSentences);
    }
}
