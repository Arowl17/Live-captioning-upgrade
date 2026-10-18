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
        Assert.True(batch.Add(new[] { Line("Card number?"), Line("Three, zero.") }, "Th", 0));
        Assert.False(batch.Add(new[] { Line("3032 5817.") }, string.Empty, 1));

        // One line it replaces was collected; the other was shown before.
        Assert.False(batch.Add(new[] { Line("3032 5817 4802 8929.") }, "Expiry", 2));

        var (lines, pending, replaced) = batch.Take();
        Assert.Equal(new[] { "Card number?", "3032 5817 4802 8929." }, lines.Select(l => l.Text));
        Assert.Equal("Expiry", pending);
        Assert.Equal(0, replaced);

        Assert.True(batch.Add(new[] { Line("Yes.") }, string.Empty, 3));
        (lines, _, replaced) = batch.Take();
        Assert.Equal(new[] { "Yes." }, lines.Select(l => l.Text));
        Assert.Equal(2, replaced);
    }
}
