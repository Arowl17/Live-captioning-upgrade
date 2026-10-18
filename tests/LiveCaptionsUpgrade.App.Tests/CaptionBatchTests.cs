using System;
using System.Linq;
using LiveCaptionsUpgrade.Core;
using Xunit;

namespace LiveCaptionsUpgrade.AppTests;

public class CaptionBatchTests
{
    private static CaptionLine Line(string text) => new(text, DateTimeOffset.Now);

    [Fact]
    public void Changes_are_collected_until_taken_and_replacements_apply_to_them_first()
    {
        var batch = new global::LiveCaptionsUpgrade.CaptionBatch();
        Assert.True(batch.Add(new[] { Line("Card number?"), Line("Three.") }, "Ze", 0));
        Assert.False(batch.Add(new[] { Line("30.") }, string.Empty, 1));

        var (lines, pending, replaced) = batch.Take();
        Assert.Equal(new[] { "Card number?", "30." }, lines.Select(l => l.Text));
        Assert.Equal(string.Empty, pending);
        Assert.Equal(0, replaced);

        // Of the lines the full number replaces, one is still waiting to be shown; the other was shown already.
        Assert.True(batch.Add(new[] { Line("Two.") }, "Fi", 0));
        Assert.False(batch.Add(new[] { Line("3032 5817 4802 8929.") }, "Expiry", 2));

        (lines, pending, replaced) = batch.Take();
        Assert.Equal(new[] { "3032 5817 4802 8929." }, lines.Select(l => l.Text));
        Assert.Equal("Expiry", pending);
        Assert.Equal(1, replaced);
    }
}
