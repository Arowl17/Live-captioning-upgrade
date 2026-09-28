using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using LiveCaptionsUpgrade.Core;
using Xunit;

namespace LiveCaptionsUpgrade.AppTests;

/// <summary>
/// Runs the real app, the way the work laptop does (showing captions from another computer), and opens its
/// windows. Layout mistakes only show up when a window is loaded, so they fail here rather than on someone's screen.
/// </summary>
public class WindowTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    [Fact]
    public void The_app_starts_in_show_mode_opens_its_windows_shows_shared_captions_and_exits()
    {
        // It uses the real settings folder, so it only runs on a throwaway build machine.
        if (Environment.GetEnvironmentVariable("CI") is null)
        {
            return;
        }

        string settingsPath = AppSettings.DefaultPath;
        string? savedSettings = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
        new AppSettings { CaptionSharing = CaptionSharingMode.Receive }.Save(settingsPath);
        int liveCaptionsBefore = CountLiveCaptions();
        string? status = null;
        var steps = new List<string>();
        try
        {
            RunOnUiThread(() =>
            {
                var app = new global::LiveCaptionsUpgrade.App();
                app.InitializeComponent();

                // Once started up (sharing starts in the background), use it like a person would, then exit.
                var timer = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Background, (sender, _) =>
                {
                    ((DispatcherTimer)sender!).Stop();
                    try
                    {
                        status = app.SharingStatus;
                        steps.Add("status");

                        app.OpenPairing();
                        DoEvents();
                        steps.Add("pairing window");

                        app.OpenSettings();
                        DoEvents();
                        steps.Add("settings window");

                        app.ToggleClickThrough();
                        app.ToggleClickThrough();
                        DoEvents();
                        steps.Add("quick toggles");

                        // Captions arriving from the other computer, as the network code delivers them.
                        var receive = typeof(global::LiveCaptionsUpgrade.App).GetMethod("OnCaptionsReceived", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        receive.Invoke(app, new object[] { new List<CaptionLine> { new("Hello, this is Anna from billing.", DateTimeOffset.Now) }, "How can I" });
                        receive.Invoke(app, new object[] { new List<CaptionLine> { new("How can I help?", DateTimeOffset.Now) }, string.Empty });
                        DoEvents();
                        steps.Add("shared captions");

                        app.ToggleOverlayVisible();
                        app.ToggleOverlayVisible();
                        DoEvents();
                        steps.Add("hide and show");
                    }
                    catch (Exception e)
                    {
                        steps.Add("error: " + e);
                    }
                    finally
                    {
                        app.ExitApp();
                    }
                }, Dispatcher.CurrentDispatcher);

                app.Run();
            });
        }
        finally
        {
            if (savedSettings is null)
            {
                File.Delete(settingsPath);
            }
            else
            {
                File.WriteAllText(settingsPath, savedSettings);
            }
        }

        Assert.Equal(new[] { "status", "pairing window", "settings window", "quick toggles", "shared captions", "hide and show" }, steps);
        Assert.Equal("Not paired with another computer yet", status);

        // Showing captions from another computer never involves Live Captions on this one.
        Thread.Sleep(1500);
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
        })
        {
            // If the app hangs (e.g. on an error message box), don't keep the test run alive.
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(Timeout))
        {
            throw new TimeoutException("The app didn't finish in time; it may be showing an error message.");
        }

        if (error is not null)
        {
            throw new Exception("The app failed: " + error, error);
        }
    }
}
