#requires -Version 5.1
<#
    Build DiskTreemap.exe from DiskTreemap.cs.

    The result is a single self-contained native WinForms executable: it has no
    HTTP server, no browser and no embedded resources.

    Usage:
      powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
#>
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root
try {
    $candidates = @(
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
    )
    $csc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $csc) { throw 'csc.exe (.NET Framework 4.x) not found.' }

    $manifest = Join-Path $root 'app.manifest'
    if (-not (Test-Path $manifest)) { throw ("manifest not found: " + $manifest) }
    $icon = Join-Path $root 'app.ico'
    if (-not (Test-Path $icon)) { throw ("icon not found: " + $icon) }

    Write-Host ("Using compiler: {0}" -f $csc)
    # /win32manifest embeds the manifest (asInvoker, supportedOS, DPI aware).
    # Together with the assembly attributes in DiskTreemap.cs it gives the
    # binary a proper identity, which keeps anti-virus heuristics quieter.
    # NOTE: keep this script ASCII-only - PowerShell 5.1 reads a BOM-less
    # UTF-8 file as ANSI, and non-ASCII bytes here corrupt the parser.
    # /win32icon embeds app.ico as the executable's icon resource.
    & $csc /nologo /target:winexe /optimize+ /win32manifest:$manifest /win32icon:$icon /out:DiskTreemap.exe DiskTreemap.cs
    if ($LASTEXITCODE -ne 0) { throw ("csc failed with exit code " + $LASTEXITCODE) }

    $exe = Join-Path $root 'DiskTreemap.exe'
    Write-Host ("Built {0} ({1:n0} bytes)" -f $exe, (Get-Item $exe).Length)
}
finally { Pop-Location }
