<div align="center">

<img src="assets/logo.png" width="96" alt="">

# DejaVu

### Your last 5 to 25 minutes, always recorded. One key saves it.

**Like ShadowPlay, except it never turns itself off, survives crashes,<br>and keeps Discord out of your clips.**

`one exe` · `any GPU` · `no account` · `no uploads` · `no telemetry`

<img src="assets/screenshot.png" width="760" alt="The Replay recovered notification: a crashed session's buffer saved as a clip on the next launch">

</div>

---

## The whole app in 10 seconds

1. **A red dot in the corner** means your screen is being buffered. It starts with Windows and stays on.
2. **Something clip-worthy happens.**
3. **Press <kbd>Alt</kbd>+<kbd>F10</kbd>** and `eldenring_2026-08-11_224513.mp4` lands in `Videos\DejaVu`. A short chime plays and a balloon confirms it; clicking the balloon opens the file. The chime matters more than it sounds: Windows hides balloons while a game is fullscreen, so the sound is the confirmation you actually get mid-match.

That's the entire app. There is no main window and nothing to set up; every setting lives in the tray menu. You don't have to wait for the buffer to fill either: press the key ninety seconds after launch and you get a ninety-second clip.

**Get it:** `DejaVu.exe` from the [latest release](https://github.com/blancodagoat/DejaVu/releases/latest), or `scoop bucket add blancodagoat https://github.com/blancodagoat/scoop-bucket` then `scoop install dejavu`. If your browser saved it as `DejaVu (1).exe` next to an old copy, run it anyway: on launch it deletes the stale copies beside it and renames itself back.

---

## Why it doesn't let you down

| The usual pain | DejaVu |
|---|---|
| ShadowPlay turns itself off silently, so you press the key and nothing was recording | Always on; if capture breaks it falls back to the main display, keeps going, and tells you so |
| OBS makes you remember to start the buffer every session | Buffering starts the moment the app launches, including at boot |
| A crash eats your clip, because RAM buffers lose everything | The buffer lives on disk in crash-safe segments; after a crash, the next launch saves what survived |
| Discord pings and voice chat leak into shared clips | Capture a game window and the clip carries that game's audio, nothing else. Monitor capture excludes Discord from the mix by default |
| `Replay_2024_final(3).mp4` filename chaos | Clips are named after the game that was on screen |
| Saved clips eat your disk forever | An optional cap rolls the oldest clips off past 10/25/50 GB, and the newest is never touched |

---

## Everything you can change (all in the tray menu)

| Setting | Options |
|---|---|
| Buffer length | 5 / 10 / 15 / 20 / 25 min |
| Capture | Auto (follows your game), any monitor, or one window |
| Quality | Low / Medium / High. Constant-quality encoding: action gets the bits, menus cost almost nothing |
| Frame rate | 30 to 240 fps, offered up to what your display can actually show |
| System audio | On or off. The mic is never recorded |
| Captured app audio only | On by default. With a window captured, the clip carries that app's audio alone, even when a virtual mixer like SteelSeries Sonar routes the app to its own output device |
| Keep Discord out of clips | Applies to monitor capture; one click puts voice chat back into the mix |
| Save sound | A chime when a clip saves, a low buzz when a save fails. Off if you prefer silence |
| Replays folder | Open it or move it somewhere else, both from the menu |
| Clip folder cap | Off / 10 / 25 / 50 GB |
| Hotkey | Click-to-rebind dialog |
| Corner indicator | Off, the red dot, or the app icon. It sits on the display being recorded, stays above fullscreen games, and never appears in your clips |
| Notify about new versions | Off by default, so the app stays silent on the network. On, it asks GitHub a few times a day; on a scoop install, clicking the update balloon runs the whole update and restarts the app |

> **Admin games:** if the hotkey stops working while a game with anti-cheat has focus, hit *Restart as administrator* in the tray once. That's a Windows rule, not ours.

---

## vs the big two

| | ShadowPlay | OBS Replay Buffer | DejaVu |
|---|---|---|---|
| Stays on | silently dies | manual start | always, and self-heals |
| Crash survival | RAM, all lost | MKV remux dance | auto-recovers on boot |
| "Saved" feedback | sometimes nothing | none natively | balloon, click to open |
| Discord-free audio | no | manual per-app setup | default |
| Install | NVIDIA App + overlay | full OBS + scenes | one exe (~160 KB) |
| GPU | NVIDIA only | any | any with a hardware encoder |

<sub>Based on each tool's design and widely reported user complaints; both evolve, so verify against current versions.</sub>

**Honest numbers:** while buffering 1440p60 at High, DejaVu's measured working set is about 160 MB, most of it the hardware encoder's own working space, and shrinking it further is an active work item. Paused, it's a tray icon. There is no overlay, no background service, and no FPS tax from an in-game UI. On modern GPUs (RTX 40+, RX 7000+, Arc) it records AV1, which gets you the same quality in roughly a third smaller files; everywhere else it falls back to H.264 on its own.

---

<details>
<summary><b>How it works under the hood</b></summary>

<br>

- The capture engine is our own: Windows.Graphics.Capture frames go straight into a Media Foundation hardware encoder (AV1 where the GPU can, H.264 otherwise). Frames never leave the GPU, and there are no third-party components in the app at all.
- Capture never stops. The encoder rotates between one-minute fragmented-MP4 segment files, so a seam costs at most one frame, and killing the power mid-write still leaves playable files. Old segments delete themselves, which keeps disk use flat.
- Saving stitches the segments covering your window into one standard MP4. It's a lossless remux, no re-encode, done in a couple of seconds.
- Segments on disk at launch mean the last session died, so they're stitched into `recovered_*.mp4` automatically and you get a balloon. A recovered clip that won't decode is thrown away rather than handed to you.
- If a capture source delivers zero frames, the buffer drops it for the main display and says so. A sleeping or locked screen just idles until you're back. Repeated hard failures pause it for a minute at a time between retries, and it reports that too; it never stops silently.
- Replay audio matches what you capture. A captured window records that app's audio: through the app's own output device when a virtual mixer (SteelSeries Sonar, per-app output settings) routes it there, through include-mode process loopback otherwise. Monitor capture records the system mix with the `audioExclude` process trees removed (Discord, Canary, PTB, and Vesktop by default). Either way it's encoded to AAC live, muxed per segment, and the route each session took is written to the log.
- Everything the app does gets a line in `%APPDATA%\DejaVu\log.txt`, so a problem in the field leaves a trace instead of a vanished balloon.

</details>

<details>
<summary><b>Runs on old PCs, and that's the point</b></summary>

<br>

DejaVu is built for roughly 2015 machines and up:

- Windows 10 2004 or later. The same build provides the process-loopback audio device, so Discord exclusion works everywhere the app runs.
- Any GPU with a hardware H.264 encoder: NVENC since 2012, QuickSync since 2011, AMD VCE since 2013. Without one, encoding falls back to software and costs CPU.
- The buffer lives on disk instead of RAM by design. On an old 8 GB machine, RAM is what your game needs, and the buffer takes none of it. The disk writes are light enough for a 2015 hard drive.

</details>

<details>
<summary><b>Privacy</b></summary>

<br>

- The buffer lives in `%LOCALAPPDATA%\DejaVu\buffer` and is wiped on a clean exit. After a crash it becomes your recovered clip instead.
- Nothing leaves your machine. No uploads, no account, no telemetry. The app never phones home; the update check in the tray menu runs only when you click it. The one exception is opt-in: turn on "Notify about new versions" and it asks GitHub a few times a day, which means GitHub sees your IP.
- Failure balloons offer "click to report": that click only opens a prefilled GitHub issue in your browser (with the log tail, usernames scrubbed) for you to review and submit, or just close. The app itself sends nothing, ever.
- Pause buffering any time from the tray; the dot turns grey.
- The mic is never captured, and Discord voices are kept out of the mix by default.

</details>

<details>
<summary><b>Files &amp; config</b></summary>

<br>

| Where | What |
|---|---|
| `Videos\DejaVu` | your saved replays |
| `%APPDATA%\DejaVu\config.json` | all settings (missing keys fill themselves in; out-of-range values are clamped in memory without rewriting your file) |
| `%LOCALAPPDATA%\DejaVu\buffer` | the rolling buffer |

Config keys: `bufferMinutes` (5 to 25) · `quality` · `fps` · `saveHotkey` · `saveRoot` · `captureTarget` (`"auto"` or `\\.\DISPLAY2`) · `showIndicator` · `indicatorStyle` (`"dot"` or `"icon"`) · `systemAudio` · `appAudioOnly` · `clipCapGB` · `audioExclude` · `saveSound` · `updateNotify`

</details>

<details>
<summary><b>Building &amp; tests</b></summary>

<br>

```
dotnet build src/DejaVu/DejaVu.csproj
dotnet run --project tests/DejaVu.Tests            # unit tests
dotnet run --project tests/DejaVu.Tests -- smoke   # + live capture / crash-recovery / audio-exclusion proof
dotnet run --project tests/DejaVu.Tests -- soak 12 # crash-kill soak: hard-kills a recording process at
                                                   # random moments, then proves every recovered clip
                                                   # decodes head to tail ("soak 500" = overnight run)
```

The smoke test records the real screen and plays a test tone to prove exclusion strips it from the captured mix. It skips itself if the screen is locked or asleep, since a sleeping desktop delivers no frames.

</details>

---

<div align="center">

**[MIT license](LICENSE)** · sibling of [Memento](https://github.com/blancodagoat/memento), the screenshot tool that stays out of your way

</div>
