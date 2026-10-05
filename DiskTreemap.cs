// DiskTreemap.exe  --  native Windows disk-usage treemap viewer
//
// A single self-contained executable (no browser, no server). On launch it
// shows a SpaceSniffer-style picker so you choose a drive / folder, then scans
// it in the background and renders a squarified treemap you can drill into.
// Right-click any cell for native file operations.
//
// Build (C# 5 compiler, .NET Framework 4.x):
//   csc.exe /target:winexe /optimize+ /out:DiskTreemap.exe DiskTreemap.cs
//
// Usage:
//   DiskTreemap.exe                 (open the picker)
//   DiskTreemap.exe <root>          (open the viewer at <root>)
//   DiskTreemap.exe <root> --out file.json --min bytes
//   DiskTreemap.exe --help

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Reflection;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// ---------------------------------------------------------------------------
// 程序集元信息：编译后成为 exe 的版本资源，在“属性 → 详细信息”里可见。
// 未签名二进制若连这些信息都没有，会被杀软的 ML 模型判为“来历不明”；
// 填全产品名 / 版权 / 版本是降低误报最直接的一步。
// ---------------------------------------------------------------------------
[assembly: AssemblyTitle("DiskTreemap")]
[assembly: AssemblyDescription("Disk usage treemap viewer (native WinForms)")]
[assembly: AssemblyProduct("DiskTreemap")]
[assembly: AssemblyCompany("DiskTreemap contributors")]
[assembly: AssemblyCopyright("Copyright (C) 2026 DiskTreemap contributors. MIT licensed.")]
[assembly: AssemblyVersion("1.0.4.0")]
[assembly: AssemblyFileVersion("1.0.4.0")]
[assembly: AssemblyInformationalVersion("1.0.4")]
[assembly: ComVisible(false)]

namespace DiskTreemap
{
    /* =====================================================================
       Data model + serializable tree
       ===================================================================== */

    internal sealed class Node
    {
        public string Name;
        public string FullPath;
        public long Size;
        public bool IsDir;
        public bool IsAgg;
        public int AggCount;
        public Node Parent;
        public List<Node> Children;

        public bool HasChildren { get { return Children != null && Children.Count > 0; } }
    }

    /* =====================================================================
       Loc  --  运行期本地化：系统界面语言为中文时显示中文，否则显示英文
       ===================================================================== */

    internal static class Loc
    {
        public static readonly bool IsZh;

        static Loc()
        {
            IsZh = DetectZh();
        }

        [DllImport("kernel32.dll")]
        private static extern ushort GetUserDefaultUILanguage();

        private static bool DetectZh()
        {
            // 首选 Win32“用户界面语言”，可覆盖 zh-CN / zh-TW / zh-HK
            try
            {
                int primary = GetUserDefaultUILanguage() & 0x3FF;
                if (primary != 0)
                    return primary == 0x04;   // LANG_CHINESE
            }
            catch { }
            // 回退到 .NET 的当前 UI 区域
            try { return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh"; }
            catch { }
            return false;
        }

        // 以中文原文为 key；英文缺失时回退中文
        public static string T(string zh)
        {
            if (IsZh) return zh;
            string en;
            return En.TryGetValue(zh, out en) ? en : zh;
        }

        private static readonly Dictionary<string, string> En =
            new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // ---- 选择盘符 / 文件夹对话框 ----
            { "0（显示所有文件）", "0 (show all files)" },
            { "1 MB（默认）", "1 MB (default)" },
            { "DiskTreemap - 选择要扫描的驱动器或文件夹", "DiskTreemap - Choose a drive or folder to scan" },
            { "选择驱动器：", "Select a drive:" },
            { "驱动器", "Drive" },
            { "类型", "Type" },
            { "文件系统", "File system" },
            { "总容量", "Total" },
            { "可用", "Free" },
            { "已用", "Used" },
            { "或指定文件夹：", "Or specify a folder:" },
            { "浏览...", "Browse..." },
            { "小于此大小的文件会被合并：", "Merge files smaller than:" },
            { "开始扫描", "Start scan" },
            { "取消", "Cancel" },
            { "本地磁盘", "Local disk" },
            { "可移动", "Removable" },
            { "网络", "Network" },
            { "其他", "Other" },
            { "选择要扫描的文件夹", "Choose a folder to scan" },
            { "请选择一个驱动器或文件夹。", "Please select a drive or folder." },
            { "路径无效：", "Invalid path: " },
            { "目录不存在：", "Directory not found: " },
            { "请选择要扫描的驱动器或文件夹", "Select a drive or folder to scan" },

            // ---- 标题栏 ----
            { "关闭", "Close" },
            { "最小化", "Minimize" },
            { "最大化 / 还原", "Maximize / Restore" },
            { "选择路径 / 重新选择扫描目标", "Choose path / pick another target" },
            { "选择路径...", "Choose path..." },
            { "后退 (Alt+←)", "Back (Alt+←)" },
            { "上一级 (Backspace)", "Up one level (Backspace)" },
            { "重新扫描 (F5)", "Rescan (F5)" },
            { "缩小", "Zoom out" },
            { "放大", "Zoom in" },
            { "适应窗口", "Fit window" },
            { "切换到浅色主题", "Switch to light theme" },
            { "切换到深色主题", "Switch to dark theme" },
            { "双击复制当前路径", "Double-click to copy path" },

            // ---- 扫描遮罩 / 状态栏 ----
            { "正在扫描...", "Scanning..." },
            { "扫描失败：", "Scan failed: " },
            { "未知错误", "Unknown error" },
            { "   （占总量 {0}）", "   ({0} of total)" },
            { "DiskTreemap - {0}   （{1}）", "DiskTreemap - {0}   ({1})" },
            { "文件夹", "Folder" },
            { "分组", "Group" },
            { "文件", "File" },
            { "{0}：{1}   {2}{3}", "{0}: {1}   {2}{3}" },
            { "   （双击进入：{0}）", "   (double-click to enter: {0})" },
            { "   （双击打开）", "   (double-click to open)" },

            // ---- 右键菜单 ----
            { "进入此目录", "Enter this folder" },
            { "打开", "Open" },
            { "在资源管理器中显示", "Show in Explorer" },
            { "属性", "Properties" },
            { "复制完整路径", "Copy full path" },
            { "复制名称", "Copy name" },
            { "移到回收站", "Move to Recycle Bin" },

            // ---- 原生操作错误 ----
            { "未选择对象", "No item selected" },
            { "聚合分组不可操作", "Aggregated group is not actionable" },
            { "该节点没有有效路径", "This node has no valid path" },
            { "超出扫描范围，已拒绝", "Outside the scanned root - refused" },
            { "路径不存在", "Path does not exist" },
            { "不能删除扫描根目录", "The scan root cannot be deleted" },
            { "不支持对链接/挂载点的操作", "Operations on links / mount points are not supported" },
            { "无法读取该路径的属性", "Cannot read the attributes of this path" },
            { "打开失败：", "Open failed: " },
            { "打开资源管理器失败：", "Failed to open Explorer: " },
            { "打开属性失败：", "Failed to open properties: " },
            { "确定要把下面这项移到回收站吗？\r\n\r\n{0}\r\n\r\n（通常可从回收站还原；若超出回收站配额或该盘未启用回收站，则会被永久删除）", "Move the following item to the Recycle Bin?\r\n\r\n{0}\r\n\r\n(Usually restorable from the Recycle Bin; if it exceeds the bin's quota, or the drive has no Recycle Bin, it is deleted permanently.)" },
            { "删除失败（代码 {0}）", "Delete failed (code {0})" },

            // ---- 命令行 ----
            { "--out 缺少参数", "--out requires a value" },
            { "--out 需要指定扫描根目录", "--out requires a scan root" },
            { "--min 缺少参数", "--min requires a value" },
            { "--min 必须是 >= 0 的整数（字节）", "--min must be an integer >= 0 (bytes)" },
            { "未知参数: ", "Unknown argument: " },
            { "只能指定一个扫描根目录", "Only one scan root can be specified" },
            { "无法解析根目录: ", "Cannot resolve the root path: " },
            { "目录不存在: ", "Directory not found: " },
            { "扫描已取消", "Scan cancelled" },
            { "写出 JSON 失败: ", "Failed to write JSON: " },
            { "扫描目录: ", "Scanning: " },
            { "完成: {0:n1}s  文件 {1:n0}  目录 {2:n0}  聚合节点 {3:n0}  最大深度 {4}", "Done: {0:n1}s  files {1:n0}  dirs {2:n0}  agg nodes {3:n0}  max depth {4}" },
            { "已写出 {0} ({1:n1} KB)", "Wrote {0} ({1:n1} KB)" },
            { "跳过 {0} 个无法读取的目录或文件（大小被低估）", "skipped {0} unreadable entries (sizes are under-reported)" },
            { "   （{0} 个目录无法读取）", "   ({0} entries could not be read)" },
            { "错误: ", "Error: " },
            { "发生未处理的错误：", "Unhandled error: " },
            { "DiskTreemap - 磁盘占用树状图（SpaceSniffer 的原生替代品）", "DiskTreemap - disk usage treemap (a native SpaceSniffer alternative)" },
            { "用法:", "Usage:" },
            { "  DiskTreemap.exe                 打开驱动器/文件夹选择器", "  DiskTreemap.exe                 open the drive / folder picker" },
            { "  DiskTreemap.exe <根目录>        直接打开查看器", "  DiskTreemap.exe <root>          open the viewer directly" },
            { "  DiskTreemap.exe <根目录> --out <文件> [--min <字节>]", "  DiskTreemap.exe <root> --out <file> [--min <bytes>]" },
            { "选项:", "Options:" },
            { "  --out <文件>    只扫描并写出 JSON，不打开界面", "  --out <file>    scan only, write JSON, no window" },
            { "  --min <字节>    小于该值的文件被聚合（默认 1048576）", "  --min <bytes>   merge files smaller than this (default 1048576)" },
            { "  --help          显示本帮助", "  --help          show this help" }
        };
    }

    /* =====================================================================
       Scanner  --  parallel directory walker producing an in-memory Node tree
       ===================================================================== */

    internal static class Scanner
    {
        private class Dir
        {
            public string Name;
            public List<Dir> Children = new List<Dir>();
            public List<FileEnt> Files = new List<FileEnt>();
            public long Size;
        }

        private struct FileEnt
        {
            public string Name;
            public long Size;
        }

        public class Stats
        {
            public long Dirs;
            public long Files;
            public long AggNodes;
            public long AggFiles;
            public long MaxDepth;
            public long Skipped;   // 无法读取的目录 / 条目数量（大小会被低估）
        }

        /* ---- 原生目录枚举 ----------------------------------------------------
           注意：WIN32_FIND_DATA 里的 FILETIME 必须声明成两个 uint。若图省事写成
           long，默认 8 字节对齐会让它后面的所有字段整体偏移 4 字节，读出来
           的名称 / 属性 / 大小全是垃圾数据（而且不报错）。               */

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint Low;
            public uint High;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WIN32_FIND_DATA
        {
            public uint Attributes;
            public FILETIME CreationTime;
            public FILETIME LastAccessTime;
            public FILETIME LastWriteTime;
            public uint SizeHigh;
            public uint SizeLow;
            public uint Reserved0;
            public uint Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AltFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileEx(string pattern, int infoLevel,
            out WIN32_FIND_DATA data, int searchOp, IntPtr filter, int flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextFile(IntPtr handle, out WIN32_FIND_DATA data);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FindClose(IntPtr handle);

        private static readonly IntPtr InvalidHandle = new IntPtr(-1);
        private const int FindExInfoBasic = 1;          // 不解析 8.3 短名 -> 明显更快
        private const int FindExSearchNameMatch = 0;
        private const int FindFirstExLargeFetch = 2;    // 用更大的枚举缓冲
        private const uint AttrDirectory = 0x10;
        private const uint AttrReparsePoint = 0x400;
        private const int ErrorFileNotFound = 2;        // 空目录
        private const int ErrorNoMoreFiles = 18;        // 枚举正常结束

        public static Node Scan(string root, long minFile, out Stats stats, Func<bool> cancelled)
        {
            DirectoryInfo rootDi = new DirectoryInfo(root);
            string rootName = rootDi.Name;
            if (string.IsNullOrEmpty(rootName)) rootName = rootDi.FullName;

            bool stop = false;
            long skipped = 0;   // 多线程累加，只能走 Interlocked

            ConcurrentDictionary<string, Dir> map =
                new ConcurrentDictionary<string, Dir>(StringComparer.OrdinalIgnoreCase);
            Dir rootNode = new Dir();
            rootNode.Name = rootName;
            map[rootDi.FullName] = rootNode;

            List<string> current = new List<string>();
            current.Add(rootDi.FullName);

            while (current.Count > 0)
            {
                if (cancelled != null && cancelled()) { stop = true; break; }
                ConcurrentBag<string> next = new ConcurrentBag<string>();
                try
                {
                    Parallel.ForEach(current,
                        new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2 },
                        delegate(string d)
                        {
                            if (stop) return;
                            if (cancelled != null && cancelled()) { stop = true; return; }
                            Dir node;
                            if (!map.TryGetValue(d, out node)) return;

                            // 用 FindFirstFileEx 而非 DirectoryInfo：名称 / 属性 / 大小
                            // 全部由同一次枚举带回，不再为每个条目补发系统调用，
                            // 也不解析 8.3 短名（FindExInfoBasic）。整盘扫描快 6~7 倍。
                            string prefix = d.EndsWith("\\") ? d : d + "\\";
                            WIN32_FIND_DATA data;
                            IntPtr h = FindFirstFileEx(prefix + "*",
                                FindExInfoBasic, out data, FindExSearchNameMatch,
                                IntPtr.Zero, FindFirstExLargeFetch);
                            if (h == InvalidHandle)
                            {
                                // 空目录不是错误，只有真正打不开才算一次跳过
                                if (Marshal.GetLastWin32Error() != ErrorFileNotFound)
                                    Interlocked.Increment(ref skipped);
                                return;
                            }
                            try
                            {
                                while (true)
                                {
                                    if (stop) return;
                                    string name = data.FileName;
                                    if (!string.IsNullOrEmpty(name) && name != "." && name != "..")
                                    {
                                        uint at = data.Attributes;
                                        // 跳过 junction / 符号链接（目录与文件都跳过）：既防止成环，
                                        // 也避免文件型链接被按目标大小重复计数
                                        if ((at & AttrReparsePoint) == 0)
                                        {
                                            if ((at & AttrDirectory) != 0)
                                            {
                                                Dir child = new Dir();
                                                child.Name = name;
                                                if (map.TryAdd(prefix + name, child))
                                                {
                                                    lock (node) { node.Children.Add(child); }
                                                    next.Add(prefix + name);
                                                }
                                            }
                                            else
                                            {
                                                FileEnt fe = new FileEnt();
                                                fe.Name = name;
                                                fe.Size = ((long)data.SizeHigh << 32) | (long)data.SizeLow;
                                                lock (node) { node.Files.Add(fe); }
                                            }
                                        }
                                    }
                                    if (!FindNextFile(h, out data))
                                    {
                                        // 正常结束是 ERROR_NO_MORE_FILES；其他错误算一次跳过
                                        if (Marshal.GetLastWin32Error() != ErrorNoMoreFiles)
                                            Interlocked.Increment(ref skipped);
                                        break;
                                    }
                                }
                            }
                            finally { FindClose(h); }
                        });
                }
                catch (AggregateException) { }

                if (stop) throw new OperationCanceledException();
                current = new List<string>(next);
            }

            ComputeSize(rootNode);
            map = null;   // 目录字典已完成使命：尽早释放，别和 Node 树一起常驻
            // 枚举阶段会为每个条目产生一个文件名字符串等大量短命对象。扫描现在很快，
            // 走到这里时它们多半还没被回收；先收一次，避免和随后的 Node 树叠加成峰值。
            GC.Collect();
            GC.WaitForPendingFinalizers();

            stats = new Stats();
            stats.Skipped = skipped;
            Node tree = ToNode(rootNode, rootDi.FullName, minFile, stats, 0);
            return tree;
        }

        private static long ComputeSize(Dir d)
        {
            long s = 0;
            foreach (FileEnt f in d.Files) s += f.Size;
            foreach (Dir c in d.Children) s += ComputeSize(c);
            d.Size = s;
            return s;
        }

        private static Node ToNode(Dir d, string fullPath, long minFile, Stats st, long depth)
        {
            Node node = new Node();
            node.Name = d.Name;
            node.FullPath = fullPath;
            node.IsDir = true;
            node.Size = d.Size;

            st.Dirs++;
            if (depth > st.MaxDepth) st.MaxDepth = depth;

            List<Node> kids = new List<Node>();

            foreach (Dir c in d.Children)
            {
                Node child = ToNode(c, Path.Combine(fullPath, c.Name), minFile, st, depth + 1);
                child.Parent = node;
                kids.Add(child);
            }
            d.Children = null;   // 转换完即释放，压住 Dir 树与 Node 树同时常驻的峰值

            long smallSum = 0;
            int smallCnt = 0;
            foreach (FileEnt f in d.Files)
            {
                st.Files++;
                if (f.Size >= minFile)
                {
                    Node fn = new Node();
                    fn.Name = f.Name;
                    fn.FullPath = Path.Combine(fullPath, f.Name);
                    fn.Size = f.Size;
                    fn.IsDir = false;
                    fn.Parent = node;
                    kids.Add(fn);
                }
                else { smallSum += f.Size; smallCnt++; }
            }

            if (smallCnt > 0)
            {
                Node agg = new Node();
                agg.Name = "(" + smallCnt.ToString(CultureInfo.InvariantCulture) + " small files)";
                agg.Size = smallSum;
                agg.IsDir = false;
                agg.IsAgg = true;
                agg.AggCount = smallCnt;
                agg.Parent = node;
                kids.Add(agg);
                st.AggNodes++;
                st.AggFiles += smallCnt;
            }

            kids.Sort(delegate(Node a, Node b) { return b.Size.CompareTo(a.Size); });
            node.Children = kids;
            return node;
        }

        /* ------------------------- JSON serialization --------------------- */

        // 直接写进 TextWriter：整盘扫描的 JSON 可达数十 MB，
        // 先在内存里拼成字符串会额外多出一份（写文件时还要再编码一份）。
        public static void WriteJson(TextWriter w, string rootFull, Node tree)
        {
            w.Write("{\"root\":\"");
            w.Write(Esc(rootFull));
            w.Write("\",\"tree\":");
            WriteJsonNode(w, tree);
            w.Write('}');
        }

        private static void WriteJsonNode(TextWriter w, Node d)
        {
            w.Write("{\"n\":\"");
            w.Write(Esc(d.Name));
            w.Write("\",\"s\":");
            w.Write(d.Size);
            w.Write(",\"c\":[");
            bool first = true;
            if (d.Children != null)
            {
                foreach (Node c in d.Children)
                {
                    if (!first) w.Write(',');
                    first = false;
                    WriteJsonNode(w, c);
                }
            }
            w.Write(']');
            if (d.IsAgg)
            {
                w.Write(",\"agg\":1,\"k\":");
                w.Write(d.AggCount);
            }
            w.Write('}');
        }

        private static string Esc(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length + 8);
            foreach (char ch in s)
            {
                if (ch == '"') sb.Append("\\\"");
                else if (ch == '\\') sb.Append("\\\\");
                else if (ch == '\n') sb.Append("\\n");
                else if (ch == '\r') sb.Append("\\r");
                else if (ch == '\t') sb.Append("\\t");
                else if (ch < 32) sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(ch);
            }
            return sb.ToString();
        }
    }

    /* =====================================================================
       Squarified treemap layout
       ===================================================================== */

    internal static class TreemapLayout
    {
        public static RectangleF[] Squarify(List<Node> nodes, RectangleF bounds)
        {
            int n = nodes.Count;
            RectangleF[] result = new RectangleF[n];
            if (n == 0) return result;

            double w = bounds.Width, h = bounds.Height;
            if (w <= 0 || h <= 0) return result;

            double total = 0;
            for (int i = 0; i < n; i++) total += Math.Max(0, nodes[i].Size);

            double area = w * h;
            double[] a = new double[n];
            for (int i = 0; i < n; i++)
            {
                double sz = Math.Max(0, nodes[i].Size);
                double frac = total > 0 ? sz / total : 1.0 / n;
                a[i] = frac * area;
                if (a[i] <= 0) a[i] = 0.0001;     // keep the maths finite for empty nodes
            }

            double x = bounds.X, y = bounds.Y, rw = w, rh = h;
            int idx = 0;
            while (idx < n)
            {
                double side = Math.Min(rw, rh);
                if (side <= 0) break;

                int start = idx;
                double sum = a[idx];
                double worst = Worst(a, start, 1, sum, side);
                idx++;
                while (idx < n)
                {
                    double ns = sum + a[idx];
                    double nw = Worst(a, start, idx - start + 1, ns, side);
                    if (nw > worst) break;
                    sum = ns; worst = nw; idx++;
                }

                if (rw >= rh)
                {
                    double stripW = sum / rh;
                    if (stripW <= 0) stripW = rw;
                    double oy = y;
                    for (int k = start; k < idx; k++)
                    {
                        double cellH = a[k] / stripW;
                        result[k] = new RectangleF((float)x, (float)oy, (float)stripW, (float)cellH);
                        oy += cellH;
                    }
                    x += stripW; rw -= stripW;
                }
                else
                {
                    double stripH = sum / rw;
                    if (stripH <= 0) stripH = rh;
                    double ox = x;
                    for (int k = start; k < idx; k++)
                    {
                        double cellW = a[k] / stripH;
                        result[k] = new RectangleF((float)ox, (float)y, (float)cellW, (float)stripH);
                        ox += cellW;
                    }
                    y += stripH; rh -= stripH;
                }
            }
            return result;
        }

        private static double Worst(double[] a, int start, int count, double sum, double side)
        {
            double s2 = sum * sum;
            double side2 = side * side;
            double worst = 0;
            for (int k = start; k < start + count; k++)
            {
                double r = a[k];
                if (r <= 0) continue;
                double ratio = side2 * r / s2;
                double w = ratio > 0 ? Math.Max(ratio, 1.0 / ratio) : double.MaxValue;
                if (w > worst) worst = w;
            }
            return worst;
        }
    }

    /* =====================================================================
       Palette  --  colours by node kind / file extension
       ===================================================================== */

    internal sealed class PaletteData
    {
        public readonly bool Dark;
        public readonly Color CanvasBg;
        public readonly Color CellText;
        public readonly Color HeaderText;
        public readonly Color FrameLine;
        public readonly Color[] DirShades;
        public readonly Color[] FileShades;
        public readonly Color[] ContainerShades;
        public readonly Color[] HeaderShades;

        public PaletteData(bool dark, Color canvasBg, Color cellText, Color headerText, Color frameLine,
                           Color[] dirShades, Color[] fileShades,
                           Color[] containerShades, Color[] headerShades)
        {
            Dark = dark;
            CanvasBg = canvasBg;
            CellText = cellText;
            HeaderText = headerText;
            FrameLine = frameLine;
            DirShades = dirShades;
            FileShades = fileShades;
            ContainerShades = containerShades;
            HeaderShades = headerShades;
        }

        public Color For(Node n, int depth)
        {
            if (n.IsDir && !n.IsAgg)
            {
                int i = ((depth % DirShades.Length) + DirShades.Length) % DirShades.Length;
                return DirShades[i];
            }
            return FileShades[Palette.Category(n.Name)];
        }

        public Color Container(int depth)
        {
            int i = ((depth % ContainerShades.Length) + ContainerShades.Length) % ContainerShades.Length;
            return ContainerShades[i];
        }

        public Color Header(int depth)
        {
            int i = ((depth % HeaderShades.Length) + HeaderShades.Length) % HeaderShades.Length;
            return HeaderShades[i];
        }

        // 深色主题下“更暗的描边”会糊掉，改为提亮；浅色主题维持压暗
        public Color BorderOf(Color c)
        {
            return Dark ? Palette.Shift(c, 42) : Palette.Shift(c, -60);
        }
    }

    internal static class Palette
    {
        public static readonly PaletteData Light = BuildLight();
        public static readonly PaletteData Dark = BuildDark();
        public static PaletteData Current = Light;

        public static Color CanvasBg { get { return Current.CanvasBg; } }
        public static Color CellText { get { return Current.CellText; } }
        public static Color HeaderText { get { return Current.HeaderText; } }
        public static Color FrameLine { get { return Current.FrameLine; } }

        public static Color For(Node n, int depth) { return Current.For(n, depth); }
        public static Color Container(int depth) { return Current.Container(depth); }
        public static Color Header(int depth) { return Current.Header(depth); }
        public static Color BorderOf(Color c) { return Current.BorderOf(c); }

        public static Color Darken(Color c) { return Shift(c, -60); }

        internal static Color Shift(Color c, int d)
        {
            return Color.FromArgb(
                Math.Max(0, Math.Min(255, c.R + d)),
                Math.Max(0, Math.Min(255, c.G + d)),
                Math.Max(0, Math.Min(255, c.B + d)));
        }

        private static PaletteData BuildLight()
        {
            return new PaletteData(false,
                Color.FromArgb(245, 246, 248),
                Color.FromArgb(28, 34, 42),
                Color.FromArgb(46, 58, 76),
                Color.FromArgb(196, 205, 219),
                new Color[]
                {
                    Color.FromArgb(206, 224, 242),
                    Color.FromArgb(178, 204, 230),
                    Color.FromArgb(150, 184, 216),
                    Color.FromArgb(122, 164, 200)
                },
                new Color[]
                {
                    Color.FromArgb(197, 168, 224),
                    Color.FromArgb(238, 186, 122),
                    Color.FromArgb(170, 212, 158),
                    Color.FromArgb(226, 205, 128),
                    Color.FromArgb(232, 152, 152),
                    Color.FromArgb(140, 205, 205),
                    Color.FromArgb(176, 178, 190),
                    Color.FromArgb(208, 212, 218)
                },
                new Color[]
                {
                    Color.FromArgb(248, 250, 253),
                    Color.FromArgb(253, 253, 255)
                },
                new Color[]
                {
                    Color.FromArgb(228, 235, 246),
                    Color.FromArgb(237, 242, 250)
                });
        }

        private static PaletteData BuildDark()
        {
            return new PaletteData(true,
                Color.FromArgb(30, 32, 36),
                Color.FromArgb(235, 238, 242),
                Color.FromArgb(226, 231, 238),
                Color.FromArgb(70, 76, 86),
                new Color[]
                {
                    Color.FromArgb(58, 82, 108),
                    Color.FromArgb(68, 96, 126),
                    Color.FromArgb(80, 112, 146),
                    Color.FromArgb(94, 130, 168)
                },
                new Color[]
                {
                    Color.FromArgb(112, 84, 140),
                    Color.FromArgb(140, 104, 62),
                    Color.FromArgb(78, 116, 84),
                    Color.FromArgb(138, 120, 58),
                    Color.FromArgb(146, 82, 82),
                    Color.FromArgb(62, 122, 122),
                    Color.FromArgb(96, 102, 116),
                    Color.FromArgb(82, 88, 98)
                },
                new Color[]
                {
                    Color.FromArgb(34, 37, 42),
                    Color.FromArgb(38, 42, 48)
                },
                new Color[]
                {
                    Color.FromArgb(48, 54, 64),
                    Color.FromArgb(52, 58, 70)
                });
        }

        internal static int Category(string name)
        {
            string ext = "";
            int dot = name.LastIndexOf('.');
            if (dot >= 0 && dot < name.Length - 1) ext = name.Substring(dot + 1).ToLowerInvariant();
            switch (ext)
            {
                case "mp4": case "mkv": case "avi": case "mov": case "wmv": case "flv":
                case "webm": case "m4v": case "mpg": case "mpeg": case "rmvb": case "ts":
                    return 0;
                case "mp3": case "wav": case "flac": case "aac": case "ogg": case "m4a":
                case "wma": case "ape": case "opus":
                    return 1;
                case "jpg": case "jpeg": case "png": case "gif": case "bmp": case "webp":
                case "tif": case "tiff": case "svg": case "ico": case "heic": case "raw":
                    return 2;
                case "zip": case "rar": case "7z": case "tar": case "gz": case "bz2":
                case "xz": case "iso": case "cab": case "zst":
                    return 3;
                case "pdf": case "doc": case "docx": case "xls": case "xlsx": case "ppt":
                case "pptx": case "txt": case "md": case "rtf": case "csv": case "epub":
                    return 4;
                case "cs": case "cpp": case "c": case "h": case "hpp": case "js":
                case "py": case "java": case "go": case "rs": case "rb": case "php":
                case "lua": case "html": case "css": case "json": case "xml": case "ps1":
                case "sh": case "sql": case "yml": case "yaml":
                    return 5;
                case "exe": case "dll": case "msi": case "sys": case "com": case "bin":
                case "bat": case "cmd": case "scr":
                    return 6;
                default:
                    return 7;
            }
        }
    }

    /* =====================================================================
       Theme  --  teal accent + light / dark chrome palette
       ===================================================================== */

    internal static class Theme
    {
        public static readonly Color Accent = Color.FromArgb(10, 178, 158);
        public static readonly Color AccentDark = Color.FromArgb(8, 150, 134);
        public static readonly Color AccentLight = Color.FromArgb(52, 202, 184);
        public static readonly Color CloseColor = Color.FromArgb(255, 95, 87);
        public static readonly Color MinColor = Color.FromArgb(254, 188, 46);
        public static readonly Color MaxColor = Color.FromArgb(40, 200, 64);

        public static bool IsDark;

        public static Color WindowBg;
        public static Color TitleBarBg;
        public static Color TitleBarText;
        public static Color Border;
        public static Color ControlBg;
        public static Color ControlText;
        public static Color SubtleText;
        public static Color HoverBg;
        public static Color PressBg;
        public static Color PillBg;
        public static Color StatusBg;

        static Theme() { Set(false); }

        public static Color AccentColor { get { return IsDark ? AccentLight : Accent; } }

        public static void Set(bool dark)
        {
            IsDark = dark;
            Palette.Current = dark ? Palette.Dark : Palette.Light;

            if (dark)
            {
                WindowBg = Color.FromArgb(30, 32, 36);
                TitleBarBg = Color.FromArgb(38, 40, 45);
                TitleBarText = Color.FromArgb(232, 236, 242);
                Border = Color.FromArgb(58, 62, 70);
                ControlBg = Color.FromArgb(48, 51, 57);
                ControlText = Color.FromArgb(232, 236, 242);
                SubtleText = Color.FromArgb(150, 158, 170);
                HoverBg = Color.FromArgb(56, 60, 68);
                PressBg = Color.FromArgb(68, 73, 82);
                PillBg = Color.FromArgb(46, 49, 55);
                StatusBg = Color.FromArgb(33, 35, 40);
            }
            else
            {
                WindowBg = Color.FromArgb(245, 246, 248);
                TitleBarBg = Color.FromArgb(248, 249, 251);
                TitleBarText = Color.FromArgb(28, 34, 42);
                Border = Color.FromArgb(214, 219, 226);
                ControlBg = Color.FromArgb(255, 255, 255);
                ControlText = Color.FromArgb(28, 34, 42);
                SubtleText = Color.FromArgb(120, 128, 140);
                HoverBg = Color.FromArgb(233, 236, 241);
                PressBg = Color.FromArgb(219, 224, 231);
                PillBg = Color.FromArgb(255, 255, 255);
                StatusBg = Color.FromArgb(248, 249, 251);
            }
        }
    }

    /* =====================================================================
       Prefs  --  用户偏好持久化（HKCU\Software\DiskTreemap）
       ===================================================================== */

    internal static class Prefs
    {
        private const string RegKeyPath = @"Software\DiskTreemap";
        private const string ThemeDarkValue = "ThemeDark";

        // 手动指定过主题则返回该值；从未指定过返回 null（表示跟随系统）
        public static bool? ThemeDark
        {
            get
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegKeyPath))
                    {
                        if (k != null)
                        {
                            object v = k.GetValue(ThemeDarkValue);
                            if (v is int) return (int)v != 0;
                        }
                    }
                }
                catch { }
                return null;
            }
        }

        public static void SaveThemeDark(bool dark)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RegKeyPath))
                {
                    if (k != null) k.SetValue(ThemeDarkValue, dark ? 1 : 0, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        // 读取系统“应用主题”设置（AppsUseLightTheme）
        public static bool SystemPrefersDark()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("AppsUseLightTheme");
                        if (v is int) return (int)v == 0;
                    }
                }
            }
            catch { }
            return false;
        }

        // 启动时的主题：有手动记录就用记录，否则跟随系统
        public static bool StartupDark()
        {
            bool? saved = ThemeDark;
            return saved.HasValue ? saved.Value : SystemPrefersDark();
        }
    }

    /* =====================================================================
       Style  --  light Apple-ish renderer + rounded window corners
       ===================================================================== */

    internal sealed class AppleColorTable : ProfessionalColorTable
    {
        private static Color Bar { get { return Theme.StatusBg; } }
        private static Color Hover { get { return Theme.HoverBg; } }
        private static Color Press { get { return Theme.PressBg; } }
        private static Color Line { get { return Theme.Border; } }

        public override Color ToolStripGradientBegin { get { return Bar; } }
        public override Color ToolStripGradientMiddle { get { return Bar; } }
        public override Color ToolStripGradientEnd { get { return Bar; } }
        public override Color ToolStripBorder { get { return Line; } }
        public override Color ToolStripDropDownBackground { get { return Theme.ControlBg; } }

        public override Color ButtonSelectedGradientBegin { get { return Hover; } }
        public override Color ButtonSelectedGradientMiddle { get { return Hover; } }
        public override Color ButtonSelectedGradientEnd { get { return Hover; } }
        public override Color ButtonSelectedBorder { get { return Line; } }
        public override Color ButtonSelectedHighlight { get { return Hover; } }

        public override Color ButtonPressedGradientBegin { get { return Press; } }
        public override Color ButtonPressedGradientMiddle { get { return Press; } }
        public override Color ButtonPressedGradientEnd { get { return Press; } }
        public override Color ButtonPressedBorder { get { return Line; } }
        public override Color ButtonPressedHighlight { get { return Press; } }

        public override Color ButtonCheckedGradientBegin { get { return Press; } }
        public override Color ButtonCheckedGradientMiddle { get { return Press; } }
        public override Color ButtonCheckedGradientEnd { get { return Press; } }
        public override Color ButtonCheckedHighlight { get { return Press; } }

        public override Color StatusStripGradientBegin { get { return Bar; } }
        public override Color StatusStripGradientEnd { get { return Bar; } }

        public override Color SeparatorDark { get { return Line; } }
        public override Color SeparatorLight { get { return Color.FromArgb(244, 245, 248); } }

        public override Color MenuBorder { get { return Line; } }
        public override Color MenuItemBorder { get { return Line; } }
        public override Color MenuItemSelected { get { return Hover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Hover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Hover; } }
        public override Color MenuItemPressedGradientBegin { get { return Bar; } }
        public override Color MenuItemPressedGradientEnd { get { return Bar; } }
        public override Color ImageMarginGradientBegin { get { return Bar; } }
        public override Color ImageMarginGradientMiddle { get { return Bar; } }
        public override Color ImageMarginGradientEnd { get { return Bar; } }
    }

    internal static class Style
    {
        // 单文件程序没有独立的图标资源：从自身 exe 取出内嵌图标给窗口 / 任务栏用。
        // 只取一次并缓存——ExtractAssociatedIcon 每次都新建 Icon，反复调用会白占 GDI 句柄。
        private static Icon _appIcon;
        private static bool _appIconLoaded;

        public static Icon AppIcon()
        {
            if (!_appIconLoaded)
            {
                _appIconLoaded = true;
                try { _appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
                catch { _appIcon = null; }
            }
            return _appIcon;
        }

        // 主窗口为无边框；铺满客户区的子控件必须在窗口边框区把命中测试还给 Form，
        // 否则 Form 收不到 WM_NCHITTEST，八向边缘拉伸就会失效。
        internal const int WM_NCHITTEST = 0x0084;
        internal const int HTCLIENT = 1;
        internal const int HTTRANSPARENT = -1;
        internal const int EdgeBorder = 6;

        internal static void DeferAtEdge(Control c, ref Message m)
        {
            Form f = c.FindForm();
            if (f == null || f.WindowState != FormWindowState.Normal) return;
            int lp = unchecked((int)(long)m.LParam);
            Point p = f.PointToClient(new Point(unchecked((short)lp), unchecked((short)(lp >> 16))));
            if (p.X <= EdgeBorder || p.Y <= EdgeBorder ||
                p.X >= f.ClientSize.Width - EdgeBorder || p.Y >= f.ClientSize.Height - EdgeBorder)
                m.Result = (IntPtr)HTTRANSPARENT;
        }

        public static ToolStripRenderer NewRenderer()
        {
            ToolStripProfessionalRenderer r = new ToolStripProfessionalRenderer(new AppleColorTable());
            r.RoundedEdges = true;
            return r;
        }

        // 扁平原生按钮：primary 为强调色实心，否则为带描边的浅色
        public static void FlatButton(Button b, bool primary)
        {
            if (b == null) return;
            b.FlatStyle = FlatStyle.Flat;
            b.UseVisualStyleBackColor = false;
            if (primary)
            {
                b.FlatAppearance.BorderSize = 0;
                b.BackColor = Theme.AccentColor;
                b.ForeColor = Color.White;
                b.FlatAppearance.MouseOverBackColor = Theme.AccentDark;
                b.FlatAppearance.MouseDownBackColor = Theme.AccentDark;
            }
            else
            {
                b.FlatAppearance.BorderSize = 1;
                b.FlatAppearance.BorderColor = Theme.Border;
                b.BackColor = Theme.ControlBg;
                b.ForeColor = Theme.ControlText;
                b.FlatAppearance.MouseOverBackColor = Theme.HoverBg;
                b.FlatAppearance.MouseDownBackColor = Theme.PressBg;
            }
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void RoundCorners(Form f)
        {
            if (f == null) return;

            bool rounded = false;
            try
            {
                int pref = 2;   // DWMWCP_ROUND (Windows 11)
                rounded = DwmSetWindowAttribute(f.Handle, 33, ref pref, sizeof(int)) == 0;
            }
            catch { }

            try
            {
                // DWMWA_BORDER_COLOR(34) = DWMWA_COLOR_NONE(0xFFFFFFFE)  -> 去掉灰色描边
                int none = unchecked((int)0xFFFFFFFE);
                DwmSetWindowAttribute(f.Handle, 34, ref none, sizeof(int));
            }
            catch { }

            if (rounded) return;
            ApplyRegion(f);
            f.Resize += delegate { ApplyRegion(f); };
        }

        private static void ApplyRegion(Form f)
        {
            try
            {
                // 赋新 Region 前先接住旧的：WinForms 不会替我们释放它
                Region old = f.Region;
                if (f.WindowState == FormWindowState.Maximized || f.Width < 24 || f.Height < 24)
                {
                    f.Region = null;
                }
                else
                {
                    using (GraphicsPath gp = RoundedPath(new Rectangle(0, 0, f.Width, f.Height), 12))
                        f.Region = new Region(gp);
                }
                if (old != null) old.Dispose();
            }
            catch { }
        }

        private static GraphicsPath RoundedPath(Rectangle r, int radius)
        {
            GraphicsPath gp = new GraphicsPath();
            int d = radius * 2;
            gp.AddArc(r.X, r.Y, d, d, 180, 90);
            gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }
    }

    /* =====================================================================
       PathPickerForm  --  SpaceSniffer-style drive / folder chooser
       ===================================================================== */

    internal sealed class PathPickerForm : Form
    {
        private ListView _list;
        private TextBox _path;
        private ComboBox _min;
        private Button _ok;

        private static readonly long[] MinValues = new long[] { 0, 65536, 1048576, 10485760 };
        private static readonly string[] MinLabels = new string[]
        {
            Loc.T("0（显示所有文件）"), Loc.T("64 KB"), Loc.T("1 MB（默认）"), Loc.T("10 MB")
        };

        public string SelectedPath;
        public long MinFile = 1048576;

        public PathPickerForm(string initialPath, long initialMin)
        {
            Text = Loc.T("DiskTreemap - 选择要扫描的驱动器或文件夹");
            Icon = Style.AppIcon();
            FormBorderStyle = FormBorderStyle.None;   // 与主窗口一致：无边框 + 圆角 + 自绘标题条
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(600, 460);
            Font = new Font("Segoe UI", 9f);
            BackColor = Theme.WindowBg;
            ForeColor = Theme.ControlText;

            // 顶部标题条：标题文字 + 关闭按钮，条内空白可拖动窗口
            Panel head = new Panel();
            head.Dock = DockStyle.Top;
            head.Height = 40;
            head.BackColor = Theme.TitleBarBg;
            head.Paint += HeadPaint;
            head.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) DragWindow();
            };

            Button closeBtn = new Button();
            closeBtn.Text = "\uE8BB";
            closeBtn.Font = new Font("Segoe MDL2 Assets", 9f);
            closeBtn.Size = new Size(30, 26);
            closeBtn.Location = new Point(560, 7);
            closeBtn.TabStop = false;
            closeBtn.FlatStyle = FlatStyle.Flat;
            closeBtn.UseVisualStyleBackColor = false;   // 否则按钮底色会被视觉样式覆盖，与标题条不融合
            closeBtn.FlatAppearance.BorderSize = 0;
            closeBtn.BackColor = Theme.TitleBarBg;
            closeBtn.ForeColor = Theme.SubtleText;
            closeBtn.FlatAppearance.MouseOverBackColor = Theme.CloseColor;
            closeBtn.FlatAppearance.MouseDownBackColor = Theme.CloseColor;
            closeBtn.Click += delegate { Close(); };
            head.Controls.Add(closeBtn);
            head.Resize += delegate { closeBtn.Location = new Point(Math.Max(0, head.ClientSize.Width - 40), 7); };

            Panel body = new Panel();
            body.Dock = DockStyle.Fill;
            body.BackColor = Theme.WindowBg;
            body.ForeColor = Theme.ControlText;

            Label caption = new Label();
            caption.Text = Loc.T("选择驱动器：");
            caption.Location = new Point(12, 10);
            caption.AutoSize = true;
            body.Controls.Add(caption);

            _list = new ListView();
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.GridLines = false;
            _list.HideSelection = false;
            _list.BackColor = Theme.ControlBg;
            _list.ForeColor = Theme.ControlText;
            _list.Location = new Point(12, 32);
            _list.Size = new Size(576, 240);
            _list.Columns.Add(Loc.T("驱动器"), 90);
            _list.Columns.Add(Loc.T("类型"), 70);
            _list.Columns.Add(Loc.T("文件系统"), 84);   // 宽度照顾英文表头
            _list.Columns.Add(Loc.T("总容量"), 88, HorizontalAlignment.Right);
            _list.Columns.Add(Loc.T("可用"), 84, HorizontalAlignment.Right);
            _list.Columns.Add(Loc.T("已用"), 88, HorizontalAlignment.Right);
            _list.DoubleClick += delegate { Accept(); };
            body.Controls.Add(_list);

            Label lp = new Label();
            lp.Text = Loc.T("或指定文件夹：");
            lp.Location = new Point(12, 284);
            lp.AutoSize = true;
            body.Controls.Add(lp);

            _path = new TextBox();
            _path.Location = new Point(12, 304);
            _path.Size = new Size(486, 24);
            _path.BorderStyle = BorderStyle.FixedSingle;
            _path.BackColor = Theme.ControlBg;
            _path.ForeColor = Theme.ControlText;
            if (!string.IsNullOrEmpty(initialPath)) _path.Text = initialPath;
            body.Controls.Add(_path);

            // 手动改动路径即取消列表选中，保证“最后一次操作生效”
            _path.TextChanged += delegate
            {
                if (_list.SelectedItems.Count > 0) _list.SelectedItems[0].Selected = false;
            };

            Button browse = new Button();
            browse.Text = Loc.T("浏览...");
            browse.Location = new Point(506, 303);
            browse.Size = new Size(82, 26);
            Style.FlatButton(browse, false);
            browse.Click += delegate { Browse(); };
            body.Controls.Add(browse);

            Label lm = new Label();
            lm.Text = Loc.T("小于此大小的文件会被合并：");
            lm.Location = new Point(12, 342);
            lm.AutoSize = true;
            body.Controls.Add(lm);

            _min = new ComboBox();
            _min.DropDownStyle = ComboBoxStyle.DropDownList;
            _min.FlatStyle = FlatStyle.Flat;
            _min.BackColor = Theme.ControlBg;
            _min.ForeColor = Theme.ControlText;
            _min.Location = new Point(196, 339);
            _min.Size = new Size(160, 24);
            _min.Items.AddRange(MinLabels);
            _min.SelectedIndex = NearestMinIndex(initialMin);
            body.Controls.Add(_min);

            _ok = new Button();
            _ok.Text = Loc.T("开始扫描");
            _ok.Location = new Point(376, 372);
            _ok.Size = new Size(100, 30);
            Style.FlatButton(_ok, true);
            _ok.Click += delegate { Accept(); };
            body.Controls.Add(_ok);

            Button cancel = new Button();
            cancel.Text = Loc.T("取消");
            cancel.Location = new Point(488, 372);
            cancel.Size = new Size(100, 30);
            Style.FlatButton(cancel, false);
            cancel.DialogResult = DialogResult.Cancel;
            body.Controls.Add(cancel);

            AcceptButton = _ok;
            CancelButton = cancel;

            Controls.Add(body);
            Controls.Add(head);   // Fill 先加、Top 后加，Dock 才会正确让出空间

            PopulateDrives();
        }

        // 命令行传进来的阈值未必正好是某个预设值（选择框本来就只有粗档位），
        // 取最接近的一档，而不是把用户给的 --min 直接丢掉。
        private static int NearestMinIndex(long bytes)
        {
            int best = 2;                          // 无输入时的默认：1 MB
            long bestDelta = long.MaxValue;
            for (int i = 0; i < MinValues.Length; i++)
            {
                long delta = Math.Abs(MinValues[i] - bytes);
                if (delta < bestDelta) { bestDelta = delta; best = i; }
            }
            return best;
        }

        private void HeadPaint(object sender, PaintEventArgs e)
        {
            Control head = (Control)sender;
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            Rectangle r = new Rectangle(14, 0, Math.Max(0, head.Width - 52), head.Height - 1);
            TextRenderer.DrawText(e.Graphics, Text, Font, r, Theme.TitleBarText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            using (Pen pen = new Pen(Theme.Border))
                e.Graphics.DrawLine(pen, 0, head.Height - 1, head.Width, head.Height - 1);
        }

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTCAPTION = 2;

        private void DragWindow()
        {
            try
            {
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            }
            catch { }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;   // CS_DROPSHADOW：无边框窗口也带柔和投影
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Style.RoundCorners(this);
        }

        private void PopulateDrives()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    // 单个驱动器读取失败（拔出的 U 盘、断线的网络盘等）不能连累整份列表
                    try
                    {
                        if (!d.IsReady) continue;
                        DriveType t = d.DriveType;
                        if (t != DriveType.Fixed && t != DriveType.Removable && t != DriveType.Network)
                            continue;

                        string typeText;
                        switch (t)
                        {
                            case DriveType.Fixed: typeText = Loc.T("本地磁盘"); break;
                            case DriveType.Removable: typeText = Loc.T("可移动"); break;
                            case DriveType.Network: typeText = Loc.T("网络"); break;
                            default: typeText = Loc.T("其他"); break;
                        }

                        ListViewItem it = new ListViewItem(d.Name);
                        it.SubItems.Add(typeText);
                        it.SubItems.Add(d.DriveFormat);
                        it.SubItems.Add(Fmt(d.TotalSize));
                        it.SubItems.Add(Fmt(d.AvailableFreeSpace));
                        it.SubItems.Add(Fmt(d.TotalSize - d.AvailableFreeSpace));
                        it.Tag = d.Name;
                        _list.Items.Add(it);
                    }
                    catch { }
                }
            }
            catch { }
            _list.EndUpdate();
        }

        private void Browse()
        {
            using (FolderBrowserDialog fb = new FolderBrowserDialog())
            {
                fb.Description = Loc.T("选择要扫描的文件夹");
                fb.ShowNewFolderButton = false;
                if (!string.IsNullOrEmpty(_path.Text) && Directory.Exists(_path.Text))
                    fb.SelectedPath = _path.Text;
                if (fb.ShowDialog(this) == DialogResult.OK)
                    _path.Text = fb.SelectedPath;
            }
        }

        private void Accept()
        {
            // 列表里明确选中的驱动器优先；否则用文本框里的路径
            string p = _list.SelectedItems.Count == 1
                ? (string)_list.SelectedItems[0].Tag
                : _path.Text;

            if (string.IsNullOrEmpty(p)) p = "";
            p = p.Trim().Trim('"');
            if (p.Length == 0)
            {
                MessageBox.Show(this, Loc.T("请选择一个驱动器或文件夹。"), "DiskTreemap",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string full;
            try { full = Path.GetFullPath(p); }
            catch
            {
                MessageBox.Show(this, Loc.T("路径无效：") + p, "DiskTreemap",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!Directory.Exists(full))
            {
                MessageBox.Show(this, Loc.T("目录不存在：") + full, "DiskTreemap",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SelectedPath = full;
            MinFile = MinValues[_min.SelectedIndex < 0 ? 2 : _min.SelectedIndex];
            DialogResult = DialogResult.OK;
            Close();
        }

        internal static string Fmt(long bytes)
        {
            if (bytes < 0) bytes = 0;
            string[] u = new string[] { "B", "KB", "MB", "GB", "TB", "PB" };
            double v = bytes;
            int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return v.ToString(i == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + u[i];
        }
    }

    /* =====================================================================
       TreemapCanvas  --  double-buffered squarified treemap with zoom / pan
       ===================================================================== */

    internal sealed class TreemapCanvas : Control
    {
        private sealed class Placed
        {
            public Node Node;
            public RectangleF Rect;
            public RectangleF Header;
            public bool HasHeader;
            public int Depth;
        }

        private const int MaxDepth = 8;

        private const float FrameInsetPx = 10f;   // 窗口内容与树图之间的留白
        private const float CellPadPx = 3f;       // 容器边框与内部子项之间的留白
        private float _headerScreen = 17f;        // 容器标题栏高度（屏幕像素，随字体 / DPI 计算）

        private Node _focus;
        private float _scale = 1f;
        private float _tx, _ty;
        private int _worldW = 1, _worldH = 1;

        private readonly List<Placed> _placed = new List<Placed>();
        private Node _hover;
        private Node _selected;

        private bool _dragging;
        private bool _leftDown;   // 不能用 _downScreen == Point.Empty 判断：正好点在 (0,0) 时会误判
        private Point _downScreen;
        private float _downTx, _downTy;

        private readonly Font _font = new Font("Segoe UI", 8.25f);
        private readonly Font _fontBold = new Font("Segoe UI", 8.25f, FontStyle.Bold);
        private readonly SolidBrush _fill = new SolidBrush(Color.Black);
        private readonly Pen _border = new Pen(Color.Black);
        private readonly SolidBrush _text = new SolidBrush(Palette.CellText);
        private readonly SolidBrush _chip = new SolidBrush(Color.FromArgb(205, 255, 255, 255));
        private readonly SolidBrush _empty = new SolidBrush(Color.FromArgb(120, 120, 120));
        private readonly Pen _hoverPen = new Pen(Color.FromArgb(255, 255, 255), 2f);
        private readonly Pen _selPen = new Pen(Color.FromArgb(20, 20, 20), 2f);
        private readonly StringFormat _fmt = new StringFormat();
        private readonly StringFormat _fmtHead = new StringFormat();
        private readonly StringFormat _measure = new StringFormat();

        public event Action<Node> Activated;
        public event Action<Node> OpenRequested;
        public event Action<Node> MenuRequested;
        public event Action<Node> Hovered;
        public event Action<Node> SelectionChanged;

        public TreemapCanvas()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            ApplyTheme();
            _fmt.Trimming = StringTrimming.EllipsisCharacter;
            _fmt.FormatFlags = StringFormatFlags.NoWrap;
            _fmt.LineAlignment = StringAlignment.Near;
            _fmtHead.Trimming = StringTrimming.EllipsisCharacter;
            _fmtHead.FormatFlags = StringFormatFlags.NoWrap;
            _fmtHead.LineAlignment = StringAlignment.Center;   // 垂直居中，杜绝下沿被裁
            _measure.FormatFlags = StringFormatFlags.NoWrap;
            _measure.LineAlignment = StringAlignment.Near;
        }

        // 主题切换后重新着色所有画笔（由 MainForm 在切换主题时调用）
        public void ApplyTheme()
        {
            BackColor = Palette.CanvasBg;
            _text.Color = Palette.CellText;
            _empty.Color = Theme.IsDark ? Color.FromArgb(130, 136, 146) : Color.FromArgb(120, 120, 120);
            _hoverPen.Color = Theme.IsDark ? Theme.AccentLight : Color.FromArgb(255, 255, 255);
            _selPen.Color = Theme.IsDark ? Color.FromArgb(235, 238, 242) : Color.FromArgb(20, 20, 20);
            Invalidate();
        }

        public Node SelectedNode { get { return _selected; } }

        public void SetFocus(Node node)
        {
            _focus = node;
            _hover = null;
            _selected = null;
            // 默认按 1:1 铺满窗口（仅留少量边框空隙）；想要四周留白可点“适应”或“－”
            _scale = 1f;
            _tx = 0f;
            _ty = 0f;
            Invalidate();
        }

        public void ResetView()
        {
            // 约 0.8 倍并居中：四周留出舒服的空白（与“点一次 -”的观感一致）
            _scale = 0.8f;
            float w = Math.Max(1, _worldW);
            float h = Math.Max(1, _worldH);
            _tx = (w - w * _scale) / 2f;
            _ty = (h - h * _scale) / 2f;
        }

        public void ZoomBy(float factor, Point around)
        {
            float old = _scale;
            float next = old * factor;
            if (next < 0.15f) next = 0.15f;
            if (next > 40f) next = 40f;
            if (next == old) return;

            float wx = (around.X - _tx) / old;
            float wy = (around.Y - _ty) / old;
            _scale = next;
            _tx = around.X - wx * next;
            _ty = around.Y - wy * next;
            Invalidate();
        }

        public void SelectNode(Node node)
        {
            _selected = node;
            if (SelectionChanged != null) SelectionChanged(node);
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            _worldW = Math.Max(1, ClientSize.Width);
            _worldH = Math.Max(1, ClientSize.Height);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            // 标题栏高度随字体与 DPI 走，避免文字被矩形裁成半截
            _headerScreen = Math.Max(17f, _fontBold.GetHeight(g) + 7f);
            _placed.Clear();

            if (_focus == null)
            {
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(Loc.T("请选择要扫描的驱动器或文件夹"), _fontBold, _empty, ClientRectangle, sf);
                }
                return;
            }

            _fill.Color = Palette.CanvasBg;
            g.FillRectangle(_fill, new RectangleF(0, 0, _worldW, _worldH));

            float m = FrameInsetPx;
            RectangleF world = new RectangleF(m, m,
                Math.Max(1f, _worldW - 2f * m), Math.Max(1f, _worldH - 2f * m));
            float minWorld = 12f / _scale;

            GraphicsState st = g.Save();
            g.TranslateTransform(_tx, _ty);
            g.ScaleTransform(_scale, _scale);
            _border.Width = 1f / _scale;
            DrawSubtree(g, _focus, world, 0, minWorld);
            g.Restore(st);

            DrawOverlay(g);
        }

        private void DrawSubtree(Graphics g, Node node, RectangleF rect, int depth, float minWorld)
        {
            if (node == null) return;
            if (rect.Width <= 0.01f || rect.Height <= 0.01f) return;

            float sw = rect.Width * _scale;
            float sh = rect.Height * _scale;
            // SpaceSniffer 风格：格子用直角矩形，同一级子项才能紧密相邻、不留缝
            float radius = 0f;

            bool hasKids = node.Children != null && node.Children.Count > 0 && depth < MaxDepth;
            bool container = hasKids && sw >= 14f && sh >= 14f;

            if (!container)
            {
                Color fill = Palette.For(node, depth);
                _fill.Color = fill;
                FillCell(g, rect, radius);
                _border.Color = Palette.BorderOf(fill);
                _border.Width = 1.05f / _scale;
                StrokeCell(g, rect, radius);
                _placed.Add(new Placed { Node = node, Rect = rect, Depth = depth });
                return;
            }

            float headerH = (sw >= 52f && sh >= 34f)
                ? Math.Min(_headerScreen / _scale, rect.Height * 0.25f) : 0f;

            _fill.Color = Palette.Container(depth);
            FillCell(g, rect, radius);

            Placed p = new Placed { Node = node, Rect = rect, Depth = depth };
            if (headerH > 0.5f)
            {
                RectangleF hr = new RectangleF(rect.X, rect.Y, rect.Width, headerH);
                _fill.Color = Palette.Header(depth);
                g.FillRectangle(_fill, hr);
                p.Header = hr;
                p.HasHeader = true;
            }

            _border.Color = Palette.FrameLine;
            _border.Width = 1.1f / _scale;
            StrokeCell(g, rect, radius);
            _placed.Add(p);

            RectangleF content = rect;
            content.Y += headerH;
            content.Height -= headerH;
            float pad = CellPadPx / _scale;
            content = Deflate(content, pad);
            if (content.Width <= minWorld || content.Height <= minWorld) return;

            RectangleF[] rects = TreemapLayout.Squarify(node.Children, content);
            for (int i = 0; i < node.Children.Count; i++)
            {
                RectangleF cr = rects[i];
                if (cr.Width <= 0.75f || cr.Height <= 0.75f) continue;
                DrawSubtree(g, node.Children[i], cr, depth + 1, minWorld);
            }
        }

        private static RectangleF Deflate(RectangleF r, float d)
        {
            return new RectangleF(r.X + d, r.Y + d,
                Math.Max(0f, r.Width - 2f * d), Math.Max(0f, r.Height - 2f * d));
        }

        private void FillCell(Graphics g, RectangleF r, float radiusWorld)
        {
            if (radiusWorld * _scale >= 2f)
            {
                using (GraphicsPath gp = RoundedRect(r, radiusWorld))
                    g.FillPath(_fill, gp);
            }
            else g.FillRectangle(_fill, r);
        }

        private void StrokeCell(Graphics g, RectangleF r, float radiusWorld)
        {
            if (radiusWorld * _scale >= 2f)
            {
                using (GraphicsPath gp = RoundedRect(r, radiusWorld))
                    g.DrawPath(_border, gp);
            }
            else g.DrawRectangle(_border, r.X, r.Y, r.Width, r.Height);
        }

        private void DrawOverlay(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.None;
            RectangleF hoverRect = RectangleF.Empty;
            RectangleF selRect = RectangleF.Empty;

            for (int i = 0; i < _placed.Count; i++)
            {
                Placed p = _placed[i];
                if (p.Node == _hover) hoverRect = ScreenRect(p.Rect);
                if (p.Node == _selected) selRect = ScreenRect(p.Rect);
            }

            List<Placed> order = new List<Placed>(_placed);
            order.Sort(delegate(Placed a, Placed b)
            {
                float aa = a.Rect.Width * a.Rect.Height;
                float bb = b.Rect.Width * b.Rect.Height;
                if (aa != bb) return bb.CompareTo(aa);
                return a.Depth.CompareTo(b.Depth);
            });

            List<RectangleF> taken = new List<RectangleF>();

            // 1) 目录容器标题栏：“名称  大小”（外框最大者即盘符/根目录）
            for (int i = 0; i < order.Count; i++)
            {
                Placed p = order[i];
                if (p.Node == null || !p.HasHeader) continue;
                string name = p.Node.Name;
                if (string.IsNullOrEmpty(name)) continue;

                RectangleF sr = ScreenRect(p.Header);
                if (sr.Height < 9f) continue;

                RectangleF tr = new RectangleF(sr.X + 6f, sr.Y, sr.Width - 10f, sr.Height);
                // 放不下“名称 + 大小”时退化为只显示名称，尽量把文件夹名字露全
                string label = FitLabel(g, name, PathPickerForm.Fmt(p.Node.Size), _fontBold, tr.Width);
                _text.Color = Palette.HeaderText;
                g.DrawString(label, _fontBold, _text, tr, _fmtHead);
                taken.Add(new RectangleF(tr.X - 3f, tr.Y, tr.Width + 6f, sr.Height));
            }

            // 2) 文件 / 小格子标签：名称 + 大小
            for (int i = 0; i < order.Count; i++)
            {
                Placed p = order[i];
                if (p.Depth == 0 || p.Node == null || p.HasHeader) continue;
                string name = p.Node.Name;
                if (string.IsNullOrEmpty(name)) continue;

                RectangleF sr = ScreenRect(p.Rect);
                if (sr.Width < 30f || sr.Height < 15f) continue;

                Font f = (p.Node.IsDir && !p.Node.IsAgg) ? _fontBold : _font;
                string label = FitLabel(g, name,
                    p.Node.Size > 0 ? PathPickerForm.Fmt(p.Node.Size) : null, f, sr.Width - 8f);
                SizeF ts = g.MeasureString(label, f, new SizeF(10000f, 1000f), _measure);
                if (ts.Width > sr.Width - 8f || ts.Height > sr.Height - 4f) continue;

                RectangleF lr = new RectangleF(sr.X + 3f, sr.Y + 2f, ts.Width + 3f, ts.Height + 1f);
                RectangleF res = new RectangleF(lr.X - 1f, lr.Y - 1f, lr.Width + 2f, lr.Height + 2f);
                bool clash = false;
                for (int k = 0; k < taken.Count; k++)
                {
                    if (taken[k].IntersectsWith(res)) { clash = true; break; }
                }
                if (clash) continue;
                taken.Add(res);

                _chip.Color = Theme.IsDark
                    ? Color.FromArgb(p.Node.IsDir ? 200 : 175, 18, 20, 24)
                    : Color.FromArgb(p.Node.IsDir ? 220 : 195, 255, 255, 255);
                using (GraphicsPath gp = RoundedRect(lr, 3f))
                    g.FillPath(_chip, gp);
                _text.Color = Palette.CellText;
                g.DrawString(label, f, _text, lr, _fmt);
            }

            if (!selRect.IsEmpty)
                g.DrawRectangle(_selPen, selRect.X, selRect.Y, selRect.Width, selRect.Height);
            if (!hoverRect.IsEmpty)
            {
                _hoverPen.Width = 2f;
                g.DrawRectangle(_hoverPen, hoverRect.X + 1f, hoverRect.Y + 1f, hoverRect.Width - 2f, hoverRect.Height - 2f);
            }
        }

        // 优先“名称 + 大小”；宽度不够就退化成只显示名称，保证名字能尽量完整
        private string FitLabel(Graphics g, string name, string size, Font f, float maxWidth)
        {
            if (size != null)
            {
                string both = name + "  " + size;
                if (TextWidth(g, both, f) <= maxWidth) return both;
            }
            return name;
        }

        private float TextWidth(Graphics g, string s, Font f)
        {
            return g.MeasureString(s, f, new SizeF(10000f, 1000f), _measure).Width;
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            GraphicsPath gp = new GraphicsPath();
            float d = radius * 2f;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d < 1f) { gp.AddRectangle(r); gp.CloseFigure(); return gp; }
            gp.AddArc(r.X, r.Y, d, d, 180f, 90f);
            gp.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            gp.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            gp.CloseFigure();
            return gp;
        }

        private RectangleF ScreenRect(RectangleF r)
        {
            return new RectangleF(r.X * _scale + _tx, r.Y * _scale + _ty, r.Width * _scale, r.Height * _scale);
        }

        private Node HitTest(Point screen)
        {
            float wx = (screen.X - _tx) / _scale;
            float wy = (screen.Y - _ty) / _scale;
            Node best = null;
            int bestDepth = -1;
            for (int i = 0; i < _placed.Count; i++)
            {
                Placed p = _placed[i];
                if (p.Depth < bestDepth) continue;
                RectangleF r = p.Rect;
                if (wx >= r.X && wx < r.X + r.Width && wy >= r.Y && wy < r.Y + r.Height)
                {
                    best = p.Node;
                    bestDepth = p.Depth;
                }
            }
            return best;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Left)
            {
                _dragging = false;
                _leftDown = true;
                _downScreen = e.Location;
                _downTx = _tx;
                _downTy = _ty;
            }
            else if (e.Button == MouseButtons.Right)
            {
                Node n = HitTest(e.Location);
                if (n != null) SelectNode(n);
                if (MenuRequested != null) MenuRequested(n);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_leftDown && (e.Button & MouseButtons.Left) != 0)
            {
                int dx = e.X - _downScreen.X;
                int dy = e.Y - _downScreen.Y;
                if (!_dragging && (Math.Abs(dx) > 4 || Math.Abs(dy) > 4)) _dragging = true;
                if (_dragging)
                {
                    _tx = _downTx + dx;
                    _ty = _downTy + dy;
                    Invalidate();
                    return;
                }
            }

            Node n = HitTest(e.Location);
            if (!ReferenceEquals(n, _hover))
            {
                _hover = n;
                if (Hovered != null) Hovered(n);
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            bool wasDrag = _dragging;
            _dragging = false;
            _leftDown = false;
            _downScreen = Point.Empty;
            if (wasDrag) return;

            Node n = HitTest(e.Location);
            if (n != null) SelectNode(n);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left) return;

            Node n = HitTest(e.Location);
            if (n == null || n.IsAgg) return;      // 聚合分组不响应双击

            // 文件夹：直接进入
            if (n.IsDir)
            {
                if (Activated != null) Activated(n);
                return;
            }

            // 文件：已是当前焦点的直接子项才打开，否则先切到它所在的文件夹
            if (ReferenceEquals(n.Parent, _focus))
            {
                if (OpenRequested != null) OpenRequested(n);
                return;
            }
            if (n.Parent != null && Activated != null) Activated(n.Parent);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            float factor = e.Delta > 0 ? 1.2f : 1f / 1.2f;
            ZoomBy(factor, e.Location);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == Style.WM_NCHITTEST && (int)m.Result == Style.HTCLIENT)
                Style.DeferAtEdge(this, ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _font.Dispose();
                _fontBold.Dispose();
                _fill.Dispose();
                _border.Dispose();
                _text.Dispose();
                _chip.Dispose();
                _empty.Dispose();
                _hoverPen.Dispose();
                _selPen.Dispose();
                _fmt.Dispose();
                _fmtHead.Dispose();
                _measure.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /* =====================================================================
       TitleBar  --  custom-drawn macOS style bar (traffic lights + toolbar)
       ===================================================================== */

    internal sealed class TitleBar : Control
    {
        public const int BarHeight = 46;

        internal enum Cmd
        {
            None, Close, Min, Max, Pick, Back, Up, Rescan, ZoomIn, ZoomOut, Fit, Theme, Path, PathCopy
        }

        private sealed class Item
        {
            public Cmd Cmd;
            public Rectangle Rect;
            public string Glyph;
            public string Text;
            public string Tip;
            public bool Enabled = true;
            public bool Traffic;
            public Color TrafficColor;
        }

        private static readonly Font IconFont = new Font("Segoe MDL2 Assets", 10f);
        private static readonly Font IconFontSm = new Font("Segoe MDL2 Assets", 9f);
        private static readonly Font WordFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        private static readonly Font PillFont = new Font("Segoe UI", 8.25f);

        private readonly List<Item> _items = new List<Item>();
        private readonly SolidBrush _brush = new SolidBrush(Color.Black);
        private readonly Pen _pen = new Pen(Color.Black);
        private readonly StringFormat _sfEllipsis = new StringFormat();
        private readonly StringFormat _sfCenter = new StringFormat();
        private readonly ToolTip _tip = new ToolTip();

        private Cmd _hover = Cmd.None;
        private Cmd _pressed = Cmd.None;

        public bool CanBack = true;
        public bool CanUp = true;
        public bool Maximized;
        public string PathText = "";
        public string Wordmark = "DiskTreemap";

        public event Action<Cmd> Command;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTCAPTION = 2;

        public TitleBar()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            Height = BarHeight;
            Dock = DockStyle.Top;
            _sfEllipsis.Trimming = StringTrimming.EllipsisPath;
            _sfEllipsis.FormatFlags = StringFormatFlags.NoWrap;
            _sfEllipsis.LineAlignment = StringAlignment.Center;
            _sfCenter.Alignment = StringAlignment.Center;
            _sfCenter.LineAlignment = StringAlignment.Center;
            _sfCenter.FormatFlags = StringFormatFlags.NoWrap;
            _tip.InitialDelay = 420;
            _tip.ReshowDelay = 120;
            _tip.AutoPopDelay = 9000;
        }

        public void ApplyTheme()
        {
            BackColor = Theme.TitleBarBg;
            ForeColor = Theme.TitleBarText;
            Invalidate();
        }

        /* ----------------------------- layout ---------------------------- */

        private void Relayout()
        {
            _items.Clear();
            int cy = BarHeight / 2;

            // 交通灯（左）。直径取偶数，圆心落在整数像素上，
            // 1px 笔画才能对齐像素栅格、既清晰又精确居中。
            int d = 14;
            int x0 = 16;
            _items.Add(Traffic(Cmd.Close, x0, cy - d / 2, d, Theme.CloseColor, Loc.T("关闭")));
            _items.Add(Traffic(Cmd.Min, x0 + 21, cy - d / 2, d, Theme.MinColor, Loc.T("最小化")));
            _items.Add(Traffic(Cmd.Max, x0 + 42, cy - d / 2, d, Theme.MaxColor, Loc.T("最大化 / 还原")));

            int x = x0 + 42 + d + 16;

            // 词标宽度
            Size ws = TextRenderer.MeasureText(Wordmark, WordFont);
            x += ws.Width + 14;
            _wordRect = new Rectangle(x - ws.Width - 14, 0, ws.Width, BarHeight);
            _sep1 = new Rectangle(x - 7, cy - 9, 1, 18);
            x += 6;

            x = Btn(x, cy, Cmd.Pick, "\uED25", Loc.T("选择路径 / 重新选择扫描目标"), true);
            x = Btn(x, cy, Cmd.Back, "\uE72B", Loc.T("后退 (Alt+←)"), CanBack);
            x = Btn(x, cy, Cmd.Up, "\uE74A", Loc.T("上一级 (Backspace)"), CanUp);
            x = Btn(x, cy, Cmd.Rescan, "\uE72C", Loc.T("重新扫描 (F5)"), true);
            x += 6;
            _sep2 = new Rectangle(x - 3, cy - 9, 1, 18);
            x += 5;
            x = Btn(x, cy, Cmd.ZoomOut, "\uE71F", Loc.T("缩小"), true);
            x = Btn(x, cy, Cmd.ZoomIn, "\uE8A3", Loc.T("放大"), true);
            x = Btn(x, cy, Cmd.Fit, "\uE9A6", Loc.T("适应窗口"), true);

            // 主题切换（右）
            int rx = Math.Max(x + 10, Width - 44);
            _items.Add(new Item
            {
                Cmd = Cmd.Theme,
                Rect = new Rectangle(rx, cy - 14, 32, 28),
                Glyph = Theme.IsDark ? "\uE706" : "\uE708",
                Tip = Theme.IsDark ? Loc.T("切换到浅色主题") : Loc.T("切换到深色主题")
            });

            // 路径胶囊
            int pl = x + 12;
            int pr = rx - 12;
            if (pr - pl >= 60)
                _items.Add(new Item
                {
                    Cmd = Cmd.Path,
                    Rect = new Rectangle(pl, cy - 13, pr - pl, 26),
                    Text = PathText,
                    Tip = Loc.T("双击复制当前路径")
                });
        }

        private Rectangle _wordRect;
        private Rectangle _sep1;
        private Rectangle _sep2;

        private Item Traffic(Cmd c, int x, int y, int d, Color col, string tip)
        {
            return new Item
            {
                Cmd = c, Rect = new Rectangle(x, y, d, d),
                Traffic = true, TrafficColor = col, Tip = tip
            };
        }

        private int Btn(int x, int cy, Cmd c, string glyph, string tip, bool enabled)
        {
            _items.Add(new Item
            {
                Cmd = c, Rect = new Rectangle(x, cy - 14, 32, 28),
                Glyph = glyph, Tip = tip, Enabled = enabled
            });
            return x + 34;
        }

        private Item Find(Point p)
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (_items[i].Rect.Contains(p)) return _items[i];
            }
            return null;
        }

        /* ----------------------------- painting -------------------------- */

        protected override void OnPaint(PaintEventArgs e)
        {
            Relayout();
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            _brush.Color = Theme.TitleBarBg;
            g.FillRectangle(_brush, ClientRectangle);

            _pen.Color = Theme.Border;
            g.DrawLine(_pen, 0, Height - 1, Width, Height - 1);

            // 词标
            _brush.Color = Theme.AccentColor;
            g.DrawString(Wordmark, WordFont, _brush, _wordRect.X, (Height - WordFont.Height) / 2f - 1f);

            // 分组分隔线
            _brush.Color = Theme.Border;
            g.FillRectangle(_brush, _sep1);
            g.FillRectangle(_brush, _sep2);

            for (int i = 0; i < _items.Count; i++)
                DrawItem(g, _items[i]);
        }

        private void DrawItem(Graphics g, Item it)
        {
            bool hover = it.Cmd == _hover;
            bool press = it.Cmd == _pressed;

            if (it.Traffic)
            {
                DrawTraffic(g, it);
                return;
            }

            if (it.Cmd == Cmd.Path)
            {
                Rectangle r = it.Rect;
                using (GraphicsPath gp = Round(r, 13f))
                {
                    _brush.Color = Theme.PillBg;
                    g.FillPath(_brush, gp);
                    _pen.Color = Theme.Border;
                    g.DrawPath(_pen, gp);
                }
                Rectangle tr = new Rectangle(r.X + 12, r.Y, r.Width - 22, r.Height);
                _brush.Color = Theme.SubtleText;
                g.DrawString(it.Text ?? "", PillFont, _brush, tr, _sfEllipsis);
                return;
            }

            // 普通图标按钮
            if (hover || press)
            {
                using (GraphicsPath gp = Round(it.Rect, 7f))
                {
                    _brush.Color = press ? Theme.PressBg : Theme.HoverBg;
                    g.FillPath(_brush, gp);
                }
            }

            Color fg = it.Enabled ? (it.Cmd == Cmd.Theme ? Theme.AccentColor : Theme.ControlText) : Theme.SubtleText;
            _brush.Color = fg;
            Font gf = (it.Cmd == Cmd.Theme) ? IconFontSm : IconFont;
            g.DrawString(it.Glyph, gf, _brush, it.Rect, _sfCenter);
        }

        /* 交通灯：球面渐变 + 顶部镜面高光 + 底部内阴影，做出圆润的突起感；
           三个符号改为矢量绘制并以圆点几何中心为基准，做到亚像素级精准居中。 */
        private void DrawTraffic(Graphics g, Item it)
        {
            bool press = it.Cmd == _pressed;
            RectangleF body = it.Rect;
            float cx = body.X + body.Width / 2f;
            float cy = body.Y + body.Height / 2f;

            Color baseCol = it.Enabled ? it.TrafficColor : Blend(it.TrafficColor, Theme.TitleBarBg, 0.6f);
            Color light = Palette.Shift(baseCol, press ? 8 : 34);
            Color dark = Palette.Shift(baseCol, press ? -30 : -22);
            Color ink = Palette.Shift(baseCol, press ? -88 : -126);   // 同色深色调，保证图标清晰

            // 轻微外投影：按下时收起，制造“浮起 / 压下”的对比
            using (GraphicsPath sp = EllipsePath(new RectangleF(body.X, body.Y + 1f, body.Width, body.Height)))
            using (SolidBrush sb = new SolidBrush(Color.FromArgb(press ? 12 : 30, 0, 0, 0)))
                g.FillPath(sb, sp);

            using (GraphicsPath gp = EllipsePath(body))
            {
                // 上亮下暗的球面渐变
                using (LinearGradientBrush lg = new LinearGradientBrush(
                           new PointF(body.X, body.Y - 1f), new PointF(body.X, body.Bottom + 1f), light, dark))
                    g.FillPath(lg, gp);

                // 底部内阴影：内缩绘制，避免画到圆外
                using (Pen rim = new Pen(Color.FromArgb(press ? 18 : 38, 0, 0, 0), 1.4f))
                    g.DrawArc(rim, body.X + 1.3f, body.Y + 1.3f,
                        body.Width - 2.6f, body.Height - 2.6f, 24f, 132f);

                // 顶部镜面高光：小而柔，避免糊住图标
                if (!press && it.Enabled)
                {
                    RectangleF hl = new RectangleF(
                        body.X + body.Width * 0.31f, body.Y + body.Height * 0.11f,
                        body.Width * 0.38f, body.Height * 0.20f);
                    using (GraphicsPath hp = EllipsePath(hl))
                    using (SolidBrush hb = new SolidBrush(Color.FromArgb(95, 255, 255, 255)))
                        g.FillPath(hb, hp);
                }
            }

            // 符号：以 (cx, cy) 为中心对称绘制；圆点中心落在半像素上，
            // 故用 1px 画笔 + 半像素对齐坐标，画出的笔画既清晰又数学精确居中。
            using (Pen pen = new Pen(ink, 1f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                if (it.Cmd == Cmd.Close)
                {
                    float s = 3f;
                    g.DrawLine(pen, cx - s, cy - s, cx + s, cy + s);
                    g.DrawLine(pen, cx + s, cy - s, cx - s, cy + s);
                }
                else if (it.Cmd == Cmd.Min)
                {
                    g.DrawLine(pen, cx - 3.5f, cy, cx + 3.5f, cy);
                }
                else
                {
                    g.DrawRectangle(pen, cx - 3f, cy - 3f, 6f, 6f);
                }
            }
        }

        private static GraphicsPath EllipsePath(RectangleF r)
        {
            GraphicsPath gp = new GraphicsPath();
            gp.AddEllipse(r);
            return gp;
        }

        private static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        private static GraphicsPath Round(Rectangle r, float radius)
        {
            GraphicsPath gp = new GraphicsPath();
            float d = radius * 2f;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d < 1f) { gp.AddRectangle(r); gp.CloseFigure(); return gp; }
            gp.AddArc(r.X, r.Y, d, d, 180f, 90f);
            gp.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            gp.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            gp.CloseFigure();
            return gp;
        }

        /* ------------------------------ input ---------------------------- */

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Item it = Find(e.Location);
            Cmd c = it != null ? it.Cmd : Cmd.None;
            if (c != _hover)
            {
                _hover = c;
                Cursor = c == Cmd.None ? Cursors.Default : Cursors.Hand;
                if (it != null && !string.IsNullOrEmpty(it.Tip))
                    _tip.Show(it.Tip, this, it.Rect.Left, it.Rect.Bottom + 4, 5000);
                else
                    _tip.Hide(this);
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != Cmd.None || _pressed != Cmd.None)
            {
                _hover = Cmd.None;
                _pressed = Cmd.None;
                _tip.Hide(this);
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            Item it = Find(e.Location);
            if (it == null)
            {
                if (e.Clicks >= 2) { Raise(Cmd.Max); return; }
                if (e.Clicks == 1) DragWindow();
                return;
            }
            if (!it.Enabled) return;
            _pressed = it.Cmd;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            Cmd p = _pressed;
            _pressed = Cmd.None;
            Item it = Find(e.Location);
            if (it != null && it.Cmd == p && it.Enabled)
            {
                if (p == Cmd.Path && e.Clicks >= 2) { Raise(Cmd.PathCopy); return; }
                Raise(p);
            }
            Invalidate();
        }

        private void DragWindow()
        {
            try
            {
                ReleaseCapture();
                SendMessage(FindForm().Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            }
            catch { }
        }

        private void Raise(Cmd c)
        {
            _tip.Hide(this);
            if (Command != null) Command(c);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == Style.WM_NCHITTEST && (int)m.Result == Style.HTCLIENT)
                Style.DeferAtEdge(this, ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _brush.Dispose();
                _pen.Dispose();
                _sfEllipsis.Dispose();
                _sfCenter.Dispose();
                _tip.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /* 铺满客户区的面板（如扫描遮罩）：边框区命中测试同样透传给主窗体 */
    internal sealed class EdgePanel : Panel
    {
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == Style.WM_NCHITTEST && (int)m.Result == Style.HTCLIENT)
                Style.DeferAtEdge(this, ref m);
        }
    }

    /* 状态栏紧贴窗口底边，命中测试需透传给主窗体以支持边缘拉伸 */
    internal sealed class EdgeStatusStrip : StatusStrip
    {
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == Style.WM_NCHITTEST && (int)m.Result == Style.HTCLIENT)
                Style.DeferAtEdge(this, ref m);
        }
    }

    /* =====================================================================
       MainForm  --  toolbar + treemap canvas + status bar
       ===================================================================== */

    internal sealed class MainForm : Form
    {
        private string _scanPath;
        private long _minFile;

        private TreemapCanvas _canvas;
        private TitleBar _titleBar;
        private StatusStrip _status;
        private ToolStripStatusLabel _lblFocus;
        private ToolStripStatusLabel _lblHover;
        private Panel _overlay;
        private Label _ovLabel;
        private Button _ovCancel;
        private Panel _ovBar;
        private System.Windows.Forms.Timer _ovTimer;
        private int _ovPhase;

        private Node _root;
        private Node _focus;
        private readonly Stack<Node> _back = new Stack<Node>();
        private volatile bool _cancel;
        private volatile int _scanGen;   // 最新扫描代次；旧扫描的结果直接丢弃

        // 主题：_autoTheme 跟随系统，关闭后使用 _dark 手动值
        private bool _autoTheme = true;
        private bool _dark;
        private int _lastStateChange;

        private const int WM_SETTINGCHANGE = 0x001A;
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_MAXIMIZE = 0xF030;
        private const int SC_RESTORE = 0xF120;
        private const int HTCLIENT = 1;
        private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                          HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        private const int ResizeBorder = 6;

        public MainForm(string scanPath, long minFile)
        {
            _scanPath = scanPath;
            _minFile = minFile;

            Text = "DiskTreemap";
            Icon = Style.AppIcon();
            FormBorderStyle = FormBorderStyle.None;
            ClientSize = new Size(1100, 720);
            MinimumSize = new Size(720, 480);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            KeyPreview = true;
            DoubleBuffered = true;

            // 有手动记录就沿用记录（不再跟随系统），否则跟随系统
            _autoTheme = !Prefs.ThemeDark.HasValue;
            _dark = Prefs.StartupDark();
            Theme.Set(_dark);

            _canvas = new TreemapCanvas();
            _canvas.Dock = DockStyle.Fill;
            _canvas.Activated += delegate(Node n) { NavigateInto(n); };
            _canvas.OpenRequested += delegate(Node n) { OpenNode(n); };
            _canvas.MenuRequested += delegate(Node n) { ShowMenu(n); };
            _canvas.Hovered += delegate(Node n) { UpdateHover(n); };
            _canvas.SelectionChanged += delegate(Node n) { UpdateHover(n); };

            _overlay = BuildOverlay();
            _titleBar = BuildTitleBar();
            BuildStatus();

            BackColor = Theme.WindowBg;
            Padding = new Padding(0);

            Controls.Add(_canvas);
            Controls.Add(_overlay);
            Controls.Add(_status);
            Controls.Add(_titleBar);

            KeyDown += OnKeyDown;
        }

        /* ---------------------------- chrome ----------------------------- */

        private TitleBar BuildTitleBar()
        {
            TitleBar tb = new TitleBar();
            tb.Command += delegate(TitleBar.Cmd c) { OnChromeCommand(c); };
            tb.CanBack = false;
            tb.CanUp = false;
            return tb;
        }

        private void OnChromeCommand(TitleBar.Cmd c)
        {
            switch (c)
            {
                case TitleBar.Cmd.Close: Close(); break;
                case TitleBar.Cmd.Min: WindowState = FormWindowState.Minimized; break;
                case TitleBar.Cmd.Max: ToggleMaximize(); break;
                case TitleBar.Cmd.Pick: PickPath(); break;
                case TitleBar.Cmd.Back: GoBack(); break;
                case TitleBar.Cmd.Up: GoUp(); break;
                case TitleBar.Cmd.Rescan: StartScan(_scanPath, _minFile); break;
                case TitleBar.Cmd.ZoomIn: _canvas.ZoomBy(1.25f, CenterClient()); break;
                case TitleBar.Cmd.ZoomOut: _canvas.ZoomBy(0.8f, CenterClient()); break;
                case TitleBar.Cmd.Fit: _canvas.ResetView(); _canvas.Invalidate(); break;
                case TitleBar.Cmd.Theme: ToggleTheme(); break;
                case TitleBar.Cmd.PathCopy: CopyText(_titleBar.PathText); break;
            }
        }

        private void ToggleMaximize()
        {
            // 防抖：原生标题栏双击也会走 SC_MAXIMIZE，避免与之互相抵消
            if (unchecked(Environment.TickCount - _lastStateChange) < 300) return;
            WindowState = WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal : FormWindowState.Maximized;
            _lastStateChange = Environment.TickCount;
        }

        private void ApplyDark(bool dark)
        {
            _dark = dark;
            Theme.Set(dark);
            BackColor = Theme.WindowBg;
            _canvas.ApplyTheme();
            _titleBar.ApplyTheme();
            ApplyStatusTheme();
            ApplyOverlayTheme();
            _status.Renderer = Style.NewRenderer();
            if (_focus != null) _titleBar.PathText = _focus.FullPath ?? _scanPath;
            _titleBar.Invalidate();
            Invalidate(true);
        }

        private void ToggleTheme()
        {
            // 手动切换即退出“跟随系统”模式，并把选择记下来供下次启动沿用
            _autoTheme = false;
            ApplyDark(!_dark);
            Prefs.SaveThemeDark(_dark);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SETTINGCHANGE)
            {
                if (_autoTheme)
                {
                    bool want = Prefs.SystemPrefersDark();
                    if (want != _dark) BeginInvoke((MethodInvoker)delegate { ApplyDark(want); });
                }
            }
            else if (m.Msg == WM_SYSCOMMAND)
            {
                int cmd = (int)m.WParam & 0xFFF0;
                if (cmd == SC_MAXIMIZE || cmd == SC_RESTORE) _lastStateChange = Environment.TickCount;
            }
            else if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                if ((int)m.Result == HTCLIENT && WindowState == FormWindowState.Normal)
                {
                    int lp = unchecked((int)(long)m.LParam);
                    Point p = PointToClient(new Point(unchecked((short)lp), unchecked((short)(lp >> 16))));
                    bool l = p.X <= ResizeBorder, r = p.X >= ClientSize.Width - ResizeBorder;
                    bool t = p.Y <= ResizeBorder, b = p.Y >= ClientSize.Height - ResizeBorder;
                    int hit = 0;
                    if (l && t) hit = HTTOPLEFT;
                    else if (r && t) hit = HTTOPRIGHT;
                    else if (l && b) hit = HTBOTTOMLEFT;
                    else if (r && b) hit = HTBOTTOMRIGHT;
                    else if (l) hit = HTLEFT;
                    else if (r) hit = HTRIGHT;
                    else if (t) hit = HTTOP;
                    else if (b) hit = HTBOTTOM;
                    if (hit != 0) m.Result = (IntPtr)hit;
                }
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_titleBar != null) _titleBar.Maximized = WindowState == FormWindowState.Maximized;
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            UpdateMaximizedBounds();
        }

        private void UpdateMaximizedBounds()
        {
            // 无边框窗口最大化时会盖住任务栏，必须显式限制到工作区
            try { MaximizedBounds = Screen.FromControl(this).WorkingArea; }
            catch { }
        }

        private Panel BuildOverlay()
        {
            Panel ov = new EdgePanel();
            ov.Dock = DockStyle.Fill;
            ov.BackColor = Theme.WindowBg;
            ov.Visible = false;

            _ovLabel = new Label();
            _ovLabel.Text = Loc.T("正在扫描...");
            _ovLabel.AutoSize = false;
            _ovLabel.TextAlign = ContentAlignment.MiddleCenter;
            _ovLabel.ForeColor = Theme.ControlText;
            _ovLabel.Font = new Font("Segoe UI", 11f);
            _ovLabel.Size = new Size(320, 28);

            _ovBar = new Panel();
            _ovBar.Size = new Size(320, 4);
            _ovBar.Paint += delegate(object s, PaintEventArgs pe)
            {
                Rectangle r = _ovBar.ClientRectangle;
                if (r.Width <= 0 || r.Height <= 0) return;
                pe.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush track = new SolidBrush(Theme.IsDark ? Theme.PressBg : Color.FromArgb(226, 229, 235)))
                using (GraphicsPath tp = Rounded(r, r.Height / 2f))
                    pe.Graphics.FillPath(track, tp);

                int w = Math.Min(130, r.Width);
                int total = r.Width + w;
                int x = (_ovPhase % total) - w;
                int x1 = Math.Max(0, x), x2 = Math.Min(r.Width, x + w);
                if (x2 > x1)
                {
                    using (SolidBrush b = new SolidBrush(Theme.AccentColor))
                    using (GraphicsPath gp = Rounded(new Rectangle(x1, 0, x2 - x1, r.Height), r.Height / 2f))
                        pe.Graphics.FillPath(b, gp);
                }
            };
            _ovTimer = new System.Windows.Forms.Timer();
            _ovTimer.Interval = 16;
            _ovTimer.Tick += delegate { _ovPhase += 7; _ovBar.Invalidate(); };

            _ovCancel = new Button();
            _ovCancel.Text = Loc.T("取消");
            _ovCancel.Size = new Size(96, 30);
            _ovCancel.FlatStyle = FlatStyle.Flat;
            _ovCancel.FlatAppearance.BorderSize = 0;
            _ovCancel.BackColor = Theme.AccentColor;
            _ovCancel.ForeColor = Color.White;
            _ovCancel.Click += delegate { _cancel = true; };

            ov.Controls.Add(_ovLabel);
            ov.Controls.Add(_ovBar);
            ov.Controls.Add(_ovCancel);

            ov.Resize += delegate
            {
                int cx = ov.ClientSize.Width / 2;
                int cy = ov.ClientSize.Height / 2;
                _ovLabel.Location = new Point(cx - 160, cy - 50);
                _ovBar.Location = new Point(cx - 160, cy - 6);
                _ovCancel.Location = new Point(cx - 48, cy + 40);
            };
            return ov;
        }

        private void ApplyOverlayTheme()
        {
            _overlay.BackColor = Theme.WindowBg;
            _ovLabel.ForeColor = Theme.ControlText;
            _ovCancel.BackColor = Theme.AccentColor;
            _ovCancel.ForeColor = Color.White;
        }

        private void BuildStatus()
        {
            _status = new EdgeStatusStrip();
            _status.RenderMode = ToolStripRenderMode.Professional;
            _status.Renderer = Style.NewRenderer();
            _status.SizingGrip = false;

            _lblFocus = new ToolStripStatusLabel("");
            _lblFocus.Spring = true;
            _lblFocus.TextAlign = ContentAlignment.MiddleLeft;

            _lblHover = new ToolStripStatusLabel("");
            _lblHover.TextAlign = ContentAlignment.MiddleRight;

            _status.Items.Add(_lblFocus);
            _status.Items.Add(_lblHover);
            ApplyStatusTheme();
        }

        private void ApplyStatusTheme()
        {
            _status.BackColor = Theme.StatusBg;
            _status.ForeColor = Theme.ControlText;
            _lblFocus.ForeColor = Theme.ControlText;
            _lblHover.ForeColor = Theme.SubtleText;
        }

        private Point CenterClient()
        {
            return new Point(ClientSize.Width / 2, ClientSize.Height / 2);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;   // CS_DROPSHADOW：无边框窗口也有一层柔和投影
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Style.RoundCorners(this);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            UpdateMaximizedBounds();
            StartScan(_scanPath, _minFile);
        }

        /* --------------------------- scanning ---------------------------- */

        private void StartScan(string path, long minFile)
        {
            // 每次扫描领一个代次：旧扫描发现代次变了就自行退出，
            // 即便已经跑完也不会再回写界面，避免两次扫描互相覆盖
            int gen = ++_scanGen;
            _cancel = false;
            SetScanning(true);

            string scanPath = path;
            long min = minFile;

            Task.Factory.StartNew(delegate
            {
                try
                {
                    Scanner.Stats st;
                    Node root = Scanner.Scan(scanPath, min, out st,
                        delegate { return _cancel || gen != _scanGen; });
                    return new object[] { root, st };
                }
                catch (OperationCanceledException) { return null; }
            }).ContinueWith(delegate(Task<object[]> t)
            {
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (gen != _scanGen) return;   // 已被更新的扫描取代，由它负责收尾
                        if (t.IsFaulted)
                        {
                            SetScanning(false);
                            MessageBox.Show(this, Loc.T("扫描失败：") + Flatten(t.Exception), "DiskTreemap",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
                        if (t.Result == null)
                        {
                            SetScanning(false);
                            return;
                        }
                        Node root = (Node)t.Result[0];
                        Scanner.Stats st = (Scanner.Stats)t.Result[1];
                        _root = root;
                        _focus = root;
                        _back.Clear();
                        _canvas.SetFocus(root);
                        SetScanning(false);
                        UpdateChrome();
                        string title = string.Format(CultureInfo.InvariantCulture,
                            Loc.T("DiskTreemap - {0}   （{1}）"), _scanPath, PathPickerForm.Fmt(root.Size));
                        if (st.Skipped > 0)
                            title += string.Format(CultureInfo.InvariantCulture,
                                Loc.T("   （{0} 个目录无法读取）"), st.Skipped);
                        Text = title;
                    });
                }
                // 关窗竞态（ObjectDisposedException 派生自 InvalidOperationException）一并忽略
                catch (InvalidOperationException) { }
            });
        }

        private static string Flatten(Exception ex)
        {
            if (ex == null) return Loc.T("未知错误");
            AggregateException ag = ex as AggregateException;
            if (ag != null && ag.InnerExceptions.Count > 0) return Flatten(ag.InnerExceptions[0]);
            return ex.Message;
        }

        private void SetScanning(bool on)
        {
            _overlay.Visible = on;
            if (on) _overlay.BringToFront();
            _titleBar.Enabled = !on;
            if (on) { _ovPhase = 0; _ovTimer.Start(); } else _ovTimer.Stop();
            Cursor = on ? Cursors.WaitCursor : Cursors.Default;
        }

        /* --------------------------- navigation -------------------------- */

        private void NavigateInto(Node dir)
        {
            if (dir == null || !dir.IsDir || dir == _focus) return;
            _back.Push(_focus);
            _focus = dir;
            _canvas.SetFocus(dir);
            UpdateChrome();
        }

        private void GoUp()
        {
            if (_focus == null || _focus.Parent == null) return;
            _back.Push(_focus);
            _focus = _focus.Parent;
            _canvas.SetFocus(_focus);
            UpdateChrome();
        }

        private void GoBack()
        {
            if (_back.Count == 0) return;
            _focus = _back.Pop();
            _canvas.SetFocus(_focus);
            UpdateChrome();
        }

        private void PickPath()
        {
            using (PathPickerForm dlg = new PathPickerForm(_scanPath, _minFile))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string path = Canonical(dlg.SelectedPath);
                _scanPath = path;
                _minFile = dlg.MinFile;
                StartScan(path, dlg.MinFile);
            }
        }

        private static long DriveTotal(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return 0;
                string root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root)) return 0;
                DriveInfo di = new DriveInfo(root);
                return di.IsReady ? di.TotalSize : 0;
            }
            catch { return 0; }
        }

        private string Canonical(string p)
        {
            string full = Path.GetFullPath(p);
            char sep = Path.DirectorySeparatorChar;
            if (full.Length > 0 && full[full.Length - 1] != sep && full[full.Length - 1] != Path.AltDirectorySeparatorChar)
                full += sep;
            return full;
        }

        private void UpdateChrome()
        {
            _titleBar.PathText = _focus != null ? (_focus.FullPath ?? "") : _scanPath;
            _titleBar.CanBack = _back.Count > 0;
            _titleBar.CanUp = _focus != null && _focus.Parent != null;
            _titleBar.Invalidate();
            if (_focus != null)
            {
                // 占比以“所在盘符的总容量”为分母；取不到时退回扫描根总量
                long total = DriveTotal(_focus.FullPath);
                if (total <= 0) total = _root != null ? _root.Size : 0;
                double pct = total > 0 ? _focus.Size * 100.0 / total : 0;
                // 两位小数：整盘做分母时，几百 MB 的目录用一位小数会显示成 0.0%，看起来像零
                string pctText = pct.ToString("0.00", CultureInfo.InvariantCulture) + "%";
                _lblFocus.Text = _focus.Name + "   " + PathPickerForm.Fmt(_focus.Size)
                    + string.Format(CultureInfo.InvariantCulture, Loc.T("   （占总量 {0}）"), pctText);
            }
            UpdateHover(_canvas.SelectedNode);
        }

        private static GraphicsPath Rounded(Rectangle r, float radius)
        {
            GraphicsPath gp = new GraphicsPath();
            float d = radius * 2f;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d < 1f) { gp.AddRectangle(r); gp.CloseFigure(); return gp; }
            gp.AddArc(r.X, r.Y, d, d, 180f, 90f);
            gp.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            gp.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            gp.CloseFigure();
            return gp;
        }

        private void UpdateHover(Node n)
        {
            if (n == null) { _lblHover.Text = ""; return; }
            string kind = Loc.T(n.IsDir ? "文件夹" : (n.IsAgg ? "分组" : "文件"));
            string hint = "";
            if (!n.IsAgg)
            {
                Node dir = n;
                while (dir != null && !ReferenceEquals(dir.Parent, _focus)) dir = dir.Parent;
                if (dir != null && dir.IsDir && !dir.IsAgg)
                    hint = string.Format(CultureInfo.InvariantCulture, Loc.T("   （双击进入：{0}）"), dir.Name);
                else
                    hint = Loc.T("   （双击打开）");
            }
            _lblHover.Text = string.Format(CultureInfo.InvariantCulture, Loc.T("{0}：{1}   {2}{3}"),
                kind, n.FullPath ?? n.Name, PathPickerForm.Fmt(n.Size), hint);
        }

        /* --------------------------- keyboard ---------------------------- */

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Back) { GoUp(); e.Handled = true; }
            else if (e.KeyCode == Keys.F5) { StartScan(_scanPath, _minFile); e.Handled = true; }
            else if (e.KeyCode == Keys.Left && e.Alt) { GoBack(); e.Handled = true; }
            else if (e.KeyCode == Keys.C && e.Control && _canvas.SelectedNode != null)
            {
                CopyText(_canvas.SelectedNode.FullPath); e.Handled = true;
            }
        }

        /* --------------------------- context menu ------------------------ */

        // 不能在 Closed 里直接 Dispose：ToolStripManager 的模态菜单过滤器会继续处理
        // 本轮输入，之后再碰到这个已释放的下拉菜单就抛 ObjectDisposedException
        // （表现为“关掉菜单后点一下主窗口就报错”）。排到消息队列末尾再释放。
        private void DisposeMenuLater(ContextMenuStrip menu)
        {
            if (menu == null) return;
            try
            {
                if (IsDisposed || !IsHandleCreated) { menu.Dispose(); return; }
                BeginInvoke((MethodInvoker)delegate { try { menu.Dispose(); } catch { } });
            }
            catch { }
        }

        private void ShowMenu(Node n)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = Font;
            menu.Renderer = Style.NewRenderer();
            menu.BackColor = Theme.ControlBg;
            menu.ForeColor = Theme.ControlText;

            if (n == null)
            {
                // 画布空白处没有对象可操作，给一组视图级命令
                ToolStripMenuItem miFit = new ToolStripMenuItem(Loc.T("适应窗口"));
                miFit.Click += delegate { _canvas.ResetView(); _canvas.Invalidate(); };
                menu.Items.Add(miFit);

                ToolStripMenuItem miRescan = new ToolStripMenuItem(Loc.T("重新扫描 (F5)"));
                miRescan.Enabled = !string.IsNullOrEmpty(_scanPath);
                miRescan.Click += delegate { StartScan(_scanPath, _minFile); };
                menu.Items.Add(miRescan);

                menu.Items.Add(new ToolStripSeparator());

                ToolStripMenuItem miPick = new ToolStripMenuItem(Loc.T("选择路径..."));
                miPick.Click += delegate { PickPath(); };
                menu.Items.Add(miPick);

                menu.Closed += delegate { DisposeMenuLater(menu); };
                menu.Show(Cursor.Position);
                return;
            }

            bool actionable = !n.IsAgg && !string.IsNullOrEmpty(n.FullPath);

            Node enterTarget = n;
            while (enterTarget != null && !ReferenceEquals(enterTarget.Parent, _focus))
                enterTarget = enterTarget.Parent;

            ToolStripMenuItem miEnter = new ToolStripMenuItem(Loc.T("进入此目录"));
            miEnter.Enabled = enterTarget != null && enterTarget.IsDir && !enterTarget.IsAgg
                && enterTarget != _focus;
            miEnter.Click += delegate { NavigateInto(enterTarget); };
            menu.Items.Add(miEnter);

            ToolStripMenuItem miOpen = new ToolStripMenuItem(Loc.T("打开"));
            miOpen.Enabled = actionable;
            miOpen.Click += delegate { OpenNode(n); };
            menu.Items.Add(miOpen);

            ToolStripMenuItem miReveal = new ToolStripMenuItem(Loc.T("在资源管理器中显示"));
            miReveal.Enabled = actionable;
            miReveal.Click += delegate { RevealNode(n); };
            menu.Items.Add(miReveal);

            ToolStripMenuItem miProps = new ToolStripMenuItem(Loc.T("属性"));
            miProps.Enabled = actionable;
            miProps.Click += delegate { PropsNode(n); };
            menu.Items.Add(miProps);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miCopyPath = new ToolStripMenuItem(Loc.T("复制完整路径"));
            miCopyPath.Enabled = actionable;
            miCopyPath.Click += delegate { CopyText(n.FullPath); };
            menu.Items.Add(miCopyPath);

            ToolStripMenuItem miCopyName = new ToolStripMenuItem(Loc.T("复制名称"));
            miCopyName.Click += delegate { CopyText(n.Name); };
            menu.Items.Add(miCopyName);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miDelete = new ToolStripMenuItem(Loc.T("移到回收站"));
            miDelete.Enabled = actionable && !IsScanRoot(n);
            miDelete.Click += delegate { DeleteNode(n); };
            menu.Items.Add(miDelete);

            menu.Closed += delegate { DisposeMenuLater(menu); };
            menu.Show(Cursor.Position);
        }

        /* --------------------------- native actions ---------------------- */

        private bool IsScanRoot(Node n)
        {
            if (n == null || string.IsNullOrEmpty(n.FullPath)) return false;
            string rootTrim = _scanPath.TrimEnd(Path.DirectorySeparatorChar);
            return n.FullPath.Equals(rootTrim, StringComparison.OrdinalIgnoreCase)
                || n.FullPath.Equals(_scanPath, StringComparison.OrdinalIgnoreCase);
        }

        private bool Resolve(Node n, bool forDelete, out string full, out string err)
        {
            full = null; err = null;
            if (n == null) { err = Loc.T("未选择对象"); return false; }
            if (n.IsAgg) { err = Loc.T("聚合分组不可操作"); return false; }
            if (string.IsNullOrEmpty(n.FullPath)) { err = Loc.T("该节点没有有效路径"); return false; }

            string p = n.FullPath;
            string rootTrim = _scanPath.TrimEnd(Path.DirectorySeparatorChar);
            bool isRoot = p.Equals(rootTrim, StringComparison.OrdinalIgnoreCase)
                || p.Equals(_scanPath, StringComparison.OrdinalIgnoreCase);
            if (!isRoot && !p.StartsWith(_scanPath, StringComparison.OrdinalIgnoreCase))
            {
                err = Loc.T("超出扫描范围，已拒绝"); return false;
            }
            if (!File.Exists(p) && !Directory.Exists(p)) { err = Loc.T("路径不存在"); return false; }
            if (forDelete && isRoot) { err = Loc.T("不能删除扫描根目录"); return false; }

            try
            {
                FileAttributes at = File.GetAttributes(p);
                if ((at & FileAttributes.ReparsePoint) != 0) { err = Loc.T("不支持对链接/挂载点的操作"); return false; }
            }
            catch { err = Loc.T("无法读取该路径的属性"); return false; }

            full = p;
            return true;
        }

        private void OpenNode(Node n)
        {
            string full, err;
            if (!Resolve(n, false, out full, out err)) { Warn(err); return; }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(full);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex) { Warn(Loc.T("打开失败：") + ex.Message); }
        }

        private void RevealNode(Node n)
        {
            string full, err;
            if (!Resolve(n, false, out full, out err)) { Warn(err); return; }
            try
            {
                string sel = full.Length > 3
                    ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    : full;
                ProcessStartInfo psi = new ProcessStartInfo("explorer.exe", "/select,\"" + sel + "\"");
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex) { Warn(Loc.T("打开资源管理器失败：") + ex.Message); }
        }

        private void PropsNode(Node n)
        {
            string full, err;
            if (!Resolve(n, false, out full, out err)) { Warn(err); return; }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(full);
                psi.Verb = "properties";
                psi.UseShellExecute = true;
                psi.ErrorDialog = false;
                Process.Start(psi);
            }
            catch (Exception ex) { Warn(Loc.T("打开属性失败：") + ex.Message); }
        }

        private void DeleteNode(Node n)
        {
            string full, err;
            if (!Resolve(n, true, out full, out err)) { Warn(err); return; }

            DialogResult r = MessageBox.Show(this,
                string.Format(CultureInfo.InvariantCulture,
                    Loc.T("确定要把下面这项移到回收站吗？\r\n\r\n{0}\r\n\r\n（通常可从回收站还原；若超出回收站配额或该盘未启用回收站，则会被永久删除）"), full),
                Loc.T("移到回收站"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;

            int rc = Recycle(full);
            if (rc != 0)
            {
                Warn(string.Format(CultureInfo.InvariantCulture,
                    Loc.T("删除失败（代码 {0}）"), rc.ToString(CultureInfo.InvariantCulture)));
                return;
            }
            RemoveFromTree(n);
        }

        private void RemoveFromTree(Node n)
        {
            Node p = n.Parent;
            if (p == null || p.Children == null) return;
            if (!p.Children.Remove(n)) return;
            long sz = n.Size;
            Node cur = p;
            while (cur != null) { cur.Size -= sz; cur = cur.Parent; }

            if (_back.Count > 0)
            {
                Node[] hist = _back.ToArray();
                _back.Clear();
                for (int i = hist.Length - 1; i >= 0; i--)
                    if (!IsInside(hist[i], n)) _back.Push(hist[i]);
            }

            if (IsInside(_focus, n))
            {
                _focus = p;
                _canvas.SetFocus(p);
            }

            _canvas.Invalidate();
            UpdateChrome();
        }

        private static bool IsInside(Node node, Node ancestor)
        {
            Node c = node;
            while (c != null) { if (ReferenceEquals(c, ancestor)) return true; c = c.Parent; }
            return false;
        }

        private void CopyText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); }
            catch { }
        }

        private void Warn(string msg)
        {
            MessageBox.Show(this, msg, "DiskTreemap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        /* --------------------------- recycle bin ------------------------- */

        private const uint FO_DELETE = 0x0003;
        private const ushort FOF_SILENT = 0x0004;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_ALLOWUNDO = 0x0040;   // -> recycle bin
        private const ushort FOF_NOERRORUI = 0x0400;
        // 装不进回收站时（超过配额 / 该盘没有回收站）仍要弹系统警告：会被永久删除
        private const ushort FOF_WANTNUKEWARNING = 0x4000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        private int Recycle(string full)
        {
            SHFILEOPSTRUCT op = new SHFILEOPSTRUCT();
            op.wFunc = FO_DELETE;
            op.pFrom = full + "\0";
            op.pTo = null;
            op.fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING);
            return SHFileOperation(ref op);
        }
    }

    /* =====================================================================
       Program entry point
       ===================================================================== */

    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        private const int ATTACH_PARENT_PROCESS = -1;
        private const int STD_OUTPUT_HANDLE = -11;

        [STAThread]
        private static int Main(string[] args)
        {
            string outFile = null;
            long minFile = 1048576;
            string rootArg = null;
            bool wantHelp = false;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--help" || a == "-h" || a == "/?") { wantHelp = true; }
                else if (a == "--out" || a == "-o")
                {
                    if (i + 1 >= args.Length) return CliFail(Loc.T("--out 缺少参数"));
                    outFile = args[++i];
                }
                else if (a == "--min" || a == "--min-file")
                {
                    if (i + 1 >= args.Length) return CliFail(Loc.T("--min 缺少参数"));
                    if (!long.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out minFile) || minFile < 0)
                        return CliFail(Loc.T("--min 必须是 >= 0 的整数（字节）"));
                }
                else if (a.StartsWith("-"))
                {
                    return CliFail(Loc.T("未知参数: ") + a);
                }
                else
                {
                    if (rootArg != null) return CliFail(Loc.T("只能指定一个扫描根目录"));
                    rootArg = a;
                }
            }

            if (wantHelp || outFile != null)
                return RunCli(rootArg, outFile, minFile, wantHelp);

            return RunGui(rootArg, minFile);
        }

        private static int RunCli(string rootArg, string outFile, long minFile, bool wantHelp)
        {
            EnsureConsole();
            try { Console.OutputEncoding = Encoding.UTF8; }
            catch { }

            if (wantHelp)
            {
                PrintHelp();
                return 0;
            }

            // --out 必须显式给出扫描根目录：以前缺省会默默扫描当前工作目录
            if (string.IsNullOrEmpty(rootArg)) return CliFail(Loc.T("--out 需要指定扫描根目录"));

            string full;
            try { full = Path.GetFullPath(rootArg); }
            catch { return CliFail(Loc.T("无法解析根目录: ") + rootArg); }
            if (!Directory.Exists(full)) return CliFail(Loc.T("目录不存在: ") + full);

            Console.WriteLine("DiskTreemap");
            Console.WriteLine(Loc.T("扫描目录: ") + full);
            Stopwatch sw = Stopwatch.StartNew();
            Scanner.Stats st;
            Node tree;
            try { tree = Scanner.Scan(full, minFile, out st, null); }
            catch (OperationCanceledException) { return CliFail(Loc.T("扫描已取消")); }
            sw.Stop();
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                Loc.T("完成: {0:n1}s  文件 {1:n0}  目录 {2:n0}  聚合节点 {3:n0}  最大深度 {4}"),
                sw.Elapsed.TotalSeconds, st.Files, st.Dirs, st.AggNodes, st.MaxDepth));
            if (st.Skipped > 0)
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    Loc.T("跳过 {0} 个无法读取的目录或文件（大小被低估）"), st.Skipped));

            try
            {
                string outp = Path.GetFullPath(outFile);
                using (StreamWriter w = new StreamWriter(outp, false, new UTF8Encoding(false)))
                    Scanner.WriteJson(w, full, tree);
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    Loc.T("已写出 {0} ({1:n1} KB)"), outp, new FileInfo(outp).Length / 1024.0));
            }
            catch (Exception ex) { return CliFail(Loc.T("写出 JSON 失败: ") + ex.Message); }
            return 0;
        }

        private static int RunGui(string rootArg, long minFile)
        {
            // 声明 DPI 感知：高 DPI 下由系统位图拉伸整窗会发虚，交给 GDI 按真实分辨率绘制
            try { SetProcessDPIAware(); } catch { }
            // 选择框在主窗口之前创建，这里就要定好主题，否则它总以浅色出现
            Theme.Set(Prefs.StartupDark());
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 未处理异常兜底：给一句可读的提示，而不是 .NET 的崩溃对话框
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                WarnUnhandled(e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                WarnUnhandled(e.ExceptionObject as Exception);
            };

            string path;
            long min = minFile;

            if (!string.IsNullOrEmpty(rootArg))
            {
                try { path = Path.GetFullPath(rootArg); }
                catch { path = rootArg; }
                if (!Directory.Exists(path))
                {
                    MessageBox.Show(Loc.T("目录不存在：") + path, "DiskTreemap",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 2;
                }
            }
            else
            {
                using (PathPickerForm dlg = new PathPickerForm(null, minFile))
                {
                    if (dlg.ShowDialog() != DialogResult.OK) return 0;
                    path = dlg.SelectedPath;
                    min = dlg.MinFile;
                }
            }

            char sep = Path.DirectorySeparatorChar;
            if (path.Length > 0 && path[path.Length - 1] != sep && path[path.Length - 1] != Path.AltDirectorySeparatorChar)
                path += sep;

            Application.Run(new MainForm(path, min));
            return 0;
        }

        private static void WarnUnhandled(Exception ex)
        {
            string msg = ex != null ? ex.Message : Loc.T("未知错误");
            try
            {
                MessageBox.Show(Loc.T("发生未处理的错误：") + msg, "DiskTreemap",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        private static void EnsureConsole()
        {
            IntPtr h = GetStdHandle(STD_OUTPUT_HANDLE);
            if (h != IntPtr.Zero && h != (IntPtr)(-1)) return;   // already redirected / attached
            if (!AttachConsole(ATTACH_PARENT_PROCESS)) return;
            try
            {
                StreamWriter o = new StreamWriter(Console.OpenStandardOutput());
                o.AutoFlush = true;
                Console.SetOut(o);
                StreamWriter er = new StreamWriter(Console.OpenStandardError());
                er.AutoFlush = true;
                Console.SetError(er);
            }
            catch { }
        }

        private static int CliFail(string msg)
        {
            try { Console.Error.WriteLine(Loc.T("错误: ") + msg); } catch { }
            return 2;
        }

        private static void PrintHelp()
        {
            Console.WriteLine(Loc.T("DiskTreemap - 磁盘占用树状图（SpaceSniffer 的原生替代品）"));
            Console.WriteLine();
            Console.WriteLine(Loc.T("用法:"));
            Console.WriteLine(Loc.T("  DiskTreemap.exe                 打开驱动器/文件夹选择器"));
            Console.WriteLine(Loc.T("  DiskTreemap.exe <根目录>        直接打开查看器"));
            Console.WriteLine(Loc.T("  DiskTreemap.exe <根目录> --out <文件> [--min <字节>]"));
            Console.WriteLine();
            Console.WriteLine(Loc.T("选项:"));
            Console.WriteLine(Loc.T("  --out <文件>    只扫描并写出 JSON，不打开界面"));
            Console.WriteLine(Loc.T("  --min <字节>    小于该值的文件被聚合（默认 1048576）"));
            Console.WriteLine(Loc.T("  --help          显示本帮助"));
        }
    }
}
