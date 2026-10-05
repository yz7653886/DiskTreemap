# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
