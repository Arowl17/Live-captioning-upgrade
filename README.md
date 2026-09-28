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
- **Caption sharing between two computers**: a Windows 11 PC runs Live Captions and sends the captions over
  your home network to another computer, such as a Windows 10 work laptop without Live Captions, which shows
  them in the same caption bar. See [Caption sharing](#caption-sharing-two-computers).

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
- Reading out an address or a number (phone, card, zip code), Live Captions splits it into pieces and keeps
  rewriting it: "Seven." "Seven, seven." "7750.", or "Riverside, Texas seven." becoming "Riverside TX 75231 in the
  main." While a number is still being read out, it stays in the live text, and it goes into the history once, in
  its final form, when something other than a number follows (or after a pause). If a sentence was already shown
  when Live Captions rewrites it, the rewritten one is shown once, and the sentences around it aren't shown again.
- "Mr. Smith", "John A. Smith" and "3 p.m. today" don't split sentences.

These rules are checked by a simulation test that replays thousands of calls the way Live Captions shows
them (words arriving in bursts, corrections, late punctuation, merged sentences, sentences ended too soon and
rewritten, numbers read out digit by digit, text briefly going blank, old lines scrolling away) and verifies that every word comes out exactly
once, or for rewritten sentences, that nothing is lost or shown again.

## Requirements

- Windows 11 version 22H2 or later on the computer that runs Live Captions (Live Captions is part of Windows).
  A computer that only shows captions from another one can run Windows 10.
- Nothing else: the `.exe` includes .NET.

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

To build a single `.exe` that runs without installing anything:

```powershell
dotnet publish src/LiveCaptionsUpgrade.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishReadyToRun=true -o publish
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

- **Select text** with the mouse to copy it (double-click selects a word, handy for an order number), then
  press **Ctrl+C** or right-click → **Copy**. While you're selecting, the captions hold still; after copying
  (or clicking elsewhere) they carry on following the conversation.
- **Move** the overlay by dragging the grip on its left side (it appears when the mouse is over the overlay) or
  any empty space. **Drag any edge or corner** to resize it (the cursor changes when you're close enough to an
  edge). It doesn't snap to the screen edges like normal windows, and can't be made larger than the screen.
- **Scroll up** with the mouse wheel over the captions to read earlier text. While you're scrolled up the
  text stays still; scroll back down or click **Back to live** to follow the conversation again.
- **Ctrl+Alt+H** hides and shows the captions from any app. Captions keep being collected while hidden,
  so you can scroll back to anything said in the meantime.
- **Right-click** the overlay, or the tray icon in the notification area, for options:
  - *Copy*: copies the selected text
  - *Show captions*: same as the shortcut
  - *Lock overlay (clicks pass through)*: once locked, the tray icon is the way back to the menu. Scrolling
    doesn't work while locked.
  - *Show original Live Captions window*
  - *Caption sharing*: off, send or show, the connection status, and *Pair with a computer…*
  - *Settings…*
  - *Open transcripts folder*: by default `Documents\LiveCaptionsUpgrade\Transcripts`
  - *Exit*: saves the last unfinished sentence and restores the Live Captions window.

The *Show original Live Captions window* option is how you reach Live Captions' own settings, for example
to change the language or turn on microphone audio. Untick it to hide Live Captions again.

## Caption sharing (two computers)

For when the call happens on one computer but Live Captions is on another. For example, the call plays on a
Windows 10 work laptop (which has no Live Captions), and its sound reaches a Windows 11 PC (with an app like
AudioRelay). The PC turns the call into captions, and they show up in the caption bar on the laptop.

```
Work laptop ──audio (AudioRelay)──▶ PC: Live Captions ──▶ Live Captions Upgrade ──captions──▶ Work laptop: caption bar
```

Set up once:

1. Run Live Captions Upgrade on both computers. They must be on the same network.
2. On the PC: right-click the captions (or the tray icon) → **Caption sharing** → **Send captions to another
   computer**. On the laptop: → **Show captions from another computer**.
   The first time, the app asks to allow itself through Windows Firewall. Windows then asks for permission
   (on a work computer: the administrator password).
3. A pairing window opens on both. The other computer appears in the list (or type the IP address the other
   window shows). Click **Pair**, check that both screens show the same 6-digit code, and click
   **The codes match** on both.

From then on they reconnect by themselves, also after restarts. While sending, the PC's own caption bar is hidden
(the shortcut still shows it). The laptop has the same caption bar as usual: scroll back, copy, shortcut,
settings and its own transcripts. If the laptop connects in the middle of a call, or the connection drops for a
while, it fills in what was said in the meantime (up to an hour) without repeating anything. If Live Captions has
a problem on the PC, the laptop's caption bar says so.

Only the paired computer can connect, both check each other's identity, and the captions are encrypted (TLS).
Nothing leaves your network. The connection uses TCP port 47820, and computers find each other with UDP
broadcasts on port 47821. **Forget** under **Settings → Caption sharing** unpairs both computers (if the other one
is off at the time, it finds out the next time it tries to connect). If the connection goes silent (Wi-Fi drops,
a computer sleeps), both notice within about 10 seconds and reconnect when they can.

## Settings

Change settings from **Settings…** in the right-click or tray menu; they apply immediately.
They are stored in `%APPDATA%\LiveCaptionsUpgrade\settings.json`. If you edit that file by hand, restart the app.
The paired computer is in `pairing.json` and this computer's encrypted identity in `identity.bin`, in the same
folder. A log for troubleshooting is in `%LOCALAPPDATA%\LiveCaptionsUpgrade\logs`.

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
| `CaptionSharing` | `Off` | `Off`, `Send` (to the paired computer) or `Receive` (show the paired computer's captions) |
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
  Sharing/                          Caption sharing between two computers
    SharingController.cs              Modes, reconnecting, status messages
    SharingHost.cs, PeerConnection.cs Encrypted connections (TLS with pinned certificates) and pairing
    PairingSession.cs, PairingCode.cs 6-digit code comparison (commit/reveal, like Bluetooth)
    CaptionFeed.cs                    Recent sentences and live text, sent in order without repeats
    DiscoveryService.cs               Finding the other computer with UDP broadcasts
src/LiveCaptionsUpgrade.App/      Windows app (WPF)
  LiveCaptionsReader.cs             Finds/launches Live Captions and reads its text via UI Automation
  CaptionService.cs                 Background polling loop
  OverlayWindow.xaml(.cs)           The caption bar, with scroll-back
  SettingsWindow.xaml(.cs)          Settings window
  PairingWindow.xaml(.cs)           Pairing with the other computer
  SharingSupport.cs                 Identity storage (DPAPI), firewall rule, log file
  GlobalHotkey.cs                   System-wide show/hide shortcut
  TrayIcon.cs                       Notification-area icon and options menu
tests/LiveCaptionsUpgrade.Core.Tests/
tests/LiveCaptionsUpgrade.App.Tests/   Opens every window (Windows only)
```

Run the tests with `dotnet test tests/LiveCaptionsUpgrade.Core.Tests`. They also run on Linux and macOS; the
caption sharing tests pair and connect real encrypted connections between simulated computers on the same
machine, including a connection that goes silent and one that sends garbage.
`tests/LiveCaptionsUpgrade.App.Tests` opens the app's windows and needs Windows.

## Limitations

- **Live Captions needs Windows 11 22H2+**, and only the languages it supports work. A Windows 10 computer can
  only show captions shared from a Windows 11 one.
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
