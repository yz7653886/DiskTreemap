# DiskTreemap

A fast, single-file disk-usage treemap viewer for Windows — a lightweight
alternative to SpaceSniffer. Point it at a folder and it renders an interactive
squarified treemap you can drill into, with a right-click menu for native file
operations.

## Features

- **Parallel scanner** — walks the tree with `Parallel.ForEach` (CPU x2 workers)
  and skips junctions/symlinks to avoid loops.
- **Small-file aggregation** — files below `--min` bytes are collapsed into a
  single `(N small files)` node per folder, keeping the output small and legible.
- **Self-contained executable** — `DiskTreemap.exe` embeds the viewer as a
  manifest resource; no external files required.
- **Local, token-guarded service** — serves the UI on `127.0.0.1` from a random
  port, and exposes a small API for open / reveal / copy-path / recycle-bin delete.
- **Apple-style context menu** in the viewer, with clipboard fallback and
  viewport-edge clamping.
- **Static HTML output** — `index.html` is a standalone viewer you can double-click
  or share; it works with or without the native bridge.

## Project layout

| File | Purpose |
| --- | --- |
| `DiskTreemap.cs` | Source of the scanner + local HTTP service + native actions. |
| `DiskTreemap.exe` | Built self-contained executable (the deliverable). |
| `index.template.html` | Viewer template with a `__DEMO_DATA__` placeholder. |
| `index.html` | Standalone viewer generated from a scan result. |
| `scan-treemap.ps1` | PowerShell-only scanner producing the treemap JSON. |
| `build.ps1` | Compiles `DiskTreemap.cs` into `DiskTreemap.exe`. |
| `build-index.ps1` | Injects a scan JSON into the template to produce `index.html`. |
| `verify.ps1` | End-to-end acceptance test (CLI + service + security, 31 assertions). |
| `使用说明.md` | End-user guide (Chinese). |

## Build

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

Requires the .NET Framework 4.x C# compiler (`csc.exe`), which ships with Windows.

Run the acceptance suite afterwards (31 assertions across the CLI, the local
service, and the security boundaries):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File verify.ps1
```

## Usage

Run the executable (defaults to the current directory, opens a browser):

```powershell
.\DiskTreemap.exe
.\DiskTreemap.exe "C:\Users"
.\DiskTreemap.exe "C:\" --min 1048576 --port 8731 --no-open
.\DiskTreemap.exe --out tree.json "D:\Projects"
.\DiskTreemap.exe --help
```

Options:

| Option | Meaning |
| --- | --- |
| `--out <file>` / `-o` | Write the scan JSON to a file and exit. |
| `--min <bytes>` / `--min-file` | Aggregation threshold (default 1048576). |
| `--port <n>` | Fixed port; `0` (default) picks a free one. |
| `--no-open` | Do not launch a browser. |
| `--help` / `-h` / `/?` | Show help. |

PowerShell-only path (no executable), then build the static viewer:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scan-treemap.ps1 -Root "C:\Users" -Out demo.json
powershell -NoProfile -ExecutionPolicy Bypass -File build-index.ps1 -Json demo.json
```

## Security design

- Binds **only** to loopback (`127.0.0.1`); nothing is exposed to the network.
- Random port and a random per-run token, accepted via the `X-DT-Token` header
  or a `?t=` query parameter; comparison is constant-time.
- `Host` header is validated against `127.0.0.1` / `localhost` / `::1` with a
  matching port, blocking DNS-rebinding attempts.
- Every path is normalized and must stay inside the scan root; reparse points
  (in the final component or any ancestor), NUL bytes, and missing paths are rejected.
- Deletion goes through `SHFileOperation` with `FOF_ALLOWUNDO`, i.e. the recycle
  bin — and the scan root itself can never be deleted.

## License

Internal tool — adapt as needed.
