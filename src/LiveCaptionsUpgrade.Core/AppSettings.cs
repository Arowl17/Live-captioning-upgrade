using System.Text.Json;

namespace LiveCaptionsUpgrade.Core;

/// <summary>User settings, stored as JSON in %APPDATA%\LiveCaptionsUpgrade\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>How often the Live Captions text is read, in milliseconds.</summary>
    public int PollIntervalMs { get; set; } = 150;

    /// <summary>How long the text must stay unchanged before a punctuated last sentence counts as finished.</summary>
    public int IdleFinalizeMs { get; set; } = 1200;

    /// <summary>Minimise the original Live Captions window and remove it from the taskbar while this app runs.</summary>
    public bool HideLiveCaptionsWindow { get; set; } = true;

    public bool SaveTranscript { get; set; } = true;

    /// <summary>Folder for transcript files. Empty means Documents\LiveCaptionsUpgrade\Transcripts.</summary>
    public string TranscriptFolder { get; set; } = string.Empty;

    public string FontFamily { get; set; } = "Segoe UI";

    public double FontSize { get; set; } = 26;

    /// <summary>Colour of the sentence currently being spoken.</summary>
    public string TextColor { get; set; } = "#FFFFFF";

    /// <summary>Colour of the finished sentences shown above it.</summary>
    public string HistoryTextColor { get; set; } = "#B8B8B8";

    public string BackgroundColor { get; set; } = "#000000";

    public double BackgroundOpacity { get; set; } = 0.7;

    /// <summary>How many finished sentences to keep on screen above the live one.</summary>
    public int HistoryLines { get; set; } = 2;

    /// <summary>When true, mouse clicks pass through the overlay to the window underneath.</summary>
    public bool ClickThrough { get; set; }

    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public double WindowWidth { get; set; } = 900;

    public double WindowHeight { get; set; } = 150;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiveCaptionsUpgrade", "settings.json");

    public string ResolveTranscriptFolder() => string.IsNullOrWhiteSpace(TranscriptFolder)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LiveCaptionsUpgrade", "Transcripts")
        : Environment.ExpandEnvironmentVariables(TranscriptFolder);

    /// <summary>Loads settings, falling back to defaults when the file is missing or unreadable.</summary>
    public static AppSettings Load(string path)
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            settings = new AppSettings();
        }

        settings.Normalize();
        return settings;
    }

    public void Save(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>Clamps hand-edited values into sensible ranges.</summary>
    public void Normalize()
    {
        PollIntervalMs = Math.Clamp(PollIntervalMs, 50, 2000);
        IdleFinalizeMs = Math.Clamp(IdleFinalizeMs, 300, 10000);
        FontSize = Math.Clamp(FontSize, 8, 96);
        BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0, 1);
        HistoryLines = Math.Clamp(HistoryLines, 0, 10);
        WindowWidth = Math.Max(WindowWidth, 200);
        WindowHeight = Math.Max(WindowHeight, 60);
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? "Segoe UI" : FontFamily;
        TextColor = string.IsNullOrWhiteSpace(TextColor) ? "#FFFFFF" : TextColor;
        HistoryTextColor = string.IsNullOrWhiteSpace(HistoryTextColor) ? "#B8B8B8" : HistoryTextColor;
        BackgroundColor = string.IsNullOrWhiteSpace(BackgroundColor) ? "#000000" : BackgroundColor;
        TranscriptFolder ??= string.Empty;
    }
}
