using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using LiveCaptionsUpgrade.Core;

namespace LiveCaptionsUpgrade;

/// <summary>Thrown when Windows Live Captions is not installed (it ships with Windows 11 22H2 and later).</summary>
internal sealed class LiveCaptionsUnavailableException : Exception
{
    public LiveCaptionsUnavailableException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Reads the caption text from the built-in Windows 11 Live Captions app via UI Automation,
/// the same accessibility interface screen readers use.
/// </summary>
/// <remarks>
/// Use it from a single thread (see <see cref="CaptionService"/>). Only <see cref="Show"/> may also be called
/// from another thread, as a last-ditch restore when the app is crashing.
/// </remarks>
internal sealed class LiveCaptionsReader
{
    private const string WindowClassName = "LiveCaptionsDesktopWindow";
    private const string CaptionsAutomationId = "CaptionsTextBlock";
    private const string ProcessName = "LiveCaptions";

    // Searching the UI Automation tree is relatively slow, so don't retry on every poll. Still frequent
    // enough that a freshly started Live Captions is hidden soon after its caption area appears.
    private static readonly TimeSpan CaptionsSearchInterval = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(10);

    // Don't fight Live Captions more than this often if it keeps undoing the hiding.
    private static readonly TimeSpan RehideInterval = TimeSpan.FromSeconds(2);

    // How far past the edge of the desktop the invisible window is parked.
    private const int OffScreenMargin = 200;

    // Least size of the invisible window (unless docked). The bigger it is, the more lines of text Live Captions
    // keeps, so an address or a card number read out stays in the text until it's finished, rather than scrolling
    // away while Live Captions is still rewriting it.
    private const int HiddenMinWidth = 1600;
    private const int HiddenMinHeight = 700;

    // Allowance for the invisible resize borders Windows adds around window rectangles.
    private const int EdgeTolerance = 16;

    // The extended-style bits Hide adds; all of them together mean the window was hidden by this app.
    private const int HiddenStyleBits = NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT;

    private readonly object _windowLock = new();
    private IntPtr _hwnd;
    private AutomationElement? _window;
    private AutomationElement? _captions;
    private DateTime _lastCaptionsSearch = DateTime.MinValue;
    private SavedWindowState? _saved;
    private LiveCaptionsHideMethod _hideMethod;
    private DateTime _lastHide = DateTime.MinValue;

    public static string ExecutablePath => Path.Combine(Environment.SystemDirectory, "LiveCaptions.exe");

    public bool IsAttached => _hwnd != IntPtr.Zero && NativeMethods.IsWindow(_hwnd);

    /// <summary>True once the caption text has been found, i.e. Live Captions is past any first-run setup screen.</summary>
    public bool HasFoundCaptions => _captions is not null;

    public bool IsHidden => _saved is not null;

    /// <summary>The method used by the current <see cref="Hide"/>.</summary>
    public LiveCaptionsHideMethod HideMethod => _hideMethod;

    /// <summary>
    /// True if Live Captions was docked to the top or bottom of the screen when it was hidden. Windows keeps
    /// that strip of the screen reserved for it, so it shows up as empty space while Live Captions is invisible.
    /// </summary>
    public bool IsDocked { get; private set; }

    /// <summary>Finds the Live Captions window, starting Live Captions first if it is not running.</summary>
    /// <returns>False if the window did not appear in time.</returns>
    public async Task<bool> AttachAsync(CancellationToken cancellationToken)
    {
        IntPtr hwnd = NativeMethods.FindWindow(WindowClassName, null);
        if (hwnd == IntPtr.Zero)
        {
            if (!File.Exists(ExecutablePath))
            {
                throw new LiveCaptionsUnavailableException(
                    "Windows Live Captions was not found. It requires Windows 11 version 22H2 or later. To show captions "
                    + "from another computer instead, right-click here > Caption sharing > Show captions from another computer.");
            }

            // Don't start a second copy if one is already running, e.g. still on its first-run setup screen.
            var running = Process.GetProcessesByName(ProcessName);
            bool isRunning = running.Length > 0;
            foreach (var process in running)
            {
                process.Dispose();
            }

            if (!isRunning)
            {
                Process.Start(new ProcessStartInfo(ExecutablePath) { UseShellExecute = true })?.Dispose();
            }

            var deadline = DateTime.UtcNow + LaunchTimeout;
            while (hwnd == IntPtr.Zero && DateTime.UtcNow < deadline)
            {
                // Poll quickly so the window is found (and hidden) soon after it appears.
                await Task.Delay(100, cancellationToken);
                hwnd = NativeMethods.FindWindow(WindowClassName, null);
            }
        }

        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var window = AutomationElement.FromHandle(hwnd);
        lock (_windowLock)
        {
            _hwnd = hwnd;
            _window = window;
            _captions = null;
            _lastCaptionsSearch = DateTime.MinValue;
            _saved = null;
            IsDocked = false;
            AdoptLeftoverHiddenState();
        }

        return true;
    }

    /// <summary>Returns the text currently shown by Live Captions, or null if it could not be read this time.</summary>
    public string? ReadText()
    {
        if (!IsAttached || _window is null)
        {
            return null;
        }

        try
        {
            if (_captions is null)
            {
                if (DateTime.UtcNow - _lastCaptionsSearch < CaptionsSearchInterval)
                {
                    return null;
                }

                _lastCaptionsSearch = DateTime.UtcNow;
                _captions = _window.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, CaptionsAutomationId));
                if (_captions is null)
                {
                    return null;
                }
            }

            return _captions.Current.Name ?? string.Empty;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or TimeoutException)
        {
            // Live Captions rebuilt its UI (e.g. after a settings change); look the element up again.
            _captions = null;
            return null;
        }
    }

    /// <summary>
    /// Makes the Live Captions window invisible while it keeps captioning in the background:
    /// no window on screen, no taskbar button, not in Alt+Tab.
    /// </summary>
    public void Hide(LiveCaptionsHideMethod method)
    {
        lock (_windowLock)
        {
            if (!IsAttached || IsHidden)
            {
                return;
            }

            int exStyle = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
            var placement = NativeMethods.WINDOWPLACEMENT.Create();
            NativeMethods.GetWindowPlacement(_hwnd, ref placement);

            _saved = new SavedWindowState(exStyle, GetAlpha(exStyle), placement);
            _hideMethod = method;
            IsDocked = IsDockedToScreenEdge();
            ApplyHidden();
        }
    }

    /// <summary>
    /// Re-hides Live Captions if it undid the hiding, e.g. after a settings change or display change.
    /// Cheap enough to call on every poll.
    /// </summary>
    public void KeepHidden()
    {
        lock (_windowLock)
        {
            if (!IsHidden || !IsAttached || IsStillHidden() || DateTime.UtcNow - _lastHide < RehideInterval)
            {
                return;
            }

            ApplyHidden();
        }
    }

    /// <summary>Puts the Live Captions window back exactly as it was before <see cref="Hide"/>.</summary>
    public void Show()
    {
        lock (_windowLock)
        {
            var saved = _saved;
            _saved = null;
            IsDocked = false;
            if (saved is null || !IsAttached)
            {
                return;
            }

            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, saved.ExStyle);
            if (saved.Alpha is byte alpha)
            {
                NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, alpha, NativeMethods.LWA_ALPHA);
            }

            // Restoring the placement (after the original style, which it depends on) moves the window back and shows it.
            var placement = saved.Placement;
            placement.showCmd = placement.showCmd == NativeMethods.SW_SHOWMAXIMIZED
                ? NativeMethods.SW_SHOWMAXIMIZED
                : NativeMethods.SW_SHOWNOACTIVATE;
            placement.rcNormalPosition = KeepOnScreen(placement.rcNormalPosition);
            NativeMethods.SetWindowPlacement(_hwnd, ref placement);
        }
    }

    /// <summary>
    /// If this app was force-closed while Live Captions was hidden, the window is still invisible. Treat it as
    /// hidden by us, remembering the visible state to go back to, so it's shown again when it should be.
    /// </summary>
    private void AdoptLeftoverHiddenState()
    {
        int exStyle = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        bool leftInvisible = (exStyle & HiddenStyleBits) == HiddenStyleBits && GetAlpha(exStyle) == 0;
        bool leftMinimized = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0 && NativeMethods.IsIconic(_hwnd);
        if (!leftInvisible && !leftMinimized)
        {
            return;
        }

        var placement = NativeMethods.WINDOWPLACEMENT.Create();
        NativeMethods.GetWindowPlacement(_hwnd, ref placement);
        _saved = new SavedWindowState(exStyle & ~HiddenStyleBits, null, placement);
        _hideMethod = leftInvisible ? LiveCaptionsHideMethod.Invisible : LiveCaptionsHideMethod.Minimize;
    }

    /// <summary>
    /// Moves a window rectangle that is off every screen (e.g. where it was parked) onto the main screen, no wider
    /// than it and no taller than a third of it (it may have been left made big for hiding).
    /// </summary>
    private static NativeMethods.RECT KeepOnScreen(NativeMethods.RECT rect)
    {
        if (NativeMethods.MonitorFromRect(ref rect, NativeMethods.MONITOR_DEFAULTTONULL) != IntPtr.Zero)
        {
            return rect;
        }

        int width = Math.Min(rect.Width, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN));
        int height = Math.Min(rect.Height, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN) / 3);
        int left = Math.Max(0, (NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN) - width) / 2);
        return new NativeMethods.RECT { Left = left, Top = 0, Right = left + width, Bottom = height };
    }

    private byte? GetAlpha(int exStyle)
    {
        if ((exStyle & NativeMethods.WS_EX_LAYERED) != 0
            && NativeMethods.GetLayeredWindowAttributes(_hwnd, out _, out byte alpha, out uint flags)
            && (flags & NativeMethods.LWA_ALPHA) != 0)
        {
            return alpha;
        }

        return null;
    }

    private void ApplyHidden()
    {
        _lastHide = DateTime.UtcNow;
        int exStyle = (NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE) | NativeMethods.WS_EX_TOOLWINDOW)
            & ~NativeMethods.WS_EX_APPWINDOW;

        // The taskbar only drops a window's button if its style changes while the window is hidden.
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);

        if (_hideMethod == LiveCaptionsHideMethod.Minimize)
        {
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWMINNOACTIVE);
            return;
        }

        // Fully transparent and click-through, so it can't be seen or clicked wherever it ends up...
        NativeMethods.SetWindowLong(
            _hwnd, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT);
        NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, 0, NativeMethods.LWA_ALPHA);

        // ...still shown rather than minimised, so it carries on updating its captions...
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);

        // ...and parked past the bottom-right corner of the desktop for good measure, made big enough to keep plenty
        // of text (not when docked, where it sizes itself; Show puts the size back).
        int x = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN)
            + NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN) + OffScreenMargin;
        int y = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN)
            + NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN) + OffScreenMargin;
        var current = default(NativeMethods.RECT);
        bool resize = !IsDocked && NativeMethods.GetWindowRect(_hwnd, out current);
        NativeMethods.SetWindowPos(
            _hwnd,
            IntPtr.Zero,
            x,
            y,
            resize ? Math.Max(current.Width, HiddenMinWidth) : 0,
            resize ? Math.Max(current.Height, HiddenMinHeight) : 0,
            (resize ? 0 : NativeMethods.SWP_NOSIZE) | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    private bool IsStillHidden()
    {
        int exStyle = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        if (_hideMethod == LiveCaptionsHideMethod.Minimize)
        {
            return (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0 && NativeMethods.IsIconic(_hwnd);
        }

        // Position is deliberately not checked: a docked Live Captions may move itself back, but it stays invisible.
        return (exStyle & HiddenStyleBits) == HiddenStyleBits && !NativeMethods.IsIconic(_hwnd) && GetAlpha(exStyle) == 0;
    }

    private bool IsDockedToScreenEdge()
    {
        if (NativeMethods.IsIconic(_hwnd) || !NativeMethods.GetWindowRect(_hwnd, out var window))
        {
            return false;
        }

        var monitor = NativeMethods.MONITORINFO.Create();
        if (!NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(_hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST), ref monitor))
        {
            return false;
        }

        // Docked, Live Captions spans the full width and sits just outside the usable work area it reserved.
        var work = monitor.rcWork;
        bool fullWidth = window.Width >= monitor.rcMonitor.Width - EdgeTolerance;
        bool aboveWorkArea = Math.Abs(window.Bottom - work.Top) <= EdgeTolerance;
        bool belowWorkArea = Math.Abs(window.Top - work.Bottom) <= EdgeTolerance;
        return fullWidth && (aboveWorkArea || belowWorkArea);
    }

    private sealed record SavedWindowState(int ExStyle, byte? Alpha, NativeMethods.WINDOWPLACEMENT Placement);
}
