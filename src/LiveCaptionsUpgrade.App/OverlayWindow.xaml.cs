using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LiveCaptionsUpgrade.Core;

namespace LiveCaptionsUpgrade;

/// <summary>The always-on-top caption bar.</summary>
public partial class OverlayWindow : Window
{
    private const string ListeningHint = "Listening… captions will appear here. Right-click for options.";

    private readonly AppSettings _settings;
    private readonly Queue<string> _history = new();
    private string? _status;

    public OverlayWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        ApplySettings();
        ShowStatus(null);
    }

    /// <summary>Raised when the user right-clicks the overlay.</summary>
    public event EventHandler? MenuRequested;

    public void ShowUpdate(CaptionUpdate update)
    {
        foreach (string sentence in update.NewSentences)
        {
            _history.Enqueue(sentence);
        }

        while (_history.Count > _settings.HistoryLines)
        {
            _history.Dequeue();
        }

        HistoryText.Text = string.Join(Environment.NewLine, _history);
        HistoryText.Visibility = _history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LiveText.Text = update.Pending;
        RefreshStatus();
    }

    public void ShowStatus(string? status)
    {
        _status = status;
        RefreshStatus();
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

    private void ApplySettings()
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

        FontFamily = new FontFamily(_settings.FontFamily);
        FontSize = _settings.FontSize;
        LiveText.Foreground = new SolidColorBrush(ParseColor(_settings.TextColor, Colors.White));
        HistoryText.Foreground = new SolidColorBrush(ParseColor(_settings.HistoryTextColor, Colors.LightGray));
        StatusText.Foreground = LiveText.Foreground;
        Panel.Background = new SolidColorBrush(ParseColor(_settings.BackgroundColor, Colors.Black))
        {
            Opacity = _settings.BackgroundOpacity,
        };
    }

    private void RefreshStatus()
    {
        bool hasCaptions = _history.Count > 0 || LiveText.Text.Length > 0;
        string? message = _status ?? (hasCaptions ? null : ListeningHint);
        StatusText.Text = message ?? string.Empty;
        StatusText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

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
