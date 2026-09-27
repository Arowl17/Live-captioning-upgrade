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

    private readonly AppSettings _settings;
    private readonly LiveCaptionsReader _reader = new();
    private readonly CaptionTracker _tracker;
    private readonly TranscriptWriter? _transcript;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _hideLiveCaptions;
    private DateTime _attachedAt;
    private bool _setupHintShown;
    private bool _dockedNoticeShown;

    public CaptionService(AppSettings settings)
    {
        _settings = settings;
        _tracker = new CaptionTracker(TimeSpan.FromMilliseconds(settings.IdleFinalizeMs));
        _transcript = settings.SaveTranscript ? new TranscriptWriter(settings.ResolveTranscriptFolder()) : null;
        _hideLiveCaptions = settings.HideLiveCaptionsWindow;
    }

    public event Action<CaptionUpdate>? CaptionsUpdated;

    /// <summary>Raised with a message to show the user, or null once captions are flowing normally.</summary>
    public event Action<string?>? StatusChanged;

    /// <summary>Raised with a one-off tip for the user, e.g. how to stop Live Captions reserving screen space.</summary>
    public event Action<string>? Notice;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>Hides or shows the original Live Captions window. Applied on the next poll.</summary>
    public void SetLiveCaptionsHidden(bool hidden) => _hideLiveCaptions = hidden;

    /// <summary>Stops polling, saves any unfinished sentence and gives Live Captions its window back.</summary>
    public async Task StopAsync()
    {
        if (_cts is null || _loop is null)
        {
            return;
        }

        _cts.Cancel();
        await _loop.ConfigureAwait(false);
        _cts.Dispose();
        _cts = null;
        _loop = null;

        foreach (string sentence in _tracker.Flush())
        {
            _transcript?.Append(sentence, DateTimeOffset.Now);
        }

        _transcript?.Dispose();
        _reader.Show();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(_settings.PollIntervalMs);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_reader.IsAttached)
                {
                    StatusChanged?.Invoke("Connecting to Windows Live Captions…");
                    if (!await _reader.AttachAsync(ct))
                    {
                        StatusChanged?.Invoke("Waiting for Windows Live Captions to start (finish its setup if it is showing one)…");
                        await Task.Delay(RetryDelay, ct);
                        continue;
                    }

                    _tracker.Reset();
                    _attachedAt = DateTime.UtcNow;
                    _setupHintShown = false;
                    StatusChanged?.Invoke(null);
                }

                string? text = _reader.ReadText();
                UpdateLiveCaptionsVisibility();
                if (text is not null)
                {
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
                StatusChanged?.Invoke(ex.Message);
                break;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke("Problem reading Live Captions: " + ex.Message);
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
                StatusChanged?.Invoke("Live Captions is open but not captioning yet. If it is showing a setup screen, finish it there.");
            }

            return;
        }

        _reader.Hide(_settings.HideMethod);
        StatusChanged?.Invoke(null);
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
        if (update.NewSentences.Count == 0 && !update.TextChanged)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        foreach (string sentence in update.NewSentences)
        {
            _transcript?.Append(sentence, now);
        }

        CaptionsUpdated?.Invoke(update);
    }
}
