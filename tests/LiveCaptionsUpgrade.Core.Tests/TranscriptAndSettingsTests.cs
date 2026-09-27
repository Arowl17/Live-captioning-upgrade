using LiveCaptionsUpgrade.Core;

namespace LiveCaptionsUpgrade.Core.Tests;

public sealed class TranscriptAndSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lcu-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Transcript_is_created_lazily_and_lines_are_timestamped()
    {
        var at = new DateTimeOffset(2026, 3, 4, 9, 5, 7, TimeSpan.Zero);
        using (var writer = new TranscriptWriter(_dir))
        {
            Assert.Null(writer.FilePath);
            Assert.False(Directory.Exists(_dir));

            writer.Append("Hello world.", at);
            writer.Append("   ", at);
            writer.Append("Second line.", at.AddSeconds(2));

            Assert.Equal(Path.Combine(_dir, "transcript_2026-03-04_09-05-07.txt"), writer.FilePath);
        }

        var lines = File.ReadAllLines(Path.Combine(_dir, "transcript_2026-03-04_09-05-07.txt"));
        Assert.Equal(new[] { "[09:05:07] Hello world.", "[09:05:09] Second line." }, lines);
    }

    [Fact]
    public void Settings_round_trip()
    {
        string path = Path.Combine(_dir, "settings.json");
        var settings = new AppSettings { FontSize = 40, HistoryLines = 3, WindowLeft = 12.5, ClickThrough = true };

        settings.Save(path);
        var loaded = AppSettings.Load(path);

        Assert.Equal(40, loaded.FontSize);
        Assert.Equal(3, loaded.HistoryLines);
        Assert.Equal(12.5, loaded.WindowLeft);
        Assert.True(loaded.ClickThrough);
    }

    [Fact]
    public void Missing_or_corrupt_settings_fall_back_to_defaults()
    {
        Assert.Equal(26, AppSettings.Load(Path.Combine(_dir, "missing.json")).FontSize);

        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "bad.json");
        File.WriteAllText(path, "{ not json");
        Assert.Equal(26, AppSettings.Load(path).FontSize);
    }

    [Fact]
    public void Hand_edited_settings_allow_comments_and_are_clamped()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "edited.json");
        File.WriteAllText(path, "{\n  // bigger text\n  \"FontSize\": 500,\n  \"PollIntervalMs\": 1,\n  \"BackgroundOpacity\": 3,\n}");

        var loaded = AppSettings.Load(path);

        Assert.Equal(96, loaded.FontSize);
        Assert.Equal(50, loaded.PollIntervalMs);
        Assert.Equal(1, loaded.BackgroundOpacity);
    }
}
