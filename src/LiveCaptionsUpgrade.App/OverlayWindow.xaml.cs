using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LiveCaptionsUpgrade.Core;

namespace LiveCaptionsUpgrade;

/// <summary>The caption bar: finished sentences you can scroll back through, with the live sentence at the bottom.</summary>
public partial class OverlayWindow : Window
{
    private const string ListeningHint = "Listening… captions will appear here. Scroll to see earlier text, right-click for options.";

    private readonly ObservableCollection<CaptionLine> _lines = new();
    private readonly DispatcherTimer _pruneTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private AppSettings _settings;
    private string? _status;

    // True while showing the newest text. Scrolling up pauses this so the text you're reading doesn't move.
    private bool _followLive = true;

    public OverlayWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        HistoryList.ItemsSource = _lines;
        ApplyPlacement();
        ApplyAppearance();
        ShowStatus(null);

        _pruneTimer.Tick += (_, _) => PruneOldLines();
        _pruneTimer.Start();
    }

    /// <summary>Raised when the user right-clicks the overlay.</summary>
    public event EventHandler? MenuRequested;

    public void ShowUpdate(CaptionUpdate update)
    {
        var now = DateTimeOffset.Now;
        foreach (string sentence in update.NewSentences)
        {
            _lines.Add(new CaptionLine(sentence, now));
        }

        LiveText.Text = update.Pending;
        PruneOldLines();
        RefreshStatus();
    }

    public void ShowStatus(string? status)
    {
        _status = status;
        RefreshStatus();
    }

    /// <summary>Applies changed settings (from the settings window). Position and size are kept as they are.</summary>
    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        StoreBounds();
        ApplyAppearance();
        SetClickThrough(settings.ClickThrough);
        PruneOldLines();
    }

    public void SetClickThrough(bool enabled)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            exStyle = enabled ? exStyle | NativeMethods.WS_EX_TRANSPARENT : exStyle & ~NativeMethods.WS_EX_TRANSPARENT;
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
        }

        ResizeMode = enabled ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
        _settings.ClickThrough = enabled;
    }

    /// <summary>Copies the current position and size into the settings so they are remembered.</summary>
    public void StoreBounds()
    {
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        SetClickThrough(_settings.ClickThrough);
    }

    protected override void OnClosed(EventArgs e)
    {
        _pruneTimer.Stop();
        base.OnClosed(e);
    }

    private void ApplyPlacement()
    {
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;

        var screen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        if (_settings.WindowLeft is double left && _settings.WindowTop is double top
            && screen.IntersectsWith(new Rect(left, top, Width, Height)))
        {
            Left = left;
            Top = top;
        }
        else
        {
            // Default: centred near the bottom of the primary screen, where subtitles usually are.
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Left + ((workArea.Width - Width) / 2);
            Top = workArea.Bottom - Height - 40;
        }
    }

    private void ApplyAppearance()
    {
        Topmost = _settings.AlwaysOnTop;
        FontFamily = new FontFamily(_settings.FontFamily);
        FontSize = _settings.FontSize;
        LiveText.Foreground = new SolidColorBrush(ParseColor(_settings.TextColor, Colors.White));
        HistoryList.Foreground = new SolidColorBrush(ParseColor(_settings.HistoryTextColor, Colors.LightGray));
        StatusText.Foreground = LiveText.Foreground;
        Panel.Background = new SolidColorBrush(ParseColor(_settings.BackgroundColor, Colors.Black))
        {
            Opacity = _settings.BackgroundOpacity,
        };
    }

    /// <summary>Drops sentences older than the scroll-back limit so memory use stays small.</summary>
    private void PruneOldLines()
    {
        // Never pull text out from under someone who has scrolled back to read it; catch up once they return to live.
        if (!_followLive)
        {
            return;
        }

        int expired = Scrollback.CountExpired(_lines, DateTimeOffset.Now, TimeSpan.FromMinutes(_settings.ScrollbackMinutes));
        for (int i = 0; i < expired; i++)
        {
            _lines.RemoveAt(0);
        }
    }

    private void RefreshStatus()
    {
        bool hasCaptions = _lines.Count > 0 || LiveText.Text.Length > 0;
        string? message = _status ?? (hasCaptions ? null : ListeningHint);
        StatusText.Text = message ?? string.Empty;
        StatusText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
        {
            // The user scrolled: follow live text only while they're at the bottom.
            bool wasFollowing = _followLive;
            _followLive = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 2;
            BackToLiveButton.Visibility = _followLive ? Visibility.Collapsed : Visibility.Visible;
            if (_followLive && !wasFollowing)
            {
                PruneOldLines();
            }
        }
        else if (_followLive)
        {
            // Content or window size changed: keep the newest text in view.
            Scroller.ScrollToEnd();
        }
    }

    private void OnBackToLiveClick(object sender, RoutedEventArgs e) => Scroller.ScrollToEnd();

    private static Color ParseColor(string value, Color fallback)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(value);
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        MenuRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}
