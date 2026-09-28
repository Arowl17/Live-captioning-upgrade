using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using LiveCaptionsUpgrade.Core;
using LiveCaptionsUpgrade.Core.Sharing;
using Xunit;

namespace LiveCaptionsUpgrade.AppTests;

/// <summary>
/// Opens each window the way the app does, so a mistake in its layout (which only shows up when it's
/// loaded) fails here rather than on someone's screen.
/// </summary>
public class WindowTests
{
    [Fact]
    public void Every_window_opens_and_works_with_captions_and_settings()
    {
        int liveCaptionsBefore = CountLiveCaptions();
        RunOnUiThread(() =>
        {
            var app = new global::LiveCaptionsUpgrade.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            string folder = Path.Combine(Path.GetTempPath(), "lcu-windows-" + Guid.NewGuid().ToString("N"));
            var sharing = new SharingController(new PairingStore(Path.Combine(folder, "pairing.json")), DeviceIdentity.Create, "TEST-PC", "test", tcpPort: 0, discoveryPort: 0);
            try
            {
                var settings = new AppSettings { WindowLeft = 100, WindowTop = 100 };

                var overlay = new OverlayWindow(settings);
                overlay.Show();
                overlay.ShowUpdate(new CaptionUpdate(new[] { "Hello, this is Anna from billing." }, "How can I", true));
                overlay.ShowLines(new[]
                {
                    new CaptionLine("From the other computer.", DateTimeOffset.Now),
                    new CaptionLine("Too old to keep.", DateTimeOffset.Now.AddHours(-1)),
                }, "help");
                overlay.ShowStatus("Waiting for LAPTOP…");
                DoEvents();
                Assert.False(overlay.HasSelection);
                overlay.ApplySettings(settings);
                overlay.StoreBounds();
                overlay.Hide();
                overlay.Show();
                DoEvents();

                var settingsWindow = new SettingsWindow(settings, app, sharing);
                settingsWindow.Show();
                DoEvents();
                settingsWindow.ReflectQuickToggles(new AppSettings { CaptionSharing = CaptionSharingMode.Receive });
                DoEvents();
                settingsWindow.Close();

                sharing.SetModeAsync(CaptionSharingMode.Receive).GetAwaiter().GetResult();
                var pairing = new PairingWindow(sharing);
                pairing.Show();
                DoEvents();
                pairing.Close();

                overlay.Close();
            }
            finally
            {
                sharing.DisposeAsync().AsTask().GetAwaiter().GetResult();
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
        });

        // Only reading captions may start Live Captions; opening windows or showing shared captions must not.
        Thread.Sleep(2000);
        Assert.Equal(liveCaptionsBefore, CountLiveCaptions());
    }

    private static int CountLiveCaptions()
    {
        var processes = System.Diagnostics.Process.GetProcessesByName("LiveCaptions");
        foreach (var process in processes)
        {
            process.Dispose();
        }

        return processes.Length;
    }

    /// <summary>Lets pending layout, rendering and loaded events run.</summary>
    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void RunOnUiThread(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                error = e;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw new Exception("A window failed: " + error, error);
        }
    }
}
