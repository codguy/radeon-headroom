# Contributing

Thanks for helping. The most useful contribution right now is a **compatibility report** from a GPU that is
not on the tested list: see [docs/SUPPORTED-GPUS.md](docs/SUPPORTED-GPUS.md).

## Building and testing

```
build.cmd          runs the tests, then builds Headroom.exe
build.cmd tests    runs the tests only
```

No tools to install: the build uses the C# compiler that ships with Windows, which means the code is written
in C# 5 (no string interpolation, no `?.`, no `out var`).

## Ground rules

- **Tests first for anything in `src/Core.cs`.** That file decides which bytes get written to someone's GPU
  table. Add the failing test to `src/Tests.cs`, watch it fail, then write the code.
- **No real firmware data in the repository.** Tests build synthetic tables. Do not commit vBIOS files,
  registry dumps or anything from `tables/`, `backups/` or `profiles/`.
- **A new table format needs measurements**, not only header offsets. The method is described in
  [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md).
- **Limits only move in the safe direction.** Changes that allow voltage, power or temperature above the
  reference will not be merged.
- Keep hardware access in `src/Gpu.cs`, `src/Sensors.cs` and `src/LoadTest.cs`, and keep `src/Core.cs` free
  of registry, device and UI code so it stays testable.

## Reporting a bug

Include the output of `Headroom.exe --status`, your AMD Software version, what you did, and the exact text
Headroom showed. Hardware IDs in the status output are fine to share; they identify the model, not you.
