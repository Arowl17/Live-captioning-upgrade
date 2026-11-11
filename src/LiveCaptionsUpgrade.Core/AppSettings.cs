using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveCaptionsUpgrade.Core;

/// <summary>How the original Live Captions window is kept off screen.</summary>
public enum LiveCaptionsHideMethod
{
    /// <summary>
    /// Keep it open but fully transparent, click-through and parked off screen. Windows still has to draw a
    /// transparent window, which can slow games down; only for when Live Captions stops captioning while minimised.
    /// </summary>
    Invisible,

    /// <summary>Minimise it (the default): Windows doesn't draw a minimised window at all, and it keeps captioning.</summary>
    Minimize,
}

/// <summary>Whether this computer shares captions with another one over the local network.</summary>
public enum CaptionSharingMode
{
    /// <summary>Captions come from Live Captions on this computer and stay here.</summary>
    Off,

    /// <summary>Captions come from Live Captions on this computer and are also sent to the paired computer.</summary>
    Send,

    /// <summary>Captions come from the paired computer (for a computer without Live Captions, e.g. Windows 10).</summary>
    Receive,
}

/// <summary>User settings, stored as JSON in %APPDATA%\LiveCaptionsUpgrade\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Settings saved by a version from before <see cref="SettingsVersion"/> existed count as version 0.</summary>
    public const int CurrentVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>How often the Live Captions text is read, in milliseconds.</summary>
    public int PollIntervalMs { get; set; } = 150;

    /// <summary>How long the text must stay unchanged before a punctuated last sentence counts as finished.</summary>
    public int IdleFinalizeMs { get; set; } = 1200;

    /// <summary>Hide the original Live Captions window while this app runs, so only this app's captions are on screen.</summary>
    public bool HideLiveCaptionsWindow { get; set; } = true;

    public LiveCaptionsHideMethod HideMethod { get; set; } = LiveCaptionsHideMethod.Minimize;

    /// <summary>Which version of the settings this is, so that a newer app can bring older ones up to date.</summary>
    public int SettingsVersion { get; set; }

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

    /// <summary>How many minutes of finished sentences can be scrolled back through. Older ones are discarded.</summary>
    public int ScrollbackMinutes { get; set; } = 10;

    /// <summary>When true, mouse clicks pass through the overlay to the window underneath.</summary>
    public bool ClickThrough { get; set; }

    /// <summary>Keep the overlay above all other windows.</summary>
    public bool AlwaysOnTop { get; set; } = true;

    /// <summary>Global shortcut that hides and shows the overlay, e.g. "Ctrl+Alt+H". Empty disables it.</summary>
    public string ToggleHotkey { get; set; } = "Ctrl+Alt+H";

    /// <summary>Send captions to, or show captions from, the paired computer.</summary>
    public CaptionSharingMode CaptionSharing { get; set; } = CaptionSharingMode.Off;

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
        settings.Upgrade();
        return settings;
    }

    public void Save(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write to a temporary file and swap it in, so a crash mid-write can't leave a half-written settings file.
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Brings settings saved by an older version up to date.</summary>
    private void Upgrade()
    {
        // Before version 2, Live Captions was hidden as a transparent window by default, which turned out to slow
        // the computer down. Minimising it is the default now; move everyone over (it can be chosen again).
        if (SettingsVersion < 2)
        {
            HideMethod = LiveCaptionsHideMethod.Minimize;
        }

        SettingsVersion = CurrentVersion;
    }

    /// <summary>Clamps hand-edited values into sensible ranges.</summary>
    public void Normalize()
    {
        PollIntervalMs = Math.Clamp(PollIntervalMs, 50, 2000);
        IdleFinalizeMs = Math.Clamp(IdleFinalizeMs, 300, 10000);
        FontSize = Math.Clamp(FontSize, 8, 96);
        BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0, 1);
        ScrollbackMinutes = Math.Clamp(ScrollbackMinutes, 1, 60);
        WindowWidth = Math.Max(WindowWidth, 200);
        WindowHeight = Math.Max(WindowHeight, 60);
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? "Segoe UI" : FontFamily;
        TextColor = string.IsNullOrWhiteSpace(TextColor) ? "#FFFFFF" : TextColor;
        HistoryTextColor = string.IsNullOrWhiteSpace(HistoryTextColor) ? "#B8B8B8" : HistoryTextColor;
        BackgroundColor = string.IsNullOrWhiteSpace(BackgroundColor) ? "#000000" : BackgroundColor;
        TranscriptFolder ??= string.Empty;
        HideMethod = Enum.IsDefined(HideMethod) ? HideMethod : LiveCaptionsHideMethod.Minimize;
        CaptionSharing = Enum.IsDefined(CaptionSharing) ? CaptionSharing : CaptionSharingMode.Off;
        ToggleHotkey = Hotkey.TryParse(ToggleHotkey, out var hotkey) ? hotkey.ToString() : string.Empty;
    }

    /// <summary>Returns an independent copy, e.g. for a settings dialog to edit until OK is pressed.</summary>
    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
