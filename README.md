<div align="center">

# 🔴 DejaVu

### Your last 5–25 minutes, always recorded. One key saves it.

**Like ShadowPlay — but it never turns itself off, survives crashes,<br>and keeps Discord out of your clips.**

`one exe` · `any GPU` · `no account` · `no uploads` · `no telemetry`

</div>

---

## ⚡ The whole app in 10 seconds

1. 🔴 **A red dot in the corner** = your screen is being buffered. Always. Starts with Windows.
2. 🎮 **Something clip-worthy happens.**
3. ⌨️ **Press <kbd>Alt</kbd>+<kbd>F10</kbd>** → `eldenring_2026-08-11_224513.mp4` lands in `Videos\DejaVu`. A balloon confirms. Done.

That's it. There is no window, no scenes, no setup. The tray menu has every setting.

---

## 🧠 Why it doesn't let you down

| 😤 The usual pain | ✅ DejaVu |
|---|---|
| ShadowPlay **turns itself off silently** — you press the key, nothing was recording | Always on. If capture breaks, it heals itself and **tells you**. Never silent. |
| OBS makes you **remember to start the buffer** | Buffering from the second it launches. Boot → buffering. |
| **A crash eats your clip** (RAM buffers lose everything) | Buffer lives on disk as crash-safe segments. PC died? **Next boot auto-saves what survived.** |
| Discord pings & voice chat **leak into shared clips** | Discord's audio is **excluded from the mix by default**. Zero setup. |
| `Replay_2024_final(3).mp4` filename chaos | Clips auto-named after the game that was on screen. |
| Saved clips **eat your disk forever** | Optional cap: oldest clips roll off past 10/25/50 GB. Newest is never touched. |

---

## 🎛️ Everything you can change (it's all in the tray menu)

| Setting | Options |
|---|---|
| ⏱️ Buffer length | 5 / 10 / 15 / 20 / 25 min |
| 🖥️ Capture | Auto (follows your game) · any monitor · one window |
| 🎚️ Quality | Low / Medium / High — smart encoding: action gets the bits, menus cost ~nothing |
| 🎞️ Frame rate | 30 → 240 fps, only what your display can actually show |
| 🔊 System audio | On/off — mic is **never** recorded |
| 💾 Clip folder cap | Off / 10 / 25 / 50 GB |
| ⌨️ Hotkey | Click-to-rebind dialog |
| 🔴 Corner dot | On/off — it's click-through and **never appears in your clips** |

> 🛡️ **Admin games?** If hotkeys die while a game with anti-cheat has focus, hit *Restart as administrator* in the tray once. That's a Windows rule, not ours.

---

## 🥊 vs the big two

| | ShadowPlay | OBS Replay Buffer | 🔴 DejaVu |
|---|---|---|---|
| Stays on | ❌ silently dies | ❌ manual start | ✅ always + self-heals |
| Crash survival | ❌ RAM, all lost | ⚠️ MKV remux dance | ✅ auto-recovers on boot |
| "Saved!" feedback | ⚠️ sometimes nothing | ❌ none natively | ✅ balloon, click to open |
| Discord-free audio | ❌ | ⚠️ manual per-app setup | ✅ default |
| Install | NVIDIA App + overlay | full OBS + scenes | one exe (~160 KB) |
| GPU | NVIDIA only | any | any with a hw encoder |

<sub>Based on each tool's design and widely-reported user complaints; both evolve, verify against current versions.</sub>

**Honest numbers:** while buffering 1440p, DejaVu's working set measures ~120 MB (Low/30) to ~190 MB (High/60) — the encoder pipeline is the cost, and shrinking it is an active work item. Idle-paused it's a tray icon. No overlay, no services, no FPS tax from an in-game UI.

---

<details>
<summary>🔧 <b>How it works under the hood</b> (click)</summary>

<br>

- 📼 **Segment ring** — your screen is recorded in 1-minute hardware-encoded H.264 segments (NVENC / AMF / QuickSync, near-zero CPU) as **fragmented MP4**: kill the power mid-write and the file still plays. Old segments delete themselves, so disk use stays flat (~300 MB–1 GB total, by settings).
- 💾 **Save** = stitch the segments covering your window into one standard MP4 — a lossless remux, no re-encode, done in about a second.
- 💥 **Crash recovery** — segments on disk at launch mean the last session died; they're auto-stitched into `recovered_*.mp4` and you get a balloon.
- 🩹 **Self-healing capture** — a source that delivers zero frames gets dropped for the main display; a sleeping/locked screen just idles until you're back. It only ever stops after repeated hard failures, and it *says so*.
- 🔇 **Discord exclusion** — audio comes from Windows' process-loopback device with Discord's process tree removed from the mix (voice, pings, everything), encoded to AAC live and muxed per segment. Configurable: `audioExclude` in config (default Discord / Canary / PTB / Vesktop).

</details>

<details>
<summary>🖥️ <b>Runs on old PCs — that's the point</b></summary>

<br>

Built for ~2015 machines and up:

- **Windows 10 2004+** (that same build provides the Discord-exclusion audio device, so it works everywhere the app runs)
- **Any GPU with a hardware H.264 encoder** — NVENC since 2012, QuickSync since 2011, AMD VCE since 2013. No hw encoder → software fallback (costs CPU).
- **Disk over RAM by design**: on an old 8 GB machine, RAM is what your game needs — the buffer takes none of it. Disk writes are light enough for a 2015 hard drive.

</details>

<details>
<summary>🔒 <b>Privacy</b></summary>

<br>

- The buffer lives in `%LOCALAPPDATA%\DejaVu\buffer`, wiped on clean exit (after a crash it becomes your recovered clip).
- Nothing ever leaves your machine. No uploads, no account, no telemetry, no update phone-home.
- Pause buffering any time from the tray — the dot turns grey.
- The mic is **never** captured. Discord voices are **never** in the mix by default.

</details>

<details>
<summary>📁 <b>Files & config</b></summary>

<br>

| Where | What |
|---|---|
| `Videos\DejaVu` | your saved replays |
| `%APPDATA%\DejaVu\config.json` | all settings (broken values self-repair to defaults) |
| `%LOCALAPPDATA%\DejaVu\buffer` | the rolling buffer |

Config keys: `bufferMinutes` (5–25) · `quality` · `fps` · `saveHotkey` · `saveRoot` · `captureTarget` (`"auto"` or `\\.\DISPLAY2`) · `showIndicator` · `systemAudio` · `clipCapGB` · `audioExclude`

</details>

<details>
<summary>🛠️ <b>Building & tests</b></summary>

<br>

```
dotnet build src/DejaVu/DejaVu.csproj
dotnet run --project tests/DejaVu.Tests            # unit tests
dotnet run --project tests/DejaVu.Tests -- smoke   # + live capture / crash-recovery / audio-exclusion proof
```

The smoke test records the real screen and plays a test tone to *prove* exclusion strips it from the captured mix. It skips itself if the screen is locked or asleep.

</details>

---

<div align="center">

**[MIT license](LICENSE)** · sibling of 🖼️ [Memento](https://github.com/blancodagoat/memento), the screenshot tool that stays out of your way

</div>
