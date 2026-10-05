#requires -Version 5.1
<#
    Acceptance test for the headless (CLI) surface of DiskTreemap.exe.

    DiskTreemap is now a native WinForms application with no HTTP server and no
    browser. This script therefore covers the parts that can be checked without
    a human at the keyboard:

      * CLI scan (--out): JSON shape, totals and small-file aggregation
      * aggregation disabled with --min 0
      * argument handling: --help, unknown flags, missing / invalid values
      * missing root directory is refused with a non-zero exit code

    The interactive GUI (drive picker, treemap, right-click menu) is verified
    manually or through UI automation, not here.

    Usage:
      powershell -NoProfile -ExecutionPolicy Bypass -File verify.ps1
      powershell -NoProfile -ExecutionPolicy Bypass -File verify.ps1 -Exe "D:\path\DiskTreemap.exe"

    This script is deliberately ASCII-only: PowerShell 5.1 reads a BOM-less
    UTF-8 file as ANSI, so any non-ASCII expectation would be mangled.
#>
[CmdletBinding()]
param(
    [string]$Exe
)

$ErrorActionPreference = 'Stop'

if (-not $Exe) { $Exe = Join-Path $PSScriptRoot 'DiskTreemap.exe' }
$Exe = [IO.Path]::GetFullPath($Exe)

$work = Join-Path $env:TEMP 'disk_treemap_verify'
$test = Join-Path $work 'dt_test'

if (-not (Test-Path $Exe)) { throw ("exe not found: " + $Exe) }
New-Item -ItemType Directory -Force -Path $work | Out-Null

$pass = 0; $fail = 0
function Check($name, $cond, $detail) {
  if ($cond) { $script:pass++; Write-Host ("  [PASS] {0}" -f $name) }
  else       { $script:fail++; Write-Host ("  [FAIL] {0}  -> {1}" -f $name, $detail) }
}

# ---------------- build test tree ----------------
Write-Host "== build test tree =="
if (Test-Path $test) { Remove-Item $test -Recurse -Force }
New-Item -ItemType Directory -Path $test | Out-Null
New-Item -ItemType Directory -Path (Join-Path $test 'sub') | Out-Null
$rnd = New-Object Random 12345
$b1 = New-Object byte[] (2*1024*1024); $rnd.NextBytes($b1)
[IO.File]::WriteAllBytes((Join-Path $test 'big.bin'), $b1)
[IO.File]::WriteAllBytes((Join-Path $test 'sub\inner.bin'), (New-Object byte[] (1500000)))
[IO.File]::WriteAllText((Join-Path $test 'small.txt'), 'hello')
[IO.File]::WriteAllText((Join-Path $test 'sub\tiny.txt'), 't')
Write-Host ("  test root = {0}" -f $test)

# expected total = big.bin 2097152 + inner.bin 1500000 + small.txt 5 + tiny.txt 1
$expectedTotal = 2097152 + 1500000 + 5 + 1

# ---------------- 1. CLI scan --out ----------------
Write-Host "== 1. CLI scan (--out) =="
$outjson = Join-Path $work 'dt_out.json'
if (Test-Path $outjson) { Remove-Item $outjson -Force }
& $Exe $test --out $outjson --min 1048576 | Out-Null
Check "scan exit code 0" ($LASTEXITCODE -eq 0) ("exit " + $LASTEXITCODE)
Check "json file exists" (Test-Path $outjson) "missing $outjson"
$j = Get-Content -Raw -Encoding UTF8 $outjson | ConvertFrom-Json
Check "root field" ($j.root -ieq $test) ("got " + $j.root)
Check ("root size = " + $expectedTotal) ($j.tree.s -eq $expectedTotal) ("got " + $j.tree.s)
$kids = $j.tree.c
$names = ($kids | ForEach-Object { $_.n }) -join ','
Check "root children names" ($names -match 'sub' -and $names -match 'big\.bin' -and $names -match 'small files') ("got " + $names)
$rootAgg = @($kids | Where-Object { $_.agg -eq 1 })
Check "root agg node count = 1" ($rootAgg.Count -eq 1) ("got " + $rootAgg.Count)
$sb = $kids | Where-Object { $_.n -eq 'sub' }
Check "sub has inner.bin + agg" ($sb.c.Count -eq 2) ("got " + $sb.c.Count)
Check "root agg k = 1" ($rootAgg[0].k -eq 1) ("got " + $rootAgg[0].k)
$subAgg = @($sb.c | Where-Object { $_.agg -eq 1 })
Check "sub agg k = 1" ($subAgg.Count -eq 1 -and $subAgg[0].k -eq 1) ("count=" + $subAgg.Count + " k=" + $subAgg[0].k)

# ---------------- 2. --min 0 disables aggregation ----------------
Write-Host "== 2. --min 0 disables aggregation =="
$outjson0 = Join-Path $work 'dt_out0.json'
if (Test-Path $outjson0) { Remove-Item $outjson0 -Force }
& $Exe $test --out $outjson0 --min 0 | Out-Null
Check "min0 exit code 0" ($LASTEXITCODE -eq 0) ("exit " + $LASTEXITCODE)
$j0 = Get-Content -Raw -Encoding UTF8 $outjson0 | ConvertFrom-Json
$all0 = @($j0.tree.c) + @(($j0.tree.c | Where-Object { $_.n -eq 'sub' }).c)
$agg0 = @($all0 | Where-Object { $_.agg -eq 1 })
Check "no agg nodes with --min 0" ($agg0.Count -eq 0) ("agg=" + $agg0.Count)
Check "total unchanged with --min 0" ($j0.tree.s -eq $expectedTotal) ("got " + $j0.tree.s)

# ---------------- 3. argument handling ----------------
Write-Host "== 3. argument handling =="
& $Exe --help | Out-Null
Check "--help exit code 0" ($LASTEXITCODE -eq 0) ("exit " + $LASTEXITCODE)

& $Exe --bogus-flag | Out-Null
Check "unknown flag exit code 2" ($LASTEXITCODE -eq 2) ("exit " + $LASTEXITCODE)

& $Exe $test --out | Out-Null
Check "--out without value exit 2" ($LASTEXITCODE -eq 2) ("exit " + $LASTEXITCODE)

& $Exe $test --out (Join-Path $work 'x.json') --min notanumber | Out-Null
Check "--min non-numeric exit 2" ($LASTEXITCODE -eq 2) ("exit " + $LASTEXITCODE)

& $Exe $test --out (Join-Path $work 'x.json') --min -5 | Out-Null
Check "--min negative exit 2" ($LASTEXITCODE -eq 2) ("exit " + $LASTEXITCODE)

$ghost = Join-Path $work 'does_not_exist_dir'
& $Exe $ghost --out (Join-Path $work 'x.json') | Out-Null
Check "missing root exit 2" ($LASTEXITCODE -eq 2) ("exit " + $LASTEXITCODE)

& $Exe $test (Join-Path $test 'sub') --out (Join-Path $work 'x.json') | Out-Null
Check "two positional roots exit 2" ($LASTEXITCODE -eq 2) ("exit " + $LASTEXITCODE)

# cleanup
if (Test-Path $test) { Remove-Item $test -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host ""
Write-Host ("================ {0} passed, {1} failed ================" -f $pass, $fail)
if ($fail -gt 0) { exit 1 }
