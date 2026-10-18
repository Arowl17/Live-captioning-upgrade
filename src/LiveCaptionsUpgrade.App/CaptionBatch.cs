using System;
using System.Collections.Generic;
using LiveCaptionsUpgrade.Core;

namespace LiveCaptionsUpgrade;

/// <summary>
/// Caption changes waiting to be shown. They can come faster than the window is redrawn (several a second, more
/// on a busy computer), so they are collected and shown in one go: one redraw instead of one per change. Lines a
/// later change replaces are taken back from those collected, or else counted against those already shown.
/// Thread-safe.
/// </summary>
internal sealed class CaptionBatch
{
    private readonly object _lock = new();
    private List<CaptionLine> _lines = new();
    private string _pending = string.Empty;
    private int _replaced;
    private bool _scheduled;

    /// <summary>Adds a change; returns true if it's the first since the batch was last taken (so showing it is due).</summary>
    public bool Add(IReadOnlyList<CaptionLine> lines, string pending, int replaced)
    {
        lock (_lock)
        {
            int collected = Math.Min(replaced, _lines.Count);
            _lines.RemoveRange(_lines.Count - collected, collected);
            _replaced += replaced - collected;
            _lines.AddRange(lines);
            _pending = pending;
            bool first = !_scheduled;
            _scheduled = true;
            return first;
        }
    }

    /// <summary>Everything collected: the new lines, the live text, and how many lines shown before to replace.</summary>
    public (List<CaptionLine> Lines, string Pending, int Replaced) Take()
    {
        lock (_lock)
        {
            var taken = (_lines, _pending, _replaced);
            _lines = new List<CaptionLine>();
            _replaced = 0;
            _scheduled = false;
            return taken;
        }
    }
}
