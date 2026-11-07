# DejaVu 1.5.1

DejaVu has moved off GitHub to https://git.blancodagoat.dev/blancodagoat/DejaVu. This release
points the app there.

- **Update check:** "Check for updates" and "Notify about new versions" now ask
  git.blancodagoat.dev instead of GitHub. 1.5.0 and older still ask GitHub, so they won't see
  this release on their own. Download 1.5.1 once by hand, and updates work from here on.
- **Reporting a problem:** clicking a failure balloon now copies a short report (what
  happened, your Windows version, the log tail with your username scrubbed) to the clipboard
  and opens the DejaVu Discord (https://discord.gg/NHke2BwRA6). Paste it in #support. The
  report always fits one Discord message, and the app still sends nothing by itself.
- The update balloon no longer suggests scoop: the scoop bucket isn't available right now.
