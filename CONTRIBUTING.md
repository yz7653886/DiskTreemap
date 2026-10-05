# Contributing

Thanks for taking a look. This is a small, deliberately low-tech project: one
C# file, no SDK, no package manager. Keep it that way where you can.

## Prerequisites

- Windows 7 SP1 or later.
- The .NET Framework 4.x C# compiler (`csc.exe`), which ships with Windows. No
  Visual Studio, no .NET SDK and no NuGet restore are required.

## Build and test

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File verify.ps1
```

- `build.ps1` compiles `DiskTreemap.cs` into `DiskTreemap.exe` and prints the size.
- `verify.ps1` builds a fixture tree in `%TEMP%` and checks the CLI surface:
  JSON shape, totals, aggregation behaviour, argument handling and error exit
  codes (19 assertions). It must stay green.

The GUI itself (picker, treemap, right-click menu) has no automated coverage —
describe how you checked it by hand in your pull request.

## Ground rules

- **Keep `DiskTreemap.cs` a single file.** The whole point of the project is a
  one-file, no-dependency build.
- **Target C# 5.** The build uses the framework compiler, so avoid syntax added
  after C# 5 (string interpolation, `nameof`, expression-bodied members, `?.`,
  `=>` members, etc.).
- **Keep the PowerShell scripts ASCII-only.** Windows PowerShell 5.1 reads a
  BOM-less UTF-8 file as ANSI, so non-ASCII characters in `build.ps1` /
  `verify.ps1` corrupt the parser. Use English comments there.
- **Do not commit `DiskTreemap.exe`.** It is a build artifact and is ignored by
  `.gitignore`.
- **Update `CHANGELOG.md`** for user-visible changes.
- **Keep both READMEs in sync.** `README.md` is the English page,
  `使用说明.md` the Chinese one; they link to each other.

## Submitting changes

1. Fork the repository and create a topic branch.
2. Make the change, run `build.ps1` and `verify.ps1`.
3. Open a pull request; CI runs the same two scripts.
