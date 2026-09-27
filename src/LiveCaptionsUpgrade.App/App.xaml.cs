using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using LiveCaptionsUpgrade.Core;

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
    private bool _cleanedUp;

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
        RefreshTray();
    }

    internal void ToggleLiveCaptionsWindow()
    {
        _settings.HideLiveCaptionsWindow = !_settings.HideLiveCaptionsWindow;
        _service?.SetLiveCaptionsHidden(_settings.HideLiveCaptionsWindow);
        RefreshTray();
    }

    internal void OpenTranscriptsFolder()
    {
        string folder = _settings.ResolveTranscriptFolder();
        Directory.CreateDirectory(folder);
        OpenWithShell(folder);
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
        _settingsWindow = new SettingsWindow(_settings);
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

        _overlay = new OverlayWindow(_settings);
        _tray = new TrayIcon(this);
        _overlay.MenuRequested += (_, _) => _tray.ShowMenuAtCursor();

        _hotkey = new GlobalHotkey(_overlay);
        _hotkey.Pressed += (_, _) => ToggleOverlayVisible();
        RegisterHotkey();

        _service = new CaptionService(_settings);
        _service.CaptionsUpdated += update => Dispatcher.InvokeAsync(() => _overlay.ShowUpdate(update));
        _service.StatusChanged += status => Dispatcher.InvokeAsync(() => _overlay.ShowStatus(status));
        _service.Notice += message => Dispatcher.InvokeAsync(() => _tray.ShowNotice(message));
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _overlay.Show();
        RefreshTray();
        _service.Start();
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

        // While the settings window is open the shortcut stays paused; it is registered when the window closes.
        if (_settingsWindow is null)
        {
            RegisterHotkey();
        }

        RefreshTray();
        SaveSettings();
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
            _tray?.ShowNotice($"The shortcut {hotkey} is already used by another app. Choose a different one in Settings.");
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Exit cleanly rather than crash, so the hidden Live Captions window is always given back.
        e.Handled = true;
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

        // The polling loop runs on the thread pool without capturing this thread, so blocking here cannot deadlock.
        _service?.StopAsync().GetAwaiter().GetResult();

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
        liveCaptionsVisible: !_settings.HideLiveCaptionsWindow);

    private static void OpenWithShell(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
}
