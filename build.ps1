#requires -Version 5.1
<#
    Build DiskTreemap.exe from DiskTreemap.cs.

    The viewer (index.template.html) is embedded as a manifest resource named
    "viewer.html", so the resulting executable is fully self-contained.

    Usage:
      powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1

    Note: csc.exe must be run with the project folder as the current directory
    and the resource passed as a RELATIVE file name, otherwise a path that
    contains spaces triggers error CS2021.
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

    Write-Host ("Using compiler: {0}" -f $csc)
    & $csc /nologo /target:exe /optimize+ /out:DiskTreemap.exe DiskTreemap.cs /resource:index.template.html,viewer.html
    if ($LASTEXITCODE -ne 0) { throw ("csc failed with exit code " + $LASTEXITCODE) }

    $exe = Join-Path $root 'DiskTreemap.exe'
    Write-Host ("Built {0} ({1:n0} bytes)" -f $exe, (Get-Item $exe).Length)
}
finally { Pop-Location }
