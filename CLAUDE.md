# DejaVu

Always-on instant-replay recorder (ShadowPlay alternative) with a disk-backed,
crash-safe buffer. C# / .NET 10 WinForms, win-x64, zero NuGet dependencies — stdlib,
Win32 P/Invoke, and Media Foundation interop only. `AllowUnsafeBlocks` is on for the
capture/encode path. All P/Invoke lives in `Native.cs`.

## Build

```
dotnet publish src/DejaVu/DejaVu.csproj -c Release -p:SelfContained=false -o publish/framework-dependent
dotnet publish src/DejaVu/DejaVu.csproj -c Release -o publish/self-contained
```

If DejaVu is running in the tray, the publish output exe may be locked — kill it first.

## Tests

No test framework. `tests/DejaVu.Tests` is a plain exe with asserts; exit 0 = pass.
Tests must stay headless. Production files are pulled in via explicit `<Compile Include>`
in the test csproj — new source files that tests touch need adding there.

```
dotnet run --project tests/DejaVu.Tests            # + `-- smoke`
dotnet run --project tests/DejaVu.Tests -- soak 12 # crash-kill soak (buffer survives kills)
```

Crash-safety of the replay buffer is the core promise — run the soak test after touching
buffer/recording code.

## Conventions

- Sibling repos (`../blancoshot` = Memento, `../recite`) share files by duplication, not
  a shared lib: `AppConfig.cs`, `Native.cs`, `Theme.cs`, `TrayContext.cs`,
  `HotkeyWindow.cs`, `SingleInstance.cs`, `UpdateCheck.cs`, etc. If you fix a bug in one
  of these, check whether the siblings have the same file and port the fix.
- Icons are generated: `python3 tools/make-icons.py` rewrites `assets/` — never hand-edit.
- Runtime config: `%APPDATA%\DejaVu\config.json`. The Triumvirate launcher
  (`../triumvirate`) reads/writes this file — schema changes must stay compatible.
- CI (`.github/workflows/build.yml`) recreates a rolling `latest` release on every push;
  scoop manifest in `packaging/scoop/` autoupdates from tags.
