#requires -Version 5.1
<#
    Regenerate the standalone viewer (index.html) from a scan result.

    Injects a scan JSON document into index.template.html by replacing the
    "__DEMO_DATA__" placeholder inside <script id="demo">, producing a single
    self-contained HTML file that can be opened directly in a browser.

    The JSON is written with UTF-8 (no BOM) and every "</" is escaped to "<\/"
    so the payload can never terminate the surrounding <script> element.

    Usage:
      powershell -NoProfile -ExecutionPolicy Bypass -File build-index.ps1 -Json demo.json
      powershell -NoProfile -ExecutionPolicy Bypass -File build-index.ps1 -Json "C:\data\treemap.json" -Out index.html
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Json,
    [string]$Template = 'index.template.html',
    [string]$Out = 'index.html'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$tplPath = if ([IO.Path]::IsPathRooted($Template)) { $Template } else { Join-Path $root $Template }
$outPath = if ([IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $root $Out }
$jsonPath = if ([IO.Path]::IsPathRooted($Json)) { $Json } else { Join-Path $root $Json }

if (-not (Test-Path $tplPath)) { throw ("Template not found: " + $tplPath) }
if (-not (Test-Path $jsonPath)) { throw ("Scan JSON not found: " + $jsonPath) }

$template = [IO.File]::ReadAllText($tplPath, [Text.Encoding]::UTF8)
$payload = [IO.File]::ReadAllText($jsonPath, [Text.Encoding]::UTF8)

$before = ([regex]::Matches($template, [regex]::Escape('__DEMO_DATA__'))).Count
if ($before -ne 1) { throw ("Expected exactly 1 placeholder, found " + $before) }

$safe = $payload.Replace('</', '<\/')
$html = $template.Replace('__DEMO_DATA__', $safe)
$after = ([regex]::Matches($html, [regex]::Escape('__DEMO_DATA__'))).Count

[IO.File]::WriteAllText($outPath, $html, (New-Object Text.UTF8Encoding($false)))

$kb = [math]::Round(([IO.FileInfo]$outPath).Length / 1KB, 1)
Write-Host ("Wrote {0} ({1} KB)  placeholders before={2} after={3}" -f $outPath, $kb, $before, $after)
