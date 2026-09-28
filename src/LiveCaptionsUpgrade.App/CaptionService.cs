using System;
using System.Threading;
using System.Threading.Tasks;
using LiveCaptionsUpgrade.Core;

namespace LiveCaptionsUpgrade;

/// <summary>
/// Polls Live Captions on a background thread, turns its text into finished sentences and
/// writes them to the transcript. Events are raised on the background thread.
/// </summary>
internal sealed class CaptionService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    // If the caption text hasn't appeared by now, Live Captions is probably showing its first-run setup.
    private static readonly TimeSpan SetupHintDelay = TimeSpan.FromSeconds(5);

    private readonly LiveCaptionsReader _reader = new();
    private readonly CaptionTracker _tracker;
    private readonly TranscriptRecorder _transcript;
    private readonly int _pollIntervalMs;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _hideLiveCaptions;
    private volatile LiveCaptionsHideMethod _hideMethod;
    private bool _wasAttached;
    private DateTime _attachedAt;
    private bool _setupHintShown;
    private bool _dockedNoticeShown;
    private string? _status;
    private string _lastPending = string.Empty;

    public CaptionService(AppSettings settings, TranscriptRecorder transcript)
    {
        _transcript = transcript;
        _tracker = new CaptionTracker(TimeSpan.FromMilliseconds(settings.IdleFinalizeMs));
        _pollIntervalMs = settings.PollIntervalMs;
        ApplySettings(settings);
    }

    public event Action<CaptionUpdate>? CaptionsUpdated;

    /// <summary>Raised with a message to show the user, or null once captions are flowing normally.</summary>
    public event Action<string?>? StatusChanged;

    /// <summary>Raised with a one-off message for the user, e.g. how to stop Live Captions reserving screen space.</summary>
    public event Action<string>? Notice;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>Hides or shows the original Live Captions window. Applied on the next poll.</summary>
    public void SetLiveCaptionsHidden(bool hidden) => _hideLiveCaptions = hidden;

    /// <summary>Applies changed settings: how Live Captions is hidden.</summary>
    public void ApplySettings(AppSettings settings)
    {
        _hideMethod = settings.HideMethod;
        _hideLiveCaptions = settings.HideLiveCaptionsWindow;
    }

    /// <summary>Stops polling, passes on any unfinished sentence and gives Live Captions its window back.</summary>
    public async Task StopAsync()
    {
        if (_cts is not null && _loop is not null)
        {
            _cts.Cancel();
            await _loop.ConfigureAwait(false);
            _cts.Dispose();
            _cts = null;
            _loop = null;
        }

        Publish(new CaptionUpdate(_tracker.Flush(), string.Empty, TextChanged: true));
        _reader.Show();
    }

    /// <summary>Last-ditch attempt to make Live Captions visible again when the app is crashing. Any thread.</summary>
    public void RestoreLiveCaptionsWindowNow() => _reader.Show();

    private async Task RunAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(_pollIntervalMs);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_reader.IsAttached)
                {
                    if (_wasAttached)
                    {
                        // Live Captions closed or crashed: keep what was being said rather than lose it.
                        _wasAttached = false;
                        var flushed = _tracker.Flush();
                        Publish(new CaptionUpdate(flushed, string.Empty, TextChanged: true));
                    }

                    SetStatus("Connecting to Windows Live Captions…");
                    if (!await _reader.AttachAsync(ct))
                    {
                        SetStatus("Waiting for Windows Live Captions to start (finish its setup if it is showing one)…");
                        await Task.Delay(RetryDelay, ct);
                        continue;
                    }

                    _wasAttached = true;
                    _tracker.Reset();
                    _attachedAt = DateTime.UtcNow;
                    _setupHintShown = false;
                    SetStatus(null);
                }

                string? text = _reader.ReadText();
                UpdateLiveCaptionsVisibility();
                if (text is not null)
                {
                    if (_setupHintShown || _status is not null)
                    {
                        // Reading works again, so any earlier problem message is out of date.
                        _setupHintShown = false;
                        SetStatus(null);
                    }

                    Publish(_tracker.Process(text, DateTimeOffset.Now));
                }

                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (LiveCaptionsUnavailableException ex)
            {
                SetStatus(ex.Message);
                break;
            }
            catch (Exception ex)
            {
                SetStatus("Problem reading Live Captions: " + ex.Message);
                try
                {
                    await Task.Delay(RetryDelay, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void UpdateLiveCaptionsVisibility()
    {
        if (!_hideLiveCaptions)
        {
            _reader.Show();
            return;
        }

        if (_reader.IsHidden)
        {
            if (_reader.HideMethod != _hideMethod)
            {
                // The hiding method was changed in settings: undo the old one; the next poll hides it the new way.
                _reader.Show();
                return;
            }

            _reader.KeepHidden();
            return;
        }

        // Only hide once captions are flowing. Until then Live Captions may be showing its
        // first-run setup (language download), which the user has to be able to see and finish.
        if (!_reader.HasFoundCaptions)
        {
            if (!_setupHintShown && DateTime.UtcNow - _attachedAt > SetupHintDelay)
            {
                _setupHintShown = true;
                SetStatus("Live Captions is open but not captioning yet. If it is showing a setup screen, finish it there.");
            }

            return;
        }

        _reader.Hide(_hideMethod);
        if (_reader.IsDocked && !_dockedNoticeShown)
        {
            _dockedNoticeShown = true;
            // Keep under 255 characters, the limit for tray notifications.
            Notice?.Invoke(
                "Live Captions is docked to the screen edge, so Windows keeps that space empty. To free it: tray icon > "
                + "Show original Live Captions window > Settings > Position > Floating on screen.");
        }
    }

    private void Publish(CaptionUpdate update)
    {
        // Only bother the UI when what it shows changes: often the text changes only in a sentence already
        // emitted (Live Captions correcting it), which leaves the display as it is.
        if (update.NewSentences.Count == 0 && string.Equals(update.Pending, _lastPending, StringComparison.Ordinal))
        {
            return;
        }

        _lastPending = update.Pending;

        // Show the captions first; the transcript must never hold them up.
        CaptionsUpdated?.Invoke(update);
        _transcript.Write(update.NewSentences);
    }

    private void SetStatus(string? status)
    {
        if (status == _status)
        {
            return;
        }

        _status = status;
        StatusChanged?.Invoke(status);
    }
}
