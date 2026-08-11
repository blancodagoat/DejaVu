<div align="center">

# DejaVu

**Instant replay for Windows that stays out of your way.**

Tray-only · rolling buffer · hardware encoding · crash-safe · no uploads · no editor · no telemetry

</div>

---

DejaVu quietly keeps the last few minutes of your screen in a rolling buffer.
Press **Alt+F10** and that moment is saved as an MP4 — like ShadowPlay, without the
driver suite, and built around the ways replay tools usually let people down:

- **It is always on.** Buffering starts the instant the app launches, the app starts
  with Windows by default, and the corner dot tells you the buffer is rolling. If a
  capture source dies, DejaVu falls back to the main display and keeps going — it
  never switches itself off silently.
- **A crash loses nothing.** The buffer is written as crash-safe fragmented MP4 in
  fixed segments on disk. If the PC hard-resets or the app is killed, the next launch
  stitches what survived into a clip automatically.
- **Clips are named for the game.** The app in the foreground when you hit the hotkey
  becomes the filename: `eldenring_2026-08-11_224513.mp4`.

## Quick start

1. Build and run `DejaVu.exe`. An icon appears in the tray; a small red dot in the
   bottom-right corner shows the buffer is rolling.
2. Something worth keeping happens.
3. Press **Alt+F10**. The replay lands in `Videos\DejaVu`.

## How it works

The capture target is recorded in one-minute hardware-encoded H.264 segments
(NVENC/AMF/QuickSync, picked automatically — near-zero CPU). Segments older than the
buffer window are deleted as they age out, so disk use stays flat. Saving stitches
segments together without re-encoding: fast, lossless, standard MP4 out.

Encoding uses constant-quality rate control rather than a fixed bitrate — action gets
the bits, menus and static screens cost almost nothing.

Everything is configured from the tray menu:

| | |
|---|---|
| **Capture** | Auto (follows the active window's display), a pinned monitor, or one specific window |
| **Buffer length** | 5 / 10 / 15 / 20 / 25 minutes |
| **Quality** | Low / Medium / High (constant quality, size varies with content) |
| **Frame rate** | 30 / 60 fps |
| **Clip folder cap** | Off / 10 / 25 / 50 GB — oldest clips roll off; the newest is never touched |
| **System audio** | recorded into the replay (mic is never captured) |
| **Corner indicator** | the dot; excluded from recordings, click-through |

The save hotkey is rebindable in `%APPDATA%\DejaVu\config.json` (e.g. `"Ctrl+Shift+F9"`).
Window picks last for the session; displays and auto persist.

## Privacy

The buffer lives in `%LOCALAPPDATA%\DejaVu\buffer` and is wiped after a clean exit
(after a crash it becomes the recovered clip). Nothing leaves your machine. Pause
buffering from the tray menu any time; the dot turns grey.

## Building

```
dotnet build src/DejaVu/DejaVu.csproj
dotnet run --project tests/DejaVu.Tests            # unit tests
dotnet run --project tests/DejaVu.Tests -- smoke   # + live capture/crash-recovery/concat check
```

The smoke test needs an active desktop (it records the real screen) and skips itself
if the screen is locked or asleep.

## License

[MIT](LICENSE)
