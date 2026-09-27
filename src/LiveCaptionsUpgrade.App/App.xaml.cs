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
    private bool _cleanedUp;

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

    internal void OpenSettingsFile()
    {
        _overlay?.StoreBounds();
        _settings.Save(_settingsPath);
        OpenWithShell(_settingsPath);
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
        RefreshTray();

        _service = new CaptionService(_settings);
        _service.CaptionsUpdated += update => Dispatcher.InvokeAsync(() => _overlay.ShowUpdate(update));
        _service.StatusChanged += status => Dispatcher.InvokeAsync(() => _overlay.ShowStatus(status));
        _service.Notice += message => Dispatcher.InvokeAsync(() => _tray.ShowNotice(message));
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _overlay.Show();
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

        if (_overlay is not null)
        {
            _overlay.StoreBounds();
            _overlay.Close();
        }

        try
        {
            _settings.Save(_settingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the window position is not worth a crash on exit.
        }

        _tray?.Dispose();
        _singleInstance.ReleaseMutex();
        _singleInstance.Dispose();
    }

    private void RefreshTray() => _tray?.Refresh(_settings.ClickThrough, !_settings.HideLiveCaptionsWindow);

    private static void OpenWithShell(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
}
