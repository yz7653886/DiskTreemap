#requires -Version 5.1
<#
    DiskTreemap fast scanner
    Enumerates a folder tree with a parallel .NET worker and emits a JSON tree
    that the interactive viewer (index.html) renders as a treemap.

    Usage:
      powershell -NoProfile -ExecutionPolicy Bypass -File scan-treemap.ps1 -Root "C:\" -Out "data.json" -MinFile 1048576

    Notes:
      - MinFile (bytes) = files smaller than this are aggregated into a single
        "(N small files)" node per folder, which keeps the JSON small and the
        treemap readable. Directories are always kept.
      - Junctions / symlinks are skipped to avoid loops.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Root,
    [string]$Out = "treemap.json",
    [long]$MinFile = 1048576
)

$ErrorActionPreference = 'Stop'

$src = @'
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading.Tasks;

public static class DiskTreemapScanner
{
    class D { public string Name; public List<D> Children = new List<D>(); public List<F> Files = new List<F>(); public long Size; }
    struct F { public string Name; public long Size; }
    class Ent { public D Dir; public string FileName; public long Size; }

    public static string Run(string root, long minFile)
    {
        var rootDi = new DirectoryInfo(root);
        string rootName = rootDi.Name;
        if (string.IsNullOrEmpty(rootName)) rootName = rootDi.FullName;

        var map = new ConcurrentDictionary<string, D>(StringComparer.OrdinalIgnoreCase);
        var rootNode = new D { Name = rootName };
        map[root] = rootNode;

        var current = new List<string> { root };
        while (current.Count > 0)
        {
            var next = new ConcurrentBag<string>();
            Parallel.ForEach(current,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2 },
                d =>
                {
                    D node;
                    if (!map.TryGetValue(d, out node)) return;
                    DirectoryInfo di;
                    try { di = new DirectoryInfo(d); } catch { return; }
                    try
                    {
                        foreach (var fsi in di.EnumerateFileSystemInfos())
                        {
                            try
                            {
                                FileAttributes at = fsi.Attributes;
                                bool isDir = (at & FileAttributes.Directory) != 0;
                                bool isLink = (at & FileAttributes.ReparsePoint) != 0;
                                if (isDir)
                                {
                                    if (isLink) continue;
                                    var child = new D { Name = fsi.Name };
                                    if (map.TryAdd(fsi.FullName, child))
                                    {
                                        lock (node) { node.Children.Add(child); }
                                        next.Add(fsi.FullName);
                                    }
                                }
                                else
                                {
                                    long len = 0;
                                    FileInfo fi = fsi as FileInfo;
                                    if (fi != null) len = fi.Length;
                                    lock (node) { node.Files.Add(new F { Name = fsi.Name, Size = len }); }
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                });
            current = new List<string>(next);
        }

        ComputeSize(rootNode);

        var sb = new StringBuilder(1 << 20);
        sb.Append("{\"root\":\"").Append(Esc(rootDi.FullName)).Append("\",\"tree\":");
        WriteNode(sb, rootNode, minFile);
        sb.Append("}");
        return sb.ToString();
    }

    static long ComputeSize(D d)
    {
        long s = 0;
        foreach (var f in d.Files) s += f.Size;
        foreach (var c in d.Children) s += ComputeSize(c);
        d.Size = s;
        return s;
    }

    static void WriteNode(StringBuilder sb, D d, long minFile)
    {
        sb.Append("{\"n\":\"").Append(Esc(d.Name)).Append("\",\"s\":").Append(d.Size).Append(",\"c\":[");
        var list = new List<Ent>();
        foreach (var c in d.Children) list.Add(new Ent { Dir = c, Size = c.Size });
        long smallSum = 0; int smallCnt = 0;
        foreach (var f in d.Files)
        {
            if (f.Size >= minFile) list.Add(new Ent { FileName = f.Name, Size = f.Size });
            else { smallSum += f.Size; smallCnt++; }
        }
        list.Sort((a, b) => b.Size.CompareTo(a.Size));
        bool first = true;
        foreach (var e in list)
        {
            if (!first) sb.Append(',');
            first = false;
            if (e.Dir != null) WriteNode(sb, e.Dir, minFile);
            else sb.Append("{\"n\":\"").Append(Esc(e.FileName)).Append("\",\"s\":").Append(e.Size).Append("}");
        }
        if (smallCnt > 0)
        {
            if (!first) sb.Append(',');
            sb.Append("{\"n\":\"(").Append(smallCnt).Append(" small files)\",\"s\":").Append(smallSum).Append(",\"agg\":1,\"k\":").Append(smallCnt).Append("}");
        }
        sb.Append("]}");
    }

    static string Esc(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (char ch in s)
        {
            if (ch == '"') sb.Append("\\\"");
            else if (ch == '\\') sb.Append("\\\\");
            else if (ch == '\n') sb.Append("\\n");
            else if (ch == '\r') sb.Append("\\r");
            else if (ch == '\t') sb.Append("\\t");
            else if (ch < 32) sb.Append("\\u").Append(((int)ch).ToString("x4"));
            else sb.Append(ch);
        }
        return sb.ToString();
    }
}
'@

if (-not ('DiskTreemapScanner' -as [type])) {
    Add-Type -TypeDefinition $src -Language CSharp -ReferencedAssemblies @('System.Core')
}

$sw = [Diagnostics.Stopwatch]::StartNew()
$json = [DiskTreemapScanner]::Run($Root, $MinFile)
$sw.Stop()

$full = [IO.Path]::GetFullPath($Out)
[IO.File]::WriteAllText($full, $json, (New-Object Text.UTF8Encoding($false)))

$kb = [math]::Round(([IO.FileInfo]$full).Length / 1KB, 1)
Write-Host ("Scanned '{0}' in {1:n1}s -> {2} ({3} KB)" -f $Root, $sw.Elapsed.TotalSeconds, $full, $kb)
