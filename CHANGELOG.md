# Changelog

Every release, newest first. The three 1.5 releases have full notes in [docs/](docs); older
ones are summed up in a line each. Dates are release dates.

## 1.5.2 (2026-11-07)

- Fixed Discord leaking into clips with *Captured app audio only* on, on SteelSeries Sonar and
  similar setups. DejaVu now records from the output device the game is actually playing on.
  [Full notes](docs/release-notes-1.5.2.md)

## 1.5.1 (2026-11-07)

- "Check for updates" and "Notify about new versions" ask git.blancodagoat.dev. 1.5.0 and older
  cannot see new versions on their own: download 1.5.1 once by hand.
- Clicking a failure balloon copies a short report to the clipboard and opens the DejaVu Discord.
- The update balloon no longer suggests scoop.
  [Full notes](docs/release-notes-1.5.1.md)

## 1.5.0 (2026-11-07)

- Fixed "Save failed" on some PCs with hardware AV1.
- New: record more than one output device (*Audio devices* in the tray menu).
- When a segment fails to finalize, its audio capture is still released.
  [Full notes](docs/release-notes-1.5.0.md)

## 1.4.0 (2026-10-03)

- Opt-in microphone: mixes your default mic into the clip's audio. Off by default.

## 1.3.0 (2026-09-06)

- Desktop Duplication as a second capture method, for the Windows 10 capture border.

## 1.2.13 (2026-09-06)

- A wedged encoder no longer takes the app hostage.

## 1.2.12 (2026-08-21)

- Shutdown is bounded, so Exit always exits.

## 1.2.11 (2026-08-17)

- Stopped the autostart pair that booted DejaVu non-elevated.

## 1.2.10 (2026-08-13)

- A named quit event for external managers.

## 1.2.9 (2026-08-13)

- One-click scoop updates.

## 1.2.8 (2026-08-13)

- The corner dot follows the recorded display.

## 1.2.7 (2026-08-13)

- App audio that survives restarts and per-app routing.

## 1.2.6 (2026-08-13)

- An elevated restart waits out the old instance.

## 1.2.5 (2026-08-13)

- The copied scoop command syncs buckets first.

## 1.2.4 (2026-08-13)

- Opt-in update notifications, scoop-aware.

## 1.2.3 (2026-08-13)

- An audible save confirmation.

## 1.2.2 (2026-08-13)

- Media Foundation objects are confined to the MTA.

## 1.2.1 (2026-08-12)

- Numbered download copies are tidied up on launch.

## 1.2.0 (2026-08-12)

- Hardening for other people's machines across capture, encoding, audio, muxing, filesystem,
  locale and OS configuration: failures are visible, recoverable, and never eat footage.

## 1.1.1 (2026-08-12)

- Survives d3d11.dll shims, logs audio failures, and adds the corner indicator style.

## 1.1.0 (2026-08-12)

- A crash-kill soak test with duration-verified recovery, and click-to-report from failure
  balloons.

## 1.0.0 (2026-08-12)

- First release. The update check runs only when you click it.
