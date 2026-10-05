#requires -Version 5.1
<#
    End-to-end acceptance test for DiskTreemap.exe.

    Builds a throw-away folder tree, runs the CLI scanner, then starts the local
    service and exercises the HTTP surface with raw TCP requests:

      * CLI scan (--out) JSON shape and aggregation
      * auth: 401 without / with a wrong token
      * anti-DNS-rebinding: 403 for a foreign Host header
      * viewer delivery: 204 favicon, 200 index, token injection, placeholder cleared
      * routing: 404 unknown GET, 405 non-GET, 404 unknown API
      * path safety: refuse the scan root, traversal, out-of-root, missing paths
      * native actions: reveal / open / recycle-bin delete (and confirm deletion)

    Usage:
      powershell -NoProfile -ExecutionPolicy Bypass -File verify.ps1
      powershell -NoProfile -ExecutionPolicy Bypass -File verify.ps1 -Exe "D:\path\DiskTreemap.exe"

    This script is deliberately ASCII-only: PowerShell 5.1 reads a BOM-less
    UTF-8 file as ANSI, so the expected (Chinese) error strings are decoded from
    \uXXXX escapes at runtime instead of being written literally.
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

# expected error strings (kept as escapes so this file stays pure ASCII)
$expDelRoot    = [regex]::Unescape('\u4e0d\u80fd\u5220\u9664\u626b\u63cf\u6839\u76ee\u5f55')  # cannot delete scan root
$expOutOfScope = [regex]::Unescape('\u8d85\u51fa\u626b\u63cf\u8303\u56f4')                  # outside scan scope
$expNotExist   = [regex]::Unescape('\u8def\u5f84\u4e0d\u5b58\u5728')                        # path does not exist

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
$victim = Join-Path $test 'victim.txt'
[IO.File]::WriteAllText($victim, 'delete me')
Write-Host ("  test root = {0}" -f $test)

# expected total = big.bin 2097152 + inner.bin 1500000 + small.txt 5 + tiny.txt 1 + victim.txt 9
$expectedTotal = 2097152 + 1500000 + 5 + 1 + 9

# ---------------- 1. CLI scan ----------------
Write-Host "== 1. CLI scan (--out) =="
$outjson = Join-Path $work 'dt_out.json'
if (Test-Path $outjson) { Remove-Item $outjson -Force }
$cliOut = & $Exe $test --out $outjson --min 1048576 2>&1 | Out-String
$cliOut.Trim() -split "`n" | ForEach-Object { Write-Host ("  | " + $_.TrimEnd()) }
Check "exit json file exists" (Test-Path $outjson) "missing $outjson"
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
Check "root agg k = 2" ($rootAgg[0].k -eq 2) ("got " + $rootAgg[0].k)
$subAgg = @($sb.c | Where-Object { $_.agg -eq 1 })
Check "sub agg k = 1" ($subAgg.Count -eq 1 -and $subAgg[0].k -eq 1) ("count=" + $subAgg.Count + " k=" + $subAgg[0].k)

# ---------------- 2. server ----------------
Write-Host "== 2. local server =="
$log = Join-Path $work 'dt_server.log'
$err = Join-Path $work 'dt_server.err'
if (Test-Path $log) { Remove-Item $log -Force }
if (Test-Path $err) { Remove-Item $err -Force }
$proc = Start-Process -FilePath $Exe -ArgumentList @($test, '--no-open') -RedirectStandardOutput $log -RedirectStandardError $err -PassThru -WindowStyle Hidden

$url = $null
for ($i=0; $i -lt 150; $i++) {
  Start-Sleep -Milliseconds 200
  if (Test-Path $log) {
    $txt = Get-Content -Raw -Encoding UTF8 $log
    $m = [regex]::Match($txt, 'http://127\.0\.0\.1:(\d+)/\?t=([0-9a-f]+)')
    if ($m.Success) { $url = $m.Value; $script:port = [int]$m.Groups[1].Value; $script:token = $m.Groups[2].Value; break }
  }
  if ($proc.HasExited) { break }
}
Check "server printed url" ($url -ne $null) ("log: " + (Get-Content -Raw -Encoding UTF8 $log 2>$null) + " err: " + (Get-Content -Raw -Encoding UTF8 $err 2>$null))
if ($url -eq $null) { if (-not $proc.HasExited) { $proc.Kill() }; Write-Host "ABORT"; exit 1 }
Write-Host ("  port={0} token={1}" -f $script:port, $script:token)

$script:hostHdr = "127.0.0.1:$script:port"
function Send-Raw([string]$hostHdr, [string]$req) {
  $c = New-Object Net.Sockets.TcpClient
  $c.Connect('127.0.0.1', $script:port)
  $ns = $c.GetStream()
  $r = $req.Replace('HOSTHDR', $hostHdr)
  $b = [Text.Encoding]::ASCII.GetBytes($r)
  $ns.Write($b,0,$b.Length); $ns.Flush()
  $buf = New-Object byte[] 65536
  $ms = New-Object IO.MemoryStream
  try { while (($n = $ns.Read($buf,0,$buf.Length)) -gt 0) { $ms.Write($buf,0,$n) } } catch {}
  $c.Close()
  return [Text.Encoding]::UTF8.GetString($ms.ToArray())
}
function StatusOf($resp) { ($resp -split "`r`n" | Select-Object -First 1) }
function BodyOf($resp) { $i = $resp.IndexOf("`r`n`r`n"); if ($i -lt 0) { return '' } ; return $resp.Substring($i+4) }
function Get-Req([string]$method, [string]$target, [string]$hostHdr, [string]$extra) {
  return ($method + " " + $target + " HTTP/1.1`r`nHost: " + $hostHdr + "`r`n" + $extra + "Connection: close`r`n`r`n")
}
function Get-Tok([string]$path) { return $path + "?t=" + $script:token }
function PostJson($api, $pathVal) {
  $obj = @{ path = $pathVal } | ConvertTo-Json -Compress
  $bytes = [Text.Encoding]::UTF8.GetBytes($obj)
  $req = "POST " + (Get-Tok $api) + " HTTP/1.1`r`nHost: $script:hostHdr`r`nContent-Type: application/json`r`nContent-Length: $($bytes.Length)`r`nConnection: close`r`n`r`n$obj"
  return Send-Raw $script:hostHdr $req
}

$r = Send-Raw $script:hostHdr (Get-Req 'GET' '/' $script:hostHdr '')
Check "no token -> 401" ((StatusOf $r) -match ' 401 ') (StatusOf $r)
$r = Send-Raw $script:hostHdr (Get-Req 'GET' '/?t=deadbeef' $script:hostHdr '')
Check "wrong token -> 401" ((StatusOf $r) -match ' 401 ') (StatusOf $r)
$r = Send-Raw 'evil.example.com' (Get-Req 'GET' (Get-Tok '/') 'evil.example.com' '')
Check "bad host -> 403" ((StatusOf $r) -match ' 403 ') (StatusOf $r)
$r = Send-Raw $script:hostHdr (Get-Req 'GET' '/favicon.ico' $script:hostHdr '')
Check "favicon -> 204" ((StatusOf $r) -match ' 204 ') (StatusOf $r)
$r = Send-Raw $script:hostHdr (Get-Req 'GET' (Get-Tok '/') $script:hostHdr '')
Check "index with token -> 200" ((StatusOf $r) -match ' 200 ') (StatusOf $r)
$b = BodyOf $r
Check "index injects __DT_TOKEN__" ($b -match ('window\.__DT_TOKEN__="' + $script:token + '"')) "token not found in body"
Check "index no leftover placeholder" (-not ($b -match '__DEMO_DATA__')) "placeholder still present"
Check "index embeds scan root" ($b -match [regex]::Escape('dt_test')) "root name not in body"
$r = Send-Raw $script:hostHdr (Get-Req 'GET' (Get-Tok '/api/ping') $script:hostHdr '')
Check "ping ok" ((StatusOf $r) -match ' 200 ' -and (BodyOf $r) -match '"ok":true') ((StatusOf $r) + ' ' + (BodyOf $r))
$r = Send-Raw $script:hostHdr (Get-Req 'GET' (Get-Tok '/nope') $script:hostHdr '')
Check "unknown GET -> 404" ((StatusOf $r) -match ' 404 ') (StatusOf $r)
$r = Send-Raw $script:hostHdr (Get-Req 'PUT' (Get-Tok '/') $script:hostHdr '')
Check "PUT -> 405" ((StatusOf $r) -match ' 405 ') (StatusOf $r)

$r = PostJson '/api/delete' $test
Check "delete root refused" ((BodyOf $r).Contains($expDelRoot)) (BodyOf $r)
Check "root still exists" (Test-Path $test) "root vanished!"
$r = PostJson '/api/delete' (Join-Path $test '..\..\Windows')
Check "traversal refused" ((BodyOf $r).Contains($expOutOfScope)) (BodyOf $r)
$r = PostJson '/api/delete' 'C:\Windows\System32\drivers\etc\hosts'
Check "out-of-root abs refused" ((BodyOf $r).Contains($expOutOfScope)) (BodyOf $r)
$r = PostJson '/api/delete' (Join-Path $test 'ghost.txt')
Check "missing file refused" ((BodyOf $r).Contains($expNotExist)) (BodyOf $r)
$r = PostJson '/api/delete' $victim
Check "delete victim ok" ((BodyOf $r) -match '"ok":true') (BodyOf $r)
Start-Sleep -Milliseconds 500
Check "victim gone" (-not (Test-Path $victim)) "victim still exists"
$r = PostJson '/api/reveal' (Join-Path $test 'sub')
Check "reveal ok" ((BodyOf $r) -match '"ok":true') (BodyOf $r)
$r = PostJson '/api/open' $test
Check "open ok" ((BodyOf $r) -match '"ok":true') (BodyOf $r)
$r = PostJson '/api/whatever' $test
Check "unknown api -> 404" ((StatusOf $r) -match ' 404 ') ((StatusOf $r) + ' ' + (BodyOf $r))
$obj = @{ path = $test } | ConvertTo-Json -Compress
$bytes = [Text.Encoding]::UTF8.GetBytes($obj)
$req = "POST /api/delete HTTP/1.1`r`nHost: $script:hostHdr`r`nContent-Type: application/json`r`nContent-Length: $($bytes.Length)`r`nConnection: close`r`n`r`n$obj"
$r = Send-Raw $script:hostHdr $req
Check "POST without token -> 401" ((StatusOf $r) -match ' 401 ') (StatusOf $r)

if (-not $proc.HasExited) { $proc.Kill() }
Start-Sleep -Milliseconds 300
if (Test-Path $test) { Remove-Item $test -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host ""
Write-Host ("================ {0} passed, {1} failed ================" -f $pass, $fail)
if ($fail -gt 0) { exit 1 }
