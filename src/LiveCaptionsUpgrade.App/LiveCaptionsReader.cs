using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

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
/// <remarks>Not thread-safe: use it from a single thread (see <see cref="CaptionService"/>).</remarks>
internal sealed class LiveCaptionsReader
{
    private const string WindowClassName = "LiveCaptionsDesktopWindow";
    private const string CaptionsAutomationId = "CaptionsTextBlock";
    private const string ProcessName = "LiveCaptions";

    // Searching the UI Automation tree is relatively slow, so don't retry on every poll.
    private static readonly TimeSpan CaptionsSearchInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(10);

    private IntPtr _hwnd;
    private AutomationElement? _window;
    private AutomationElement? _captions;
    private DateTime _lastCaptionsSearch = DateTime.MinValue;
    private int? _originalExStyle;

    public static string ExecutablePath => Path.Combine(Environment.SystemDirectory, "LiveCaptions.exe");

    public bool IsAttached => _hwnd != IntPtr.Zero && NativeMethods.IsWindow(_hwnd);

    public bool IsHidden => _originalExStyle is not null;

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
                    "Windows Live Captions was not found. It requires Windows 11 version 22H2 or later.");
            }

            // Don't start a second copy if one is already running, e.g. still on its first-run setup screen.
            if (Process.GetProcessesByName(ProcessName).Length == 0)
            {
                Process.Start(new ProcessStartInfo(ExecutablePath) { UseShellExecute = true })?.Dispose();
            }

            var deadline = DateTime.UtcNow + LaunchTimeout;
            while (hwnd == IntPtr.Zero && DateTime.UtcNow < deadline)
            {
                await Task.Delay(250, cancellationToken);
                hwnd = NativeMethods.FindWindow(WindowClassName, null);
            }
        }

        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        _hwnd = hwnd;
        _window = AutomationElement.FromHandle(hwnd);
        _captions = null;
        _lastCaptionsSearch = DateTime.MinValue;
        _originalExStyle = null;
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
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            // Live Captions rebuilt its UI (e.g. after a settings change); look the element up again.
            _captions = null;
            return null;
        }
    }

    /// <summary>Minimises Live Captions and removes it from the taskbar. It keeps captioning in the background.</summary>
    public void Hide()
    {
        if (!IsAttached || IsHidden)
        {
            return;
        }

        int exStyle = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        _originalExStyle = exStyle;
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_TOOLWINDOW);
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_MINIMIZE);
    }

    /// <summary>Undoes <see cref="Hide"/>.</summary>
    public void Show()
    {
        if (!IsAttached || _originalExStyle is not int exStyle)
        {
            _originalExStyle = null;
            return;
        }

        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_RESTORE);
        _originalExStyle = null;
    }
}
