using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using LiveCaptionsUpgrade.Core;

namespace LiveCaptionsUpgrade;

/// <summary>The caption bar: finished sentences you can scroll back through, with the live sentence at the bottom.</summary>
public partial class OverlayWindow : Window
{
    private const string ListeningHint =
        "Listening… captions will appear here. Scroll for earlier text, select text to copy, right-click for options.";

    // How close to an edge (in DIPs) the mouse must be to resize rather than move; corners get a bigger target.
    private const double ResizeEdge = 8;
    private const double ResizeCorner = 16;

    private static readonly Brush HoverOutline = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));

    // Finished sentences (oldest first) and their paragraphs in the caption document, kept in step.
    private readonly List<CaptionLine> _lines = new();
    private readonly List<Paragraph> _paragraphs = new();

    // The sentence being spoken: the last paragraph, present only while there is such text.
    private readonly Run _liveRun = new();
    private readonly Paragraph _liveParagraph;
    private bool _liveShown;

    private readonly DispatcherTimer _pruneTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _noteTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private AppSettings _settings;
    private string? _status;
    private string? _note;
    private Brush _historyBrush = Brushes.LightGray;
    private Brush _liveBrush = Brushes.White;

    // True while showing the newest text. Scrolling up (or selecting text) pauses this so the text you're
    // reading doesn't move.
    private bool _followLive = true;

    // Following was paused only because text was being selected: go back to live once that's done.
    private bool _resumeAfterSelection;

    // Moving/resizing in progress: which edges follow the mouse (none = moving), and where it started, in
    // device pixels. The new position is applied at most once per frame, when the window is redrawn anyway.
    private bool _dragging;
    private Edges _dragEdges;
    private NativeMethods.POINT _dragStartCursor;
    private NativeMethods.RECT _dragStartRect;
    private NativeMethods.RECT? _pendingRect;

    public OverlayWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _liveParagraph = new Paragraph(_liveRun) { Margin = new Thickness(0) };
        ApplyPlacement();
        ApplyAppearance();
        ShowStatus(null);

        // Copy (Ctrl+C) goes through our own clipboard code: WPF's throws if another app is holding the
        // clipboard, which would otherwise close this app.
        CommandManager.AddPreviewExecutedHandler(Captions, OnPreviewCommandExecuted);

        _pruneTimer.Tick += (_, _) => PruneOldLines();
        _pruneTimer.Start();
        _noteTimer.Tick += (_, _) =>
        {
            _noteTimer.Stop();
            _note = null;
            RefreshStatus();
        };

        // When shown again (e.g. with the shortcut), start at the newest text; earlier text is a scroll away.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                GoLive();
            }
        };
    }

    [Flags]
    private enum Edges
    {
        None = 0,
        Left = 1,
        Top = 2,
        Right = 4,
        Bottom = 8,
    }

    /// <summary>Raised when the user right-clicks the overlay.</summary>
    public event EventHandler? MenuRequested;

    /// <summary>Raised when the user has finished moving or resizing the overlay.</summary>
    public event EventHandler? BoundsChanged;

    public bool HasSelection => !Captions.Selection.IsEmpty;

    // Selecting (or having selected) text holds the view still, so new captions don't scroll it away.
    private bool IsHoldingView => !Captions.Selection.IsEmpty || Captions.IsMouseCaptureWithin;

    private bool IsAtBottom => Captions.VerticalOffset >= Captions.ExtentHeight - Captions.ViewportHeight - 2;

    public void ShowUpdate(CaptionUpdate update)
    {
        var now = DateTimeOffset.Now;
        var lines = new List<CaptionLine>(update.NewSentences.Count);
        foreach (string sentence in update.NewSentences)
        {
            lines.Add(new CaptionLine(sentence, now));
        }

        ShowLines(lines, update.Pending, update.Replaced);
    }

    /// <summary>
    /// Adds finished sentences (with when they were finished) and shows the sentence being spoken. The first
    /// <paramref name="replaced"/> lines shown last are taken away first: Live Captions rewrote them into the new ones.
    /// </summary>
    public void ShowLines(IReadOnlyList<CaptionLine> lines, string pending, int replaced = 0)
    {
        for (int i = Math.Min(replaced, _lines.Count); i > 0; i--)
        {
            Captions.Document.Blocks.Remove(_paragraphs[^1]);
            _lines.RemoveAt(_lines.Count - 1);
            _paragraphs.RemoveAt(_paragraphs.Count - 1);
        }

        // Lines from another computer can be up to an hour old (it catches this one up after connecting): don't
        // lay out ones that are already past the scroll-back limit only to delete them again.
        var cutoff = DateTimeOffset.Now - TimeSpan.FromMinutes(_settings.ScrollbackMinutes);
        foreach (var line in lines)
        {
            if (line.Time < cutoff)
            {
                continue;
            }

            var paragraph = new Paragraph(new Run(line.Text)) { Margin = new Thickness(0), Foreground = _historyBrush };
            if (_liveShown)
            {
                Captions.Document.Blocks.InsertBefore(_liveParagraph, paragraph);
            }
            else
            {
                Captions.Document.Blocks.Add(paragraph);
            }

            _lines.Add(line);
            _paragraphs.Add(paragraph);
        }

        SetLiveText(pending);
        PruneOldLines();
        RefreshStatus();
    }

    public void ShowStatus(string? status)
    {
        _status = status;
        RefreshStatus();
    }

    /// <summary>
    /// Copies the selected caption text to the clipboard, then lets go of the selection so the captions carry on
    /// following the conversation (if they were before the selection paused them).
    /// </summary>
    public void CopySelection()
    {
        string text = Captions.Selection.Text.TrimEnd('\r', '\n');
        if (text.Length == 0)
        {
            return;
        }

        // The clipboard can be briefly held by another app (clipboard managers, remote desktop): retry a little.
        bool copied = false;
        for (int attempt = 0; attempt < 5 && !copied; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                copied = true;
            }
            catch (ExternalException)
            {
                Thread.Sleep(50);
            }
        }

        ShowNote(copied ? "Copied" : "Couldn't copy: another app is using the clipboard. Try again.");
        if (copied)
        {
            var end = Captions.Selection.End;
            Captions.Selection.Select(end, end);
        }
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

        _settings.ClickThrough = enabled;
    }

    /// <summary>Copies the current position and size into the settings so they are remembered.</summary>
    public void StoreBounds()
    {
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        _settings.WindowWidth = ActualWidth > 0 ? ActualWidth : Width;
        _settings.WindowHeight = ActualHeight > 0 ? ActualHeight : Height;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        SetClickThrough(_settings.ClickThrough);
    }

    protected override void OnClosed(EventArgs e)
    {
        _pruneTimer.Stop();
        _noteTimer.Stop();
        base.OnClosed(e);
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

    private static Cursor? CursorFor(Edges edges) => edges switch
    {
        Edges.Left | Edges.Top or Edges.Right | Edges.Bottom => Cursors.SizeNWSE,
        Edges.Right | Edges.Top or Edges.Left | Edges.Bottom => Cursors.SizeNESW,
        Edges.Left or Edges.Right => Cursors.SizeWE,
        Edges.Top or Edges.Bottom => Cursors.SizeNS,
        _ => null,
    };

    private void ApplyPlacement()
    {
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;

        var screen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        // Use the saved position only if a good part of the window is still on a screen (monitors change).
        var visible = Rect.Intersect(screen, new Rect(_settings.WindowLeft ?? 0, _settings.WindowTop ?? 0, Width, Height));
        if (_settings.WindowLeft is double left && _settings.WindowTop is double top
            && !visible.IsEmpty && visible.Width >= Math.Min(100, Width) && visible.Height >= Math.Min(40, Height))
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
        Captions.Document.FontFamily = FontFamily;
        Captions.Document.FontSize = FontSize;

        _liveBrush = new SolidColorBrush(ParseColor(_settings.TextColor, Colors.White));
        _historyBrush = new SolidColorBrush(ParseColor(_settings.HistoryTextColor, Colors.LightGray));
        _liveParagraph.Foreground = _liveBrush;
        foreach (var paragraph in _paragraphs)
        {
            paragraph.Foreground = _historyBrush;
        }

        StatusText.Foreground = _liveBrush;
        foreach (var dot in Grip.Children)
        {
            ((Ellipse)dot).Fill = _liveBrush;
        }

        // Fully transparent pixels don't receive the mouse, so keep the background a hair above zero:
        // at 0% the window could otherwise only be grabbed by its text.
        Panel.Background = new SolidColorBrush(ParseColor(_settings.BackgroundColor, Colors.Black))
        {
            Opacity = Math.Max(_settings.BackgroundOpacity, 0.01),
        };
    }

    private void SetLiveText(string text)
    {
        _liveRun.Text = text;

        // No empty line at the bottom when nothing is being said.
        if (text.Length > 0 && !_liveShown)
        {
            Captions.Document.Blocks.Add(_liveParagraph);
            _liveShown = true;
        }
        else if (text.Length == 0 && _liveShown)
        {
            Captions.Document.Blocks.Remove(_liveParagraph);
            _liveShown = false;
        }
    }

    /// <summary>Drops sentences older than the scroll-back limit so memory use stays small.</summary>
    private void PruneOldLines()
    {
        // Don't pull text out from under someone who has scrolled back to read it or is selecting it (catch up
        // once they return to live), unless they stay there so long that the hard limit on lines is reached.
        if (!_followLive && _lines.Count <= Scrollback.MaxLines)
        {
            return;
        }

        int expired = Scrollback.CountExpired(_lines, DateTimeOffset.Now, TimeSpan.FromMinutes(_settings.ScrollbackMinutes));
        for (int i = 0; i < expired; i++)
        {
            Captions.Document.Blocks.Remove(_paragraphs[i]);
        }

        _lines.RemoveRange(0, expired);
        _paragraphs.RemoveRange(0, expired);
    }

    /// <summary>Shows a short-lived message under the captions (e.g. "Copied").</summary>
    private void ShowNote(string note)
    {
        _note = note;
        _noteTimer.Stop();
        _noteTimer.Start();
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        bool hasCaptions = _lines.Count > 0 || _liveRun.Text.Length > 0;
        string? message = _status ?? _note ?? (hasCaptions ? null : ListeningHint);
        StatusText.Text = message ?? string.Empty;
        StatusText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
        {
            // The user scrolled: follow live text only while they're at the bottom.
            bool wasFollowing = _followLive;
            _followLive = IsAtBottom && !IsHoldingView;
            _resumeAfterSelection = false;
            if (_followLive && !wasFollowing)
            {
                PruneOldLines();
            }
        }
        else if (_followLive)
        {
            // Content or window size changed: keep the newest text in view, unless text is being selected.
            if (IsHoldingView)
            {
                _followLive = false;
                _resumeAfterSelection = true;
            }
            else
            {
                Captions.ScrollToEnd();
            }
        }

        UpdateBackToLive();
    }

    private void OnSelectionChanged(object sender, RoutedEventArgs e) => ResumeIfSelectionDone();

    /// <summary>Once text is no longer selected, carry on following the conversation if the selection paused it.</summary>
    private void ResumeIfSelectionDone()
    {
        if (IsHoldingView)
        {
            UpdateBackToLive();
            return;
        }

        if (_resumeAfterSelection || (!_followLive && IsAtBottom))
        {
            _resumeAfterSelection = false;
            GoLive();
            return;
        }

        UpdateBackToLive();
    }

    private void UpdateBackToLive() =>
        BackToLiveButton.Visibility = !_followLive && !IsAtBottom ? Visibility.Visible : Visibility.Collapsed;

    private void OnBackToLiveClick(object sender, RoutedEventArgs e) => GoLive();

    private void GoLive()
    {
        _followLive = true;
        _resumeAfterSelection = false;
        Captions.Selection.Select(Captions.Document.ContentEnd, Captions.Document.ContentEnd);
        Captions.ScrollToEnd();
        PruneOldLines();
        UpdateBackToLive();
    }

    private void OnPreviewCommandExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command == ApplicationCommands.Copy)
        {
            CopySelection();
            e.Handled = true;
        }
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        var edges = EdgesAt(e.GetPosition(this));

        // On the text, the mouse selects it; on the button, it presses it. Anywhere else it moves the window.
        if (_dragging || hwnd == IntPtr.Zero || (edges == Edges.None && (Captions.IsMouseOver || BackToLiveButton.IsMouseOver))
            || !NativeMethods.GetWindowRect(hwnd, out _dragStartRect) || !NativeMethods.GetCursorPos(out _dragStartCursor))
        {
            return;
        }

        _dragEdges = edges;
        _dragging = CaptureMouse();
        if (_dragging)
        {
            CompositionTarget.Rendering += ApplyPendingBounds;
            e.Handled = true;
        }
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            // (Leave the cursor alone while text is being selected.)
            if (Mouse.Captured is null)
            {
                var edges = EdgesAt(e.GetPosition(this));
                Cursor = edges != Edges.None ? CursorFor(edges)
                    : Captions.IsMouseOver || BackToLiveButton.IsMouseOver ? null
                    : Cursors.SizeAll;
            }

            return;
        }

        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        _pendingRect = DraggedRect(cursor.X - _dragStartCursor.X, cursor.Y - _dragStartCursor.Y);
        e.Handled = true;
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        // A click on the text that selected nothing ends the pause too (checked once the text box has
        // handled the click).
        Dispatcher.InvokeAsync(ResumeIfSelectionDone, DispatcherPriority.Input);
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ApplyPendingBounds(this, EventArgs.Empty);
        CompositionTarget.Rendering -= ApplyPendingBounds;
        BoundsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Our own menu (which has Copy) everywhere, instead of the text box's standard one.
        MenuRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        Panel.BorderBrush = HoverOutline;
        Grip.Opacity = 0.6;
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            Panel.BorderBrush = Brushes.Transparent;
            Grip.Opacity = 0;
            Cursor = null;
        }
    }

    /// <summary>Moves/resizes the window to the latest dragged position, once per frame.</summary>
    private void ApplyPendingBounds(object? sender, EventArgs e)
    {
        if (_pendingRect is not NativeMethods.RECT rect)
        {
            return;
        }

        _pendingRect = null;
        uint flags = NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE;
        if (_dragEdges == Edges.None)
        {
            flags |= NativeMethods.SWP_NOSIZE;
        }

        // One call for position and size together, so the opposite edge stays put while resizing.
        NativeMethods.SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, rect.Left, rect.Top, rect.Width, rect.Height, flags);
    }

    /// <summary>The window rectangle after the mouse has moved by (dx, dy) device pixels since the drag started.</summary>
    private NativeMethods.RECT DraggedRect(int dx, int dy)
    {
        var start = _dragStartRect;
        if (_dragEdges == Edges.None)
        {
            return new NativeMethods.RECT
            {
                Left = start.Left + dx,
                Top = start.Top + dy,
                Right = start.Right + dx,
                Bottom = start.Bottom + dy,
            };
        }

        // Limits in device pixels: the minimum size, and at most the screen the window is on.
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int minWidth = (int)Math.Ceiling(MinWidth * scale);
        int minHeight = (int)Math.Ceiling(MinHeight * scale);
        int maxWidth = int.MaxValue;
        int maxHeight = int.MaxValue;
        var monitor = NativeMethods.MONITORINFO.Create();
        IntPtr hmonitor = NativeMethods.MonitorFromWindow(new WindowInteropHelper(this).Handle, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (NativeMethods.GetMonitorInfo(hmonitor, ref monitor))
        {
            maxWidth = Math.Max(minWidth, monitor.rcWork.Width);
            maxHeight = Math.Max(minHeight, monitor.rcWork.Height);
        }

        var rect = start;
        if (_dragEdges.HasFlag(Edges.Left))
        {
            rect.Left = Math.Clamp(start.Left + dx, start.Right - maxWidth, start.Right - minWidth);
        }
        else if (_dragEdges.HasFlag(Edges.Right))
        {
            rect.Right = Math.Clamp(start.Right + dx, start.Left + minWidth, start.Left + maxWidth);
        }

        if (_dragEdges.HasFlag(Edges.Top))
        {
            rect.Top = Math.Clamp(start.Top + dy, start.Bottom - maxHeight, start.Bottom - minHeight);
        }
        else if (_dragEdges.HasFlag(Edges.Bottom))
        {
            rect.Bottom = Math.Clamp(start.Bottom + dy, start.Top + minHeight, start.Top + maxHeight);
        }

        return rect;
    }

    /// <summary>Which edges a point (in DIPs, relative to the window) is close enough to grab.</summary>
    private Edges EdgesAt(Point point)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        var edges = Edges.None;

        // Corners get a bigger target, as they're the usual way to resize.
        bool nearCornerX = point.X <= ResizeCorner || point.X >= width - ResizeCorner;
        bool nearCornerY = point.Y <= ResizeCorner || point.Y >= height - ResizeCorner;
        double edge = nearCornerX && nearCornerY ? ResizeCorner : ResizeEdge;

        if (point.X <= edge)
        {
            edges |= Edges.Left;
        }
        else if (point.X >= width - edge)
        {
            edges |= Edges.Right;
        }

        if (point.Y <= edge)
        {
            edges |= Edges.Top;
        }
        else if (point.Y >= height - edge)
        {
            edges |= Edges.Bottom;
        }

        return edges;
    }
}
