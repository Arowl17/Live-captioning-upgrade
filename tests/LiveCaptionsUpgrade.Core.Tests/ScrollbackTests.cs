using LiveCaptionsUpgrade.Core;

namespace LiveCaptionsUpgrade.Core.Tests;

public class ScrollbackTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);

    [Fact]
    public void Lines_older_than_the_retention_expire()
    {
        var lines = new List<CaptionLine>
        {
            new("very old", Now.AddMinutes(-30)),
            new("old", Now.AddMinutes(-10).AddSeconds(-1)),
            new("recent", Now.AddMinutes(-9)),
            new("now", Now),
        };

        Assert.Equal(2, Scrollback.CountExpired(lines, Now, TenMinutes));
    }

    [Fact]
    public void Nothing_expires_within_the_retention()
    {
        var lines = new List<CaptionLine> { new("a", Now.AddMinutes(-10)), new("b", Now) };

        Assert.Equal(0, Scrollback.CountExpired(lines, Now, TenMinutes));
        Assert.Equal(0, Scrollback.CountExpired(new List<CaptionLine>(), Now, TenMinutes));
    }

    [Fact]
    public void Line_count_is_capped_even_for_recent_lines()
    {
        var lines = Enumerable.Range(0, Scrollback.MaxLines + 5).Select(i => new CaptionLine($"line {i}", Now)).ToList();

        Assert.Equal(5, Scrollback.CountExpired(lines, Now, TenMinutes));
    }
}
