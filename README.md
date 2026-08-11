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
| **Frame rate** | 30 / 60 / 90 / 120 / 144 / 165 / 240 fps — offered up to what your displays can show |
| **Clip folder cap** | Off / 10 / 25 / 50 GB — oldest clips roll off; the newest is never touched |
| **System audio** | recorded into the replay — with Discord's notifications and voice chat kept out of the mix (see below). The mic is never captured |
| **Corner indicator** | the dot; excluded from recordings, click-through |

The save hotkey is rebindable from the tray ("Change save hotkey…").
Window picks last for the session; displays and auto persist.

## Discord stays out of your clips

Replay audio is captured through Windows' process-loopback device with the Discord
process tree excluded — voice chat, notification pings, all of it — so a shared clip
never leaks a private conversation. The excluded apps are configurable as `audioExclude`
in `config.json` (default: Discord, DiscordCanary, DiscordPTB, Vesktop); an empty list
records the full system mix.

## Why not ShadowPlay or OBS?

Both are good at what DejaVu doesn't do. For the one job of an instant-replay buffer,
each fails in a way users report constantly:

| | ShadowPlay / NVIDIA App | OBS replay buffer | DejaVu |
|---|---|---|---|
| Stays on | Turns itself off silently — per-game, after driver updates, on alt-tab | You must remember to start the buffer every session | Always on from launch and boot; self-heals; the dot says so |
| Crash | RAM buffer — a crash loses everything | MP4 output corrupts on crash unless you remux MKV | Disk segments survive; next launch saves them automatically |
| Feedback | Save sometimes silently does nothing | No native "replay saved" toast at all | Balloon on every save; click it to reveal the file |
| Filenames | Per-game folders | Manual strftime setup or clips overwrite | `eldenring_2026-08-11_224513.mp4`, automatic |
| Voice privacy | Discord audio lands in the mix | Only with per-app audio sources configured by hand | Discord excluded by default, zero setup |
| Needs | The NVIDIA App + overlay stack, NVIDIA GPU only | The whole OBS install and a scene collection | One exe, any GPU with a hardware encoder |
| Buffer lives in | RAM | RAM (capped at 75% of physical) | Bounded disk ring — nothing held in RAM |

The claims about ShadowPlay and OBS reflect their designs and widely-reported user
complaints; both projects evolve, so verify against current versions.

While buffering, DejaVu's measured working set is ~120 MB at 1440p/30 fps Low and
~190 MB at 1440p/60 fps High — the encoder pipeline is the cost, and shrinking it is an
active work item. The app itself is a ~160 KB framework-dependent exe with no services,
no overlay, and no account.

## Old PCs are the scope, not an afterthought

DejaVu is built to run on roughly 2015-era machines: one tray exe, ~25 MB of RAM, and
near-zero CPU when a hardware H.264 encoder exists (NVENC since 2012, QuickSync since
2011, AMD VCE since 2013 — without one, encoding falls back to software and costs CPU).
Disk use is bounded by the buffer window — roughly 300 MB–1 GB depending on length and
quality — and writes stay light on both SSDs and hard drives. The floor is Windows 10
2004; that same build provides the process-loopback audio device, so Discord exclusion
works everywhere the app runs. This scope is also why the buffer lives on disk rather
than RAM: on an older 8 GB machine, a half-gigabyte of RAM is far more precious than a
half-gigabyte of disk — and the disk ring is what makes crash recovery possible at all.

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
