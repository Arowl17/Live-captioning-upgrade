using System;
using System.Diagnostics;
using System.Globalization;

namespace LiveCaptionsUpgrade;

/// <summary>
/// Adds up what reading Live Captions costs, for a line in the log every few minutes: how much processor time
/// this app and Live Captions used, and how long reading the captions took. Use from one thread.
/// </summary>
internal sealed class PerformanceCounters
{
    private readonly Stopwatch _since = Stopwatch.StartNew();
    private TimeSpan _appCpu = AppCpu();
    private TimeSpan? _liveCaptionsCpu;
    private int _liveCaptionsId;
    private long _reads;
    private TimeSpan _readTime;
    private TimeSpan _slowestRead;
    private int _longestText;

    /// <summary>How often the text was different from the read before.</summary>
    public int Changes { get; set; }

    /// <summary>True once <see cref="Report"/> has been called.</summary>
    public bool Logged { get; private set; }

    /// <summary>Time since the last <see cref="Report"/>.</summary>
    public TimeSpan Elapsed => _since.Elapsed;

    public void CountRead(TimeSpan duration, int textLength)
    {
        _reads++;
        _readTime += duration;
        _slowestRead = duration > _slowestRead ? duration : _slowestRead;
        _longestText = Math.Max(_longestText, textLength);
    }

    /// <summary>Describes the time since the last report, and starts counting afresh.</summary>
    public string Report(int liveCaptionsId)
    {
        var elapsed = _since.Elapsed;
        var appCpu = AppCpu();
        var liveCaptionsCpu = CpuOf(liveCaptionsId);

        // (Of one processor core: 100% is one core kept busy the whole time.)
        string app = Percent(appCpu - _appCpu, elapsed);
        string liveCaptions = liveCaptionsCpu is { } now && _liveCaptionsCpu is { } before && liveCaptionsId == _liveCaptionsId
            ? Percent(now - before, elapsed)
            : "?";
        double averageRead = _reads > 0 ? _readTime.TotalMilliseconds / _reads : 0;
        string report = string.Create(
            CultureInfo.InvariantCulture,
            $"Performance, last {elapsed.TotalMinutes:0.#} min: this app used {app} of a processor core, Live Captions {liveCaptions}; "
            + $"{_reads} reads of the captions ({averageRead:0.00} ms each, slowest {_slowestRead.TotalMilliseconds:0.#} ms, "
            + $"up to {_longestText} characters), {Changes} with new text");

        _since.Restart();
        _appCpu = appCpu;
        _liveCaptionsCpu = liveCaptionsCpu;
        _liveCaptionsId = liveCaptionsId;
        _reads = 0;
        _readTime = TimeSpan.Zero;
        _slowestRead = TimeSpan.Zero;
        _longestText = 0;
        Changes = 0;
        Logged = true;
        return report;
    }

    private static string Percent(TimeSpan cpu, TimeSpan elapsed) =>
        elapsed > TimeSpan.Zero ? (100 * cpu.TotalMilliseconds / elapsed.TotalMilliseconds).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "?";

    private static TimeSpan AppCpu()
    {
        using var process = Process.GetCurrentProcess();
        return process.TotalProcessorTime;
    }

    private static TimeSpan? CpuOf(int processId)
    {
        if (processId == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.TotalProcessorTime;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }
}
