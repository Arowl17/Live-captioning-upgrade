# Live Captions Upgrade

A better caption bar for Windows 11, built **on top of the built-in Live Captions**.

Windows Live Captions keeps doing the speech recognition (on-device, private, and it hears
everything your PC plays — videos, calls, games — plus your microphone if you enable it).
This app reads Live Captions' text as it appears and gives you:

- **A movable, resizable overlay**: pick the font, size, colours and background opacity, and place it anywhere,
  even over full-screen video.
- **Scroll back with the mouse wheel** through the last 10 minutes (adjustable). Older text is deleted from memory automatically.
- **Show/hide shortcut** (Ctrl+Alt+H by default) to get the captions out of the way and back again.
- **A settings window** for the look, the shortcut, scroll-back length, transcripts and how Live Captions is hidden.
- **Automatic transcripts**: every finished sentence is saved, with a timestamp, to a text file.
- **Click-through mode**: lock the overlay so clicks go to the window underneath.
- **Only this app's captions are on screen.** The original Live Captions window keeps running but is
  invisible: it has no taskbar button, doesn't appear in Alt+Tab, and clicks pass straight through it.

## How it works

```
Audio ──▶ Windows Live Captions ──(UI Automation)──▶ LiveCaptionsReader ──▶ CaptionTracker ──▶ Overlay
          (speech → text)          reads its text     polls ~7×/second       finds finished      + transcript file
                                                                              sentences
```

Live Captions exposes its caption text through UI Automation, the accessibility interface screen
readers use. The app reads the text element named `CaptionsTextBlock` in the `LiveCaptionsDesktopWindow` window.

That text is a rolling block of the last few lines, and Live Captions keeps revising it:
words are appended, recent words get corrected, and old lines scroll off the top.
`CaptionTracker` turns this into a clean stream of sentences:

- A sentence is **final** once a later sentence has started, or once it ends in punctuation and the text
  has been unchanged for `IdleFinalizeMs`.
- To avoid emitting anything twice as the text scrolls, the tracker finds recently committed sentences
  in the new text. The match is fuzzy, so a small correction such as "their" → "they're" still counts as the same sentence.
  Numbers must match exactly, so a customer correcting "555 0134" to "555 0135" is never dropped as a repeat.
- A sentence longer than the visible text keeps its beginning: the tracker remembers the unfinished sentence
  and stitches its start back on once that scrolls away.
- If Live Captions changes its mind about where a sentence ends after it was saved ("I called yesterday."
  becoming "I called yesterday, and they said no."), only the new words are added ("and they said no.").
- "Mr. Smith", "John A. Smith" and "3 p.m. today" don't split sentences.

These rules are checked by a simulation test that replays thousands of calls the way Live Captions shows
them (words arriving in bursts, corrections, late punctuation, merged sentences, text briefly going blank,
old lines scrolling away) and verifies that every word comes out exactly once.

## Requirements

- Windows 11 version 22H2 or later (Live Captions is part of Windows)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0). Windows offers to download it the first time you run the app if it is missing.

## First-time setup

1. Open Live Captions once on its own (**Win + Ctrl + L**) and finish its setup. On first run it asks
   you to download the speech files for your language.
2. Optional: in Live Captions ⚙ → **Preferences**, turn on **Include microphone audio** to caption your own voice too.
3. In Live Captions ⚙ → **Position**, choose **Floating on screen**. When docked to the top or bottom,
   Live Captions reserves that strip of the screen, and Windows keeps it empty even while Live Captions is
   invisible. The app shows a reminder if it finds Live Captions docked.

## Run it

**Download:** every push is built by GitHub Actions. Open the repository's **Actions** tab, pick the latest
**Build** run, and download `LiveCaptionsUpgrade-win-x64`.

**Build it yourself** (requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)):

```powershell
dotnet run --project src/LiveCaptionsUpgrade.App
```

To build a single `.exe`:

```powershell
dotnet publish src/LiveCaptionsUpgrade.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

The app starts Live Captions for you if it isn't already running, and hides it as soon as its captions
appear. Until then, for example while Live Captions shows its first-run setup, it stays visible so you can finish the setup.

### How Live Captions is hidden

Live Captions is not minimised, because a minimised window might stop updating its captions. Instead it stays open and is:

- made fully transparent and click-through,
- moved past the edge of the desktop,
- removed from the taskbar and Alt+Tab.

The app checks this every poll and re-applies it if Live Captions undoes it. When you exit, the Live Captions
window is put back exactly where it was.

If captions ever stop updating while Live Captions is hidden, go to **Settings → Windows Live Captions →
How to hide it** and choose **Minimize**.

## Using it

- **Drag** the overlay to move it; drag the bottom-right corner to resize.
- **Scroll up** with the mouse wheel over the captions to read earlier text. While you're scrolled up the
  text stays still; scroll back down or click **Back to live** to follow the conversation again.
- **Ctrl+Alt+H** hides and shows the captions from any app. Captions keep being collected while hidden,
  so you can scroll back to anything said in the meantime.
- **Right-click** the overlay, or the tray icon in the notification area, for options:
  - *Show captions*: same as the shortcut
  - *Lock overlay (clicks pass through)*: once locked, the tray icon is the way back to the menu. Scrolling
    doesn't work while locked.
  - *Show original Live Captions window*
  - *Settings…*
  - *Open transcripts folder*: by default `Documents\LiveCaptionsUpgrade\Transcripts`
  - *Exit*: saves the last unfinished sentence and restores the Live Captions window.

The *Show original Live Captions window* option is how you reach Live Captions' own settings, for example
to change the language or turn on microphone audio. Untick it to hide Live Captions again.

## Settings

Change settings from **Settings…** in the right-click or tray menu; they apply immediately.
They are stored in `%APPDATA%\LiveCaptionsUpgrade\settings.json`. If you edit that file by hand, restart the app.

| Setting | Default | Meaning |
|---|---|---|
| `FontFamily` / `FontSize` | `Segoe UI` / `26` | Caption font |
| `TextColor` | `#FFFFFF` | Sentence being spoken |
| `HistoryTextColor` | `#B8B8B8` | Finished sentences above it |
| `BackgroundColor` / `BackgroundOpacity` | `#000000` / `0.7` | Overlay background (opacity 0–1) |
| `ScrollbackMinutes` | `10` | How far back you can scroll (1–60 minutes) |
| `ToggleHotkey` | `Ctrl+Alt+H` | Show/hide shortcut. Empty turns it off. |
| `AlwaysOnTop` | `true` | Keep the captions above other windows |
| `ClickThrough` | `false` | Overlay locked, clicks pass through |
| `HideLiveCaptionsWindow` | `true` | Hide the original Live Captions window while running |
| `HideMethod` | `Invisible` | `Invisible` (transparent and off screen, keeps running) or `Minimize` (fallback) |
| `SaveTranscript` | `true` | Write finished sentences to a transcript file |
| `TranscriptFolder` | *(empty)* | Transcript location. Empty means `Documents\LiveCaptionsUpgrade\Transcripts`. `%VARIABLES%` are expanded. |
| `PollIntervalMs` | `150` | How often Live Captions is read (file only) |
| `IdleFinalizeMs` | `1200` | Pause, in ms, after which a punctuated last sentence counts as finished (file only) |

## Project layout

```
src/LiveCaptionsUpgrade.Core/     Platform-independent logic (unit-tested)
  CaptionTracker.cs                 Rolling caption text → finished sentences
  SentenceSplitter.cs               Sentence splitting (including CJK punctuation)
  TextSimilarity.cs                 Fuzzy matching for revised sentences
  TranscriptWriter.cs               Timestamped transcript files
  Scrollback.cs                     Which scroll-back lines are old enough to delete
  Hotkey.cs                         Shortcut parsing and formatting
  AppSettings.cs                    settings.json
src/LiveCaptionsUpgrade.App/      Windows app (WPF)
  LiveCaptionsReader.cs             Finds/launches Live Captions and reads its text via UI Automation
  CaptionService.cs                 Background polling loop
  OverlayWindow.xaml(.cs)           The caption bar, with scroll-back
  SettingsWindow.xaml(.cs)          Settings window
  GlobalHotkey.cs                   System-wide show/hide shortcut
  TrayIcon.cs                       Notification-area icon and options menu
tests/LiveCaptionsUpgrade.Core.Tests/
```

Run the tests with `dotnet test`. They also run on Linux and macOS.

## Limitations

- **Windows 11 22H2+ only**, and only the languages Live Captions supports.
- The app depends on Live Captions' internal window and element names (`LiveCaptionsDesktopWindow`,
  `CaptionsTextBlock`). A future Windows update could rename them; the constants are at the top of
  `LiveCaptionsReader.cs`.
- If Live Captions rewrites a sentence heavily *after* it was saved (more than a word or two), the transcript
  can contain both versions.
- If this app is force-closed (e.g. from Task Manager), Live Captions stays invisible until you start
  this app again, which picks it up and gives it back when you exit normally. Or press **Win + Ctrl + L**
  twice to close and reopen Live Captions.
- If the transcript can't be written (e.g. the folder is on a disconnected drive), saving pauses with a
  notification; captions keep working.
