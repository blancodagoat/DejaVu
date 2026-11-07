# DejaVu 1.5.0

A maintenance release: saves work again on PCs where AV1 recording broke, and the
clip can now carry more than one output device.

## Fixed: "Save failed" on some PCs with hardware AV1 (#9, #10)

Thanks to @AaronTheFlash (#9) and @recui (#10) for the reports and the logs.

On some PCs with an AV1-capable GPU, Windows' MP4 writer accepted the first few
frames and then gave up (error `0xC00D4A45`). DejaVu only stopped using AV1 when it
failed before the first frame, so it picked AV1 again on every restart and failed
about once a second. Each attempt left a broken file in the buffer, those pushed the
good ones out, and pressing the hotkey ended in "Save failed".

Now:

- Before recording AV1, DejaVu writes a short test clip and reads it back. If that
  fails, or takes longer than a second, it records H.264 instead.
- If AV1 fails later in the session, DejaVu switches to H.264 for the rest of the
  session, without a failure balloon. AV1 is tried again at the next launch.
- A segment that won't play back is dropped instead of taking a slot in the buffer.
- Issue reports say when AV1 was turned off.

If you were affected, you should not need to change anything: install 1.5.0 and
saves work again, in H.264.

## New: record more than one output device (#11)

Thanks to @Greetedlight18 (#11) for the request.

Virtual mixers like SteelSeries Sonar and Elgato Wave Link put the game and voice
chat on separate output devices, so recording the default device alone missed one
of them. The tray menu has a new **Audio devices** submenu: the default output is
always recorded, and any other output you tick is recorded too, mixed into the same
audio track.

- Each device runs on its own clock, so its sound is resampled on the fly to stay in
  sync with the rest over the full 25-minute buffer.
- A ticked device is recorded whole, Discord included. With *Keep Discord out of clips*
  on, Discord stays out of everything unless it plays only on ticked devices (for
  example Discord set to Sonar Chat, with Chat ticked). That is checked when each
  one-minute segment starts, so joining a call mid-segment can miss up to a minute.
- A device that is unplugged, or disappears mid-recording (say, a Sonar restart),
  only loses its own sound. The buffer keeps going and picks the device up again at
  the next one-minute segment. Ticked devices that are unplugged show as
  "Disconnected device" so you can untick them.
- If you make a ticked device your default output, it isn't recorded twice: DejaVu
  notices the switch straight away and leaves that device's own stream out until the
  next segment. With *Captured app audio only* on, that also leaves out its other
  sound (voice chat, for example) for up to a minute.
- A virtual mixer's own effects (Sonar's ducking or voice processing, for example)
  are recorded the way they sound: DejaVu records each device's output as is.
- Needs *System audio* on.
- Your config carries over as is: the new `extraAudioDevices` key is optional, and a
  1.4 config without it isn't rewritten.

## Also

- When a segment fails to finalize, its audio capture is still released.

## Launch video

The README now shows DejaVu in 20 seconds instead of a screenshot; the full video with sound is attached below as `dejavu-1.5.mp4`. Music: Sascha Ende, ende.app (CC BY 4.0).
