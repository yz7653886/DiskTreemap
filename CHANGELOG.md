# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.1] - 2026-10-05

### Fixed

- Starting a new scan (F5, rescan, or picking another path) while one was still
  running could let the older result overwrite the newer one; each scan now has a
  generation, and a superseded scan never writes back to the UI.
- File symlinks were counted at their target's size on top of the real file,
  double counting linked data; file symlinks are now skipped just like junctions.
- Moving an item to the Recycle Bin is no longer described as always recoverable:
  when an item is too large for the bin, or the drive has none, Windows now warns
  that it will be deleted permanently.
- A click that started exactly at the canvas origin did not begin a pan.
- The window region was replaced on every resize without releasing the previous
  one, and the application icon was re-extracted on every use.

### Added

- Directories that cannot be read are counted and reported (CLI output and window
  title) instead of silently lowering the total.
- Unexpected exceptions are surfaced as a readable message instead of the .NET
  crash dialog.

### Changed

- `--out` now requires an explicit scan root instead of silently scanning the
  current working directory.

[1.0.1]: https://github.com/yz7653886/DiskTreemap/releases/tag/v1.0.1

## [1.0.0] - 2026-10-05

Initial release.

### Added

- Native WinForms treemap viewer — a single self-contained executable built from
  one C# file, with no browser and no local server.
- Squarified treemap layout with nested folder containers, labels that degrade
  gracefully, and drill-down navigation (double-click a folder to enter it, a
  file to open it or jump to its folder).
- Parallel file-system scanner that skips junctions and symlinks, with
  small-file aggregation below a configurable threshold.
- Native context actions: open, reveal in Explorer, properties, copy path or
  name, and move to the Recycle Bin.
- Borderless, rounded window with macOS-style traffic-light buttons, an icon
  toolbar, a path pill and eight-way edge resizing.
- Light / dark theme that follows the Windows app theme and remembers a manual
  choice in the registry.
- Automatic English / Chinese UI that follows the Windows display language.
- DPI-aware rendering.
- Headless CLI mode (`--out`, `--min`, `--help`).
- `build.ps1` one-step build and `verify.ps1` CLI acceptance suite (19
  assertions), both run by CI on every push.

[1.0.0]: https://github.com/yz7653886/DiskTreemap/releases/tag/v1.0
