<div align="center">

# DejaVu

**Instant replay for Windows that stays out of your way.**

Tray-only · rolling buffer · hardware encoding · no uploads · no editor · no telemetry

</div>

---

DejaVu quietly keeps the last few minutes of your primary display in a rolling buffer.
Press **Alt+F10** and that moment is saved as an MP4 — like ShadowPlay, without the
driver suite.

## Quick start

1. Build and run `DejaVu.exe`. An icon appears in the tray; a small red dot in the
   bottom-right corner shows the buffer is rolling.
2. Something worth keeping happens.
3. Press **Alt+F10**. The replay lands in `Videos\DejaVu`.

## How it works

The screen is recorded in one-minute hardware-encoded H.264 segments
(NVENC/AMF/QuickSync, picked automatically — near-zero CPU). Segments older than the
buffer window are deleted as they age out, so disk use stays flat: roughly 300 MB for
5 minutes of 1080p60 at High quality. Saving stitches the segments together without
re-encoding, so it is fast and lossless.

Everything is configured from the tray menu:

| | |
|---|---|
| **Buffer length** | 5 / 10 / 15 / 20 / 25 minutes |
| **Quality** | Low (8), Medium (15), High (25 Mbps) |
| **Frame rate** | 30 / 60 fps |
| **System audio** | recorded into the replay (mic is never captured) |
| **Corner indicator** | the dot; excluded from recordings, click-through |

The save hotkey is rebindable in `%APPDATA%\DejaVu\config.json` (e.g. `"Ctrl+Shift+F9"`).

## Privacy

The buffer lives in `%LOCALAPPDATA%\DejaVu\buffer` and is wiped on exit and on
startup. Nothing leaves your machine. Pause buffering from the tray menu any time;
the dot turns grey.

## Building

```
dotnet build src/DejaVu/DejaVu.csproj
dotnet run --project tests/DejaVu.Tests            # unit tests
dotnet run --project tests/DejaVu.Tests -- smoke   # + live 10 s capture/concat check
```

## License

[MIT](LICENSE)
