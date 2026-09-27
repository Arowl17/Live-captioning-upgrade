using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LiveCaptionsUpgrade.Core;
using WinForms = System.Windows.Forms;

namespace LiveCaptionsUpgrade;

/// <summary>Edits a copy of the settings; nothing changes until OK or Apply.</summary>
public partial class SettingsWindow : Window
{
    private const string DefaultHotkeyHint = "Click the box, then press the keys you want (for example Ctrl+Alt+H).";

    private readonly AppSettings _settings;
    private readonly string _defaultTranscriptFolder = new AppSettings().ResolveTranscriptFolder();
    private string _textColor;
    private string _historyColor;
    private string _backgroundColor;
    private string _hotkey;

    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();
        _settings = current.Clone();
        _textColor = _settings.TextColor;
        _historyColor = _settings.HistoryTextColor;
        _backgroundColor = _settings.BackgroundColor;
        _hotkey = _settings.ToggleHotkey;
        LoadValues();
    }

    /// <summary>Raised with the edited settings when OK or Apply is pressed.</summary>
    public event Action<AppSettings>? Applied;

    private void LoadValues()
    {
        var fonts = Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .Distinct()
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (!fonts.Contains(_settings.FontFamily))
        {
            fonts.Insert(0, _settings.FontFamily);
        }

        FontBox.ItemsSource = fonts;
        FontBox.SelectedItem = _settings.FontFamily;
        FontSizeSlider.Value = _settings.FontSize;
        OpacitySlider.Value = Math.Round(_settings.BackgroundOpacity * 100);
        UpdateSwatches();

        AlwaysOnTopBox.IsChecked = _settings.AlwaysOnTop;
        ClickThroughBox.IsChecked = _settings.ClickThrough;
        ShowHotkey(DefaultHotkeyHint);

        ScrollbackSlider.Value = _settings.ScrollbackMinutes;

        HideLiveCaptionsBox.IsChecked = _settings.HideLiveCaptionsWindow;
        HideMethodBox.SelectedIndex = (int)_settings.HideMethod;

        SaveTranscriptBox.IsChecked = _settings.SaveTranscript;
        TranscriptFolderBox.Text = _settings.ResolveTranscriptFolder();
    }

    private AppSettings Collect()
    {
        var result = _settings.Clone();
        result.FontFamily = FontBox.SelectedItem as string ?? result.FontFamily;
        result.FontSize = Math.Round(FontSizeSlider.Value);
        result.TextColor = _textColor;
        result.HistoryTextColor = _historyColor;
        result.BackgroundColor = _backgroundColor;
        result.BackgroundOpacity = Math.Round(OpacitySlider.Value) / 100;

        result.AlwaysOnTop = AlwaysOnTopBox.IsChecked == true;
        result.ClickThrough = ClickThroughBox.IsChecked == true;
        result.ToggleHotkey = _hotkey;

        result.ScrollbackMinutes = (int)Math.Round(ScrollbackSlider.Value);

        result.HideLiveCaptionsWindow = HideLiveCaptionsBox.IsChecked == true;
        result.HideMethod = HideMethodBox.SelectedIndex == (int)LiveCaptionsHideMethod.Minimize
            ? LiveCaptionsHideMethod.Minimize
            : LiveCaptionsHideMethod.Invisible;

        result.SaveTranscript = SaveTranscriptBox.IsChecked == true;
        string folder = TranscriptFolderBox.Text.Trim();
        result.TranscriptFolder = folder.Length == 0 || string.Equals(folder, _defaultTranscriptFolder, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : folder;

        result.Normalize();
        return result;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Applied?.Invoke(Collect());
        Close();
    }

    private void OnApply(object sender, RoutedEventArgs e) => Applied?.Invoke(Collect());

    // IsCancel only closes windows opened with ShowDialog; this one is modeless so the tray stays usable.
    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnHotkeyPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Alt combinations arrive as Key.System with the real key in SystemKey.
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        var held = Keyboard.Modifiers;

        // Let Tab move focus and Escape close the window as usual.
        if (held == ModifierKeys.None && key is Key.Tab or Key.Escape)
        {
            return;
        }

        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            // Still waiting for the main key.
            return;
        }

        if (held == ModifierKeys.None && key is Key.Back or Key.Delete)
        {
            _hotkey = string.Empty;
            ShowHotkey(DefaultHotkeyHint);
            return;
        }

        var modifiers = HotkeyModifiers.None;
        if (held.HasFlag(ModifierKeys.Control))
        {
            modifiers |= HotkeyModifiers.Ctrl;
        }

        if (held.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (held.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (held.HasFlag(ModifierKeys.Windows))
        {
            modifiers |= HotkeyModifiers.Win;
        }

        var hotkey = new Hotkey(modifiers, key.ToString());
        if (!hotkey.IsValid)
        {
            ShowHotkey("Hold Ctrl, Alt or Shift together with the key (function keys F1–F24 also work alone).");
            return;
        }

        _hotkey = hotkey.ToString();
        ShowHotkey(DefaultHotkeyHint);
    }

    private void OnClearHotkey(object sender, RoutedEventArgs e)
    {
        _hotkey = string.Empty;
        ShowHotkey(DefaultHotkeyHint);
    }

    private void ShowHotkey(string hint)
    {
        HotkeyBox.Text = _hotkey.Length > 0 ? _hotkey : "None";
        HotkeyHint.Text = hint;
    }

    private void OnPickTextColor(object sender, RoutedEventArgs e) => PickColor(ref _textColor);

    private void OnPickHistoryColor(object sender, RoutedEventArgs e) => PickColor(ref _historyColor);

    private void OnPickBackgroundColor(object sender, RoutedEventArgs e) => PickColor(ref _backgroundColor);

    private void PickColor(ref string color)
    {
        var current = ParseColor(color);
        using var dialog = new WinForms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
        };
        if (dialog.ShowDialog(new DialogOwner(this)) != WinForms.DialogResult.OK)
        {
            return;
        }

        color = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        UpdateSwatches();
    }

    private void UpdateSwatches()
    {
        TextColorSwatch.Background = new SolidColorBrush(ParseColor(_textColor));
        HistoryColorSwatch.Background = new SolidColorBrush(ParseColor(_historyColor));
        BackgroundColorSwatch.Background = new SolidColorBrush(ParseColor(_backgroundColor));
    }

    private void OnBrowseTranscriptFolder(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Choose where transcripts are saved",
            UseDescriptionForTitle = true,
            SelectedPath = TranscriptFolderBox.Text,
        };
        if (dialog.ShowDialog(new DialogOwner(this)) == WinForms.DialogResult.OK)
        {
            TranscriptFolderBox.Text = dialog.SelectedPath;
        }
    }

    private void OnOpenTranscriptFolder(object sender, RoutedEventArgs e)
    {
        string folder = Environment.ExpandEnvironmentVariables(TranscriptFolderBox.Text.Trim());
        if (folder.Length == 0)
        {
            folder = _defaultTranscriptFolder;
        }

        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            MessageBox.Show(this, "Can't open that folder:\n\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static Color ParseColor(string value)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(value);
        }
        catch (FormatException)
        {
            return Colors.Black;
        }
    }

    /// <summary>Lets WinForms dialogs open in front of this WPF window.</summary>
    private sealed class DialogOwner : WinForms.IWin32Window
    {
        public DialogOwner(Window window) => Handle = new WindowInteropHelper(window).Handle;

        public IntPtr Handle { get; }
    }
}
