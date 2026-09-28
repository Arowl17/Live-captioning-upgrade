using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using LiveCaptionsUpgrade.Core;
using LiveCaptionsUpgrade.Core.Sharing;

namespace LiveCaptionsUpgrade;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private AppSettings _settings = new();
    private string _settingsPath = AppSettings.DefaultPath;
    private CaptionService? _service;
    private OverlayWindow? _overlay;
    private TrayIcon? _tray;
    private GlobalHotkey? _hotkey;
    private SettingsWindow? _settingsWindow;
    private PairingWindow? _pairingWindow;
    private TranscriptRecorder? _transcript;
    private SharingController? _sharing;

    // Caption sharing mode switches run one at a time; this is the latest one.
    private readonly SemaphoreSlim _sharingSwitchLock = new(1, 1);
    private Task _sharingSwitch = Task.CompletedTask;
    private CaptionSharingMode _activeSharing = CaptionSharingMode.Off;
    private bool _cleanedUp;

    private static string AppVersion { get; } =
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0";

    internal bool HasCaptionSelection => _overlay?.HasSelection == true;

    internal void CopyCaptionSelection() => _overlay?.CopySelection();

    internal void ToggleOverlayVisible()
    {
        if (_overlay is null)
        {
            return;
        }

        // Captions keep being collected while hidden, so earlier text can be scrolled back to after showing it again.
        if (_overlay.Visibility == Visibility.Visible)
        {
            _overlay.Hide();
        }
        else
        {
            _overlay.Show();
        }

        RefreshTray();
    }

    internal void ToggleClickThrough()
    {
        _overlay?.SetClickThrough(!_settings.ClickThrough);
        _settingsWindow?.ReflectQuickToggles(_settings);
        RefreshTray();
    }

    internal string SharingStatus => _sharing?.Describe() ?? string.Empty;

    /// <summary>Switches caption sharing from the menu.</summary>
    internal void SetCaptionSharing(CaptionSharingMode mode)
    {
        if (_settings.CaptionSharing == mode)
        {
            return;
        }

        _settings.CaptionSharing = mode;
        _settingsWindow?.ReflectQuickToggles(_settings);
        SaveSettings();
        _sharingSwitch = SwitchCaptionSharingAsync();
        RefreshTray();
    }

    /// <summary>Completes when the latest caption sharing switch (started by applying settings) has finished.</summary>
    internal Task WaitForSharingSwitchAsync() => _sharingSwitch;

    /// <summary>Opens the window for pairing with another computer.</summary>
    /// <remarks>It has no owner window: closing Settings must not cancel a pairing in progress.</remarks>
    internal async void OpenPairing()
    {
        if (_sharing is null)
        {
            return;
        }

        if (_pairingWindow is not null)
        {
            _pairingWindow.Activate();
            return;
        }

        try
        {
            await _sharingSwitch;
            if (_cleanedUp || _pairingWindow is not null)
            {
                _pairingWindow?.Activate();
                return;
            }

            if (!_sharing.IsRunning)
            {
                _tray?.ShowNotice(_sharing.Mode == CaptionSharingMode.Off
                    ? "Turn on caption sharing first: right-click the captions > Caption sharing > Send or Show."
                    : _sharing.Describe());
                return;
            }

            ShowPairingWindow(new PairingWindow(_sharing));
        }
        catch (Exception ex)
        {
            Log.Error("Opening the pairing window failed", ex);
            _tray?.ShowNotice("Couldn't open the pairing window: " + ex.Message);
        }
    }

    internal void ToggleLiveCaptionsWindow()
    {
        _settings.HideLiveCaptionsWindow = !_settings.HideLiveCaptionsWindow;
        _service?.SetLiveCaptionsHidden(_settings.HideLiveCaptionsWindow);
        _settingsWindow?.ReflectQuickToggles(_settings);
        RefreshTray();
    }

    internal void OpenTranscriptsFolder()
    {
        string folder = _settings.ResolveTranscriptFolder();
        try
        {
            Directory.CreateDirectory(folder);
            OpenWithShell(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            _tray?.ShowNotice($"Can't open the transcripts folder ({ex.Message}). Check it in Settings.");
        }
    }

    internal void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        // Pause the shortcut so it can be typed into the shortcut box instead of hiding the captions.
        _hotkey?.Unregister();
        _settingsWindow = new SettingsWindow(_settings, this, _sharing!);
        _settingsWindow.Applied += ApplySettings;
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            RegisterHotkey();
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    internal void ExitApp()
    {
        Cleanup();
        Shutdown();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, _) =>
        {
            // The process is going down (error on another thread): at least don't leave Live Captions invisible.
            try
            {
                _service?.RestoreLiveCaptionsWindowNow();
            }
            catch (Exception)
            {
                // Nothing more can be done at this point.
            }
        };

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\LiveCaptionsUpgrade.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Live Captions Upgrade is already running. Look for its icon in the notification area.",
                "Live Captions Upgrade", MessageBoxButton.OK, MessageBoxImage.Information);
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        _settings = AppSettings.Load(_settingsPath);
        FileLog.Start();

        _overlay = new OverlayWindow(_settings);
        _tray = new TrayIcon(this);
        _overlay.MenuRequested += (_, _) => _tray.ShowMenuAtCursor();
        _overlay.BoundsChanged += (_, _) =>
        {
            // Remember the new position and size straight away, not only on exit.
            _overlay.StoreBounds();
            SaveSettings();
        };

        _hotkey = new GlobalHotkey(_overlay);
        _hotkey.Pressed += (_, _) => ToggleOverlayVisible();
        RegisterHotkey();

        _transcript = new TranscriptRecorder();
        _transcript.ApplySettings(_settings);
        _transcript.Notice += message => Dispatcher.InvokeAsync(() => _tray.ShowNotice(message));

        _sharing = new SharingController(
            new PairingStore(PairingStore.DefaultPath), IdentityStore.LoadOrCreate, Environment.MachineName, AppVersion);
        _sharing.CaptionsReceived += OnCaptionsReceived;
        _sharing.StateChanged += () => Dispatcher.InvokeAsync(OnSharingStateChanged);
        _sharing.PairingRequested += session => Dispatcher.InvokeAsync(() => OnPairingRequested(session));

        // When sending, the captions are read on the other computer, so this one's bar starts hidden.
        if (_settings.CaptionSharing != CaptionSharingMode.Send)
        {
            _overlay.Show();
        }

        RefreshTray();
        _sharingSwitch = SwitchCaptionSharingAsync(startingUp: true);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cleanup();
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        Cleanup();
        base.OnSessionEnding(e);
    }

    private void ApplySettings(AppSettings updated)
    {
        _settings = updated;
        _overlay?.ApplySettings(updated);
        _service?.ApplySettings(updated);
        _transcript?.ApplySettings(updated);
        if (updated.CaptionSharing != _activeSharing)
        {
            _sharingSwitch = SwitchCaptionSharingAsync();
        }

        // While the settings window is open the shortcut stays paused; it is registered when the window closes.
        if (_settingsWindow is null)
        {
            RegisterHotkey();
        }

        RefreshTray();
        SaveSettings();
    }

    /// <summary>
    /// Brings caption sharing and the caption source in line with the setting: Live Captions on this computer
    /// (off or sending), or the captions the paired computer sends (showing).
    /// </summary>
    private async Task SwitchCaptionSharingAsync(bool startingUp = false)
    {
        await _sharingSwitchLock.WaitAsync();
        try
        {
            if (_cleanedUp || _sharing is null || _overlay is null)
            {
                return;
            }

            var previous = _activeSharing;
            var target = _settings.CaptionSharing;
            if (target == previous && !startingUp)
            {
                return;
            }

            // Let the other computer through Windows Firewall first, so Windows doesn't ask in its own way as well.
            if (target != CaptionSharingMode.Off && previous == CaptionSharingMode.Off && !startingUp)
            {
                await EnsureFirewallRuleAsync();
                if (_cleanedUp)
                {
                    return;
                }
            }

            if (target == CaptionSharingMode.Receive && _service is not null)
            {
                // Captions now come from the other computer: stop reading (and give back) Live Captions here.
                var service = _service;
                _service = null;
                await service.StopAsync();
                _overlay.ShowStatus(null);
            }
            else if (target != CaptionSharingMode.Receive && _service is null && !_cleanedUp)
            {
                // Any "waiting for the other computer" message no longer applies.
                _overlay.ShowStatus(null);
                StartLiveCaptions();
            }

            await _sharing.SetModeAsync(target);
            _activeSharing = target;
            if (_cleanedUp)
            {
                return;
            }

            if (target == CaptionSharingMode.Send && previous != CaptionSharingMode.Send && !startingUp)
            {
                _overlay.Hide();
                string shortcut = _settings.ToggleHotkey.Length > 0 ? $" ({_settings.ToggleHotkey})" : string.Empty;
                _tray?.ShowNotice($"Captions are now sent to the paired computer. This computer's caption bar is hidden; "
                    + $"show it from the menu{shortcut} if you want it here too.");
            }
            else if (previous == CaptionSharingMode.Send && target != CaptionSharingMode.Send)
            {
                _overlay.Show();
            }

            // Not paired yet: that's the next step.
            if (target != CaptionSharingMode.Off && _sharing.Paired is null && !startingUp && _pairingWindow is null)
            {
                ShowPairingWindow(new PairingWindow(_sharing));
            }

            OnSharingStateChanged();
        }
        catch (Exception ex)
        {
            Log.Error("Switching caption sharing failed", ex);
            _tray?.ShowNotice("Couldn't switch caption sharing: " + ex.Message);
        }
        finally
        {
            _sharingSwitchLock.Release();
        }
    }

    private void StartLiveCaptions()
    {
        var service = new CaptionService(_settings, _transcript!);
        service.CaptionsUpdated += update =>
        {
            // Recorded for the other computer even while not sending, so it gets the recent text when sharing starts.
            _sharing?.Publish(update);
            Dispatcher.InvokeAsync(() => _overlay?.ShowUpdate(update));
        };
        service.StatusChanged += status =>
        {
            _sharing?.SetSenderStatus(status);
            Dispatcher.InvokeAsync(() => _overlay?.ShowStatus(status));
        };
        service.Notice += message => Dispatcher.InvokeAsync(() => _tray?.ShowNotice(message));
        _service = service;
        service.Start();
    }

    /// <summary>Asks once to let the other computer through Windows Firewall, if it isn't already.</summary>
    private async Task EnsureFirewallRuleAsync()
    {
        if (await Task.Run(FirewallRule.Exists))
        {
            return;
        }

        const string Message = "So the other computer can connect to this one, Windows Firewall has to allow Live Captions Upgrade. "
            + "Windows will ask for permission next (on a work computer: the administrator password).\n\nAllow it now?";
        var answer = _settingsWindow is { IsVisible: true } owner
            ? MessageBox.Show(owner, Message, "Live Captions Upgrade", MessageBoxButton.YesNo, MessageBoxImage.Information)
            : MessageBox.Show(Message, "Live Captions Upgrade", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer == MessageBoxResult.Yes && !await Task.Run(FirewallRule.Add))
        {
            _tray?.ShowNotice("Windows Firewall wasn't changed, so the other computer may not be able to connect. Settings > Caption sharing > Allow… tries again.");
        }
    }

    /// <summary>Captions from the paired computer (network thread).</summary>
    private void OnCaptionsReceived(IReadOnlyList<CaptionLine> lines, string pending)
    {
        _transcript?.Write(lines);
        Dispatcher.InvokeAsync(() =>
        {
            if (_activeSharing == CaptionSharingMode.Receive || _sharing?.Mode == CaptionSharingMode.Receive)
            {
                _overlay?.ShowLines(lines, pending);
            }
        });
    }

    private void OnSharingStateChanged()
    {
        if (_sharing?.Mode == CaptionSharingMode.Receive)
        {
            _overlay?.ShowStatus(_sharing.DescribeProblemForReceiver());
        }

        RefreshTray();
    }

    private void OnPairingRequested(PairingSession session)
    {
        if (_cleanedUp || _sharing is null)
        {
            session.Reject();
            return;
        }

        // A request from the other computer replaces this one's own "choose a computer" step.
        _pairingWindow?.Close();
        var window = new PairingWindow(_sharing, session);
        ShowPairingWindow(window);
    }

    private void ShowPairingWindow(PairingWindow window)
    {
        _pairingWindow?.Close();
        _pairingWindow = window;
        window.Closed += (_, _) =>
        {
            if (_pairingWindow == window)
            {
                _pairingWindow = null;
            }
        };
        window.Show();
        window.Activate();
    }

    private void RegisterHotkey()
    {
        if (_hotkey is null)
        {
            return;
        }

        Hotkey.TryParse(_settings.ToggleHotkey, out var hotkey);
        if (!_hotkey.Register(hotkey))
        {
            _tray?.ShowNotice($"The shortcut {hotkey} couldn't be set up; another app is probably using it. Choose a different one in Settings.");
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Exit cleanly rather than crash, so the hidden Live Captions window is always given back.
        e.Handled = true;
        if (_cleanedUp)
        {
            return;
        }

        MessageBox.Show("Live Captions Upgrade hit an unexpected error and will close:\n\n" + e.Exception.Message,
            "Live Captions Upgrade", MessageBoxButton.OK, MessageBoxImage.Error);
        ExitApp();
    }

    private void Cleanup()
    {
        if (_cleanedUp || _singleInstance is null)
        {
            return;
        }

        _cleanedUp = true;

        // The polling loop and the network code run on the thread pool without capturing this thread,
        // so blocking here cannot deadlock.
        _service?.StopAsync().GetAwaiter().GetResult();
        _sharing?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _transcript?.Dispose();

        _pairingWindow?.Close();
        _settingsWindow?.Close();
        _hotkey?.Dispose();
        if (_overlay is not null)
        {
            _overlay.StoreBounds();
            _overlay.Close();
        }

        SaveSettings();
        _tray?.Dispose();
        _singleInstance.ReleaseMutex();
        _singleInstance.Dispose();
    }

    private void SaveSettings()
    {
        try
        {
            _settings.Save(_settingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth a crash; the settings stay in effect until the app closes.
        }
    }

    private void RefreshTray() => _tray?.Refresh(
        captionsVisible: _overlay?.Visibility == Visibility.Visible,
        hotkey: _settings.ToggleHotkey,
        clickThrough: _settings.ClickThrough,
        liveCaptionsVisible: !_settings.HideLiveCaptionsWindow,
        sharing: _settings.CaptionSharing,
        sharingStatus: SharingStatus);

    private static void OpenWithShell(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
}
