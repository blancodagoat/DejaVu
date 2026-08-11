# Contributing

Thanks for wanting to help. Keep it small and simple — that's the whole point of this tool.

## Setup

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows.

```
dotnet build src/DejaVu            # build
dotnet run --project tests/DejaVu.Tests   # run the test suite (exit code 0 = pass)
```

## Ground rules

- **No dependencies.** The app is stdlib + Win32 only; all interop lives in `src/DejaVu/Native.cs`.
- **Stay light.** The pitch is ~8 MB of RAM and under 1 MB to download. Anything that moves those numbers needs a very good reason.
- **Tests run headless.** Logic that doesn't need a live desktop goes in a testable file (see `tests/DejaVu.Tests/Tests.cs` for the pattern) with assertions added there.

## Pull requests

- One change per PR.
- `dotnet run --project tests/DejaVu.Tests` must pass — CI runs it on every push.
- For behavior changes, say what you did and why in the description; a before/after screenshot helps for anything visual.

Not sure whether something fits? Open an issue first and ask.
