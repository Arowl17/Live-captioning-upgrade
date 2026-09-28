using System;
using System.Collections.Generic;
using System.IO;
using LiveCaptionsUpgrade.Core;

namespace LiveCaptionsUpgrade;

/// <summary>Saves finished sentences to the transcript file when that's turned on. Thread-safe.</summary>
internal sealed class TranscriptRecorder : IDisposable
{
    private readonly object _lock = new();
    private TranscriptWriter? _transcript;
    private string? _folder;

    /// <summary>Raised (on any thread) with a message for the user when saving fails.</summary>
    public event Action<string>? Notice;

    /// <summary>Applies changed settings: transcript on or off, and the folder.</summary>
    public void ApplySettings(AppSettings settings)
    {
        lock (_lock)
        {
            string folder = settings.ResolveTranscriptFolder();
            if (!settings.SaveTranscript || !string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase))
            {
                _transcript?.Dispose();
                _transcript = null;
                _folder = null;
            }

            if (settings.SaveTranscript && _transcript is null)
            {
                _transcript = new TranscriptWriter(folder);
                _folder = folder;
            }
        }
    }

    public void Write(IReadOnlyList<string> sentences)
    {
        if (sentences.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var lines = new List<CaptionLine>(sentences.Count);
        foreach (string sentence in sentences)
        {
            lines.Add(new CaptionLine(sentence, now));
        }

        Write(lines);
    }

    public void Write(IReadOnlyList<CaptionLine> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        string? error = null;
        lock (_lock)
        {
            if (_transcript is null)
            {
                return;
            }

            try
            {
                foreach (var line in lines)
                {
                    _transcript.Append(line.Text, line.Time);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Stop trying for this session (it would fail on every sentence); captions carry on regardless.
                _transcript.Dispose();
                _transcript = null;
                error = ex.Message;
            }
        }

        if (error is not null)
        {
            Notice?.Invoke("Couldn't save the transcript, so saving is paused. Captions still work. "
                + "Check the transcript folder in Settings. " + error);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _transcript?.Dispose();
            _transcript = null;
            _folder = null;
        }
    }
}
