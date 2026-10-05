// DiskTreemap.exe  --  fast disk-usage treemap viewer for Windows
//
// Single self-contained executable: scans a folder, then serves the interactive
// viewer on 127.0.0.1 and exposes a small token-guarded API so the page can call
// native file operations (open / reveal / properties / recycle-bin delete).
//
// Build (C# 5 compiler, .NET Framework 4.x):
//   csc.exe /target:exe /optimize+ /out:DiskTreemap.exe DiskTreemap.cs ^
//           /resource:index.template.html,viewer.html
//
// Usage:
//   DiskTreemap.exe [root] [--out file.json] [--min bytes] [--port n] [--no-open]
//   DiskTreemap.exe --help

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DiskTreemap
{
    /* =====================================================================
       Scanner  --  parallel directory walker producing the viewer's JSON tree
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

        private class Entry
        {
            public Dir Dir;
            public string FileName;
            public long Size;
        }

        public class Stats
        {
            public long Dirs;
            public long Files;
            public long AggNodes;
            public long AggFiles;
            public long MaxDepth;
        }

        public static string Run(string root, long minFile, out Stats stats)
        {
            DirectoryInfo rootDi = new DirectoryInfo(root);
            string rootName = rootDi.Name;
            if (string.IsNullOrEmpty(rootName)) rootName = rootDi.FullName;

            ConcurrentDictionary<string, Dir> map =
                new ConcurrentDictionary<string, Dir>(StringComparer.OrdinalIgnoreCase);
            Dir rootNode = new Dir();
            rootNode.Name = rootName;
            map[root] = rootNode;

            List<string> current = new List<string>();
            current.Add(root);

            while (current.Count > 0)
            {
                ConcurrentBag<string> next = new ConcurrentBag<string>();
                Parallel.ForEach(current,
                    new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2 },
                    delegate(string d)
                    {
                        Dir node;
                        if (!map.TryGetValue(d, out node)) return;
                        DirectoryInfo di;
                        try { di = new DirectoryInfo(d); }
                        catch { return; }
                        try
                        {
                            foreach (FileSystemInfo fsi in di.EnumerateFileSystemInfos())
                            {
                                try
                                {
                                    FileAttributes at = fsi.Attributes;
                                    bool isDir = (at & FileAttributes.Directory) != 0;
                                    bool isLink = (at & FileAttributes.ReparsePoint) != 0;
                                    if (isDir)
                                    {
                                        if (isLink) continue;   // skip junctions / symlinks (loop safety)
                                        Dir child = new Dir();
                                        child.Name = fsi.Name;
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
                                        FileEnt fe = new FileEnt();
                                        fe.Name = fsi.Name;
                                        fe.Size = len;
                                        lock (node) { node.Files.Add(fe); }
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

            stats = new Stats();
            Count(rootNode, 0, stats);
            stats.AggNodes = 0;
            stats.AggFiles = 0;

            StringBuilder sb = new StringBuilder(1 << 20);
            sb.Append("{\"root\":\"").Append(Esc(rootDi.FullName)).Append("\",\"tree\":");
            WriteNode(sb, rootNode, minFile, stats, 0);
            sb.Append("}");
            return sb.ToString();
        }

        private static long ComputeSize(Dir d)
        {
            long s = 0;
            foreach (FileEnt f in d.Files) s += f.Size;
            foreach (Dir c in d.Children) s += ComputeSize(c);
            d.Size = s;
            return s;
        }

        private static void Count(Dir d, long depth, Stats st)
        {
            st.Dirs++;
            if (depth > st.MaxDepth) st.MaxDepth = depth;
            st.Files += d.Files.Count;
            foreach (Dir c in d.Children) Count(c, depth + 1, st);
        }

        private static void WriteNode(StringBuilder sb, Dir d, long minFile, Stats st, long depth)
        {
            sb.Append("{\"n\":\"").Append(Esc(d.Name)).Append("\",\"s\":").Append(d.Size).Append(",\"c\":[");
            List<Entry> list = new List<Entry>();
            foreach (Dir c in d.Children)
            {
                Entry e = new Entry();
                e.Dir = c;
                e.Size = c.Size;
                list.Add(e);
            }
            long smallSum = 0; int smallCnt = 0;
            foreach (FileEnt f in d.Files)
            {
                if (f.Size >= minFile)
                {
                    Entry e = new Entry();
                    e.FileName = f.Name;
                    e.Size = f.Size;
                    list.Add(e);
                }
                else { smallSum += f.Size; smallCnt++; }
            }
            list.Sort(delegate(Entry a, Entry b) { return b.Size.CompareTo(a.Size); });

            bool first = true;
            foreach (Entry e in list)
            {
                if (!first) sb.Append(',');
                first = false;
                if (e.Dir != null) WriteNode(sb, e.Dir, minFile, st, depth + 1);
                else sb.Append("{\"n\":\"").Append(Esc(e.FileName)).Append("\",\"s\":").Append(e.Size).Append("}");
            }
            if (smallCnt > 0)
            {
                if (!first) sb.Append(',');
                sb.Append("{\"n\":\"(").Append(smallCnt).Append(" small files)\",\"s\":").Append(smallSum)
                  .Append(",\"agg\":1,\"k\":").Append(smallCnt).Append("}");
                st.AggNodes++;
                st.AggFiles += smallCnt;
            }
            sb.Append("]}");
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
       Minimal loopback HTTP server (TcpListener based -- no URL-ACL / admin)
       ===================================================================== */
    internal sealed class Req
    {
        public string Method;
        public string Path;      // no query string
        public string RawQuery;
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = new byte[0];

        public string QueryGet(string key)
        {
            if (string.IsNullOrEmpty(RawQuery)) return null;
            string[] parts = RawQuery.Split('&');
            foreach (string p in parts)
            {
                int eq = p.IndexOf('=');
                string k = eq >= 0 ? p.Substring(0, eq) : p;
                if (string.Equals(k, key, StringComparison.Ordinal))
                {
                    string v = eq >= 0 ? p.Substring(eq + 1) : "";
                    try { return Uri.UnescapeDataString(v.Replace('+', ' ')); }
                    catch { return v; }   // malformed %-escape: fall back to the raw value
                }
            }
            return null;
        }

        public string Header(string key)
        {
            string v;
            return Headers.TryGetValue(key, out v) ? v : null;
        }

        public string BodyText()
        {
            return Encoding.UTF8.GetString(Body);
        }
    }

    internal static class Program
    {
        private const int MaxHeaderBytes = 16 * 1024;
        private const int MaxBodyBytes = 256 * 1024;

        private static string _rootArg;
        private static string _rootFull;      // canonical, always ends with a separator (except drive root already does)
        private static string _html;
        private static string _token;
        private static int _port;
        private static volatile bool _running = true;
        private static Scanner.Stats _stats;

        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; }
            catch { }

            string outFile = null;
            long minFile = 1048576;
            bool noOpen = false;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--help" || a == "-h" || a == "/?")
                {
                    PrintHelp();
                    return 0;
                }
                else if (a == "--out" || a == "-o")
                {
                    if (i + 1 >= args.Length) { Fail("--out 缺少参数"); return 2; }
                    outFile = args[++i];
                }
                else if (a == "--min" || a == "--min-file")
                {
                    if (i + 1 >= args.Length) { Fail("--min 缺少参数"); return 2; }
                    if (!long.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out minFile) || minFile < 0)
                    { Fail("--min 必须是 >= 0 的整数（字节）"); return 2; }
                }
                else if (a == "--port")
                {
                    int p;
                    if (i + 1 >= args.Length) { Fail("--port 缺少参数"); return 2; }
                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out p) || p < 0 || p > 65535)
                    { Fail("--port 必须是 0-65535 的整数"); return 2; }
                    _port = p;
                }
                else if (a == "--no-open")
                {
                    noOpen = true;
                }
                else if (a.StartsWith("-"))
                {
                    Fail("未知参数: " + a);
                    return 2;
                }
                else
                {
                    if (_rootArg != null) { Fail("只能指定一个扫描根目录"); return 2; }
                    _rootArg = a;
                }
            }

            if (string.IsNullOrEmpty(_rootArg)) _rootArg = Directory.GetCurrentDirectory();

            string full;
            try { full = Path.GetFullPath(_rootArg); }
            catch { Fail("无法解析根目录: " + _rootArg); return 2; }
            if (!Directory.Exists(full)) { Fail("目录不存在: " + full); return 2; }

            char sep = Path.DirectorySeparatorChar;
            _rootFull = full;
            if (_rootFull[_rootFull.Length - 1] != sep && _rootFull[_rootFull.Length - 1] != Path.AltDirectorySeparatorChar)
                _rootFull += sep;

            Console.WriteLine("DiskTreemap");
            Console.WriteLine("扫描目录: " + full);
            Stopwatch sw = Stopwatch.StartNew();
            string json = Scanner.Run(full, minFile, out _stats);
            sw.Stop();
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "完成: {0:n1}s  文件 {1:n0}  目录 {2:n0}  聚合节点 {3:n0}  最大深度 {4}",
                sw.Elapsed.TotalSeconds, _stats.Files, _stats.Dirs, _stats.AggNodes, _stats.MaxDepth));

            if (outFile != null)
            {
                try
                {
                    string outp = Path.GetFullPath(outFile);
                    File.WriteAllText(outp, json, new UTF8Encoding(false));
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "已写出 {0} ({1:n1} KB)", outp, new FileInfo(outp).Length / 1024.0));
                }
                catch (Exception ex)
                {
                    Fail("写出 JSON 失败: " + ex.Message);
                    return 4;
                }
                return 0;
            }

            _token = RandomHex(16);          // 32 hex chars
            _html = BuildHtml(json, _token);

            TcpListener listener;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, _port);
                listener.Start();
            }
            catch (Exception ex)
            {
                Fail("无法监听本地端口: " + ex.Message);
                return 3;
            }
            _port = ((IPEndPoint)listener.LocalEndpoint).Port;

            string url = "http://127.0.0.1:" + _port.ToString(CultureInfo.InvariantCulture) + "/?t=" + _token;
            Console.WriteLine("服务已启动: " + url);
            Console.WriteLine("按 Ctrl+C 退出。");

            Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                _running = false;
                try { listener.Stop(); } catch { }
            };

            if (!noOpen) OpenBrowser(url);

            Accept(listener);

            try { listener.Stop(); } catch { }
            Console.WriteLine("已退出。");
            return 0;
        }

        private static void PrintHelp()
        {
            Console.WriteLine("DiskTreemap - 磁盘占用树状图（SpaceSniffer 的快速替代）");
            Console.WriteLine();
            Console.WriteLine("用法:");
            Console.WriteLine("  DiskTreemap.exe [根目录] [选项]");
            Console.WriteLine();
            Console.WriteLine("选项:");
            Console.WriteLine("  --out <文件>    只扫描并写出 JSON，不启动服务");
            Console.WriteLine("  --min <字节>    小于该值的文件被聚合（默认 1048576）");
            Console.WriteLine("  --port <端口>   本地服务端口（默认自动分配）");
            Console.WriteLine("  --no-open       启动后不自动打开浏览器");
            Console.WriteLine("  --help          显示本帮助");
        }

        private static void Fail(string msg)
        {
            Console.Error.WriteLine("错误: " + msg);
        }

        /* ------------------------- HTML assembly ------------------------- */

        private static string BuildHtml(string json, string token)
        {
            string tpl = ReadResource("viewer.html");
            if (tpl == null) throw new InvalidOperationException("内嵌查看器资源缺失");
            if (tpl.IndexOf("__DEMO_DATA__", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("内嵌查看器缺少数据占位符");

            string safeJson = json.Replace("</", "<\\/");   // keep the JSON inside its <script> block
            string html = tpl.Replace("__DEMO_DATA__", safeJson);

            string inject = "<script>window.__DT_TOKEN__=" + JsString(token) + ";</script>";
            int head = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            if (head >= 0) html = html.Substring(0, head) + inject + html.Substring(head);
            else html = inject + html;
            return html;
        }

        private static string JsString(string s)
        {
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '\\' || c == '"') sb.Append('\\').Append(c);
                else if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        private static string ReadResource(string endsWith)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            string[] names = asm.GetManifestResourceNames();
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i].EndsWith(endsWith, StringComparison.OrdinalIgnoreCase))
                {
                    using (Stream s = asm.GetManifestResourceStream(names[i]))
                    {
                        if (s == null) continue;
                        using (StreamReader sr = new StreamReader(s, Encoding.UTF8, true))
                            return sr.ReadToEnd();
                    }
                }
            }
            return null;
        }

        private static string RandomHex(int bytes)
        {
            byte[] b = new byte[bytes];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
                rng.GetBytes(b);
            StringBuilder sb = new StringBuilder(bytes * 2);
            for (int i = 0; i < b.Length; i++) sb.Append(b[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /* --------------------------- server loop -------------------------- */

        private static void Accept(TcpListener listener)
        {
            while (_running)
            {
                TcpClient client = null;
                try { client = listener.AcceptTcpClient(); }
                catch { break; }               // listener stopped
                if (client == null) continue;
                TcpClient captured = client;
                ThreadPool.QueueUserWorkItem(delegate(object st) { Handle(captured); });
            }
        }

        private static void Handle(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = 10000;
                client.SendTimeout = 10000;
                using (client)
                using (NetworkStream ns = client.GetStream())
                {
                    Req req = ReadRequest(ns);
                    if (req == null) return;
                    Route(ns, req);
                }
            }
            catch { }
        }

        private static Req ReadRequest(NetworkStream ns)
        {
            MemoryStream head = new MemoryStream();
            byte[] one = new byte[1];
            int total = 0;
            int match = 0;
            // read until \r\n\r\n
            while (match < 4)
            {
                int n;
                try { n = ns.Read(one, 0, 1); }
                catch { return null; }
                if (n <= 0) return null;
                byte c = one[0];
                head.WriteByte(c);
                total++;
                if (total > MaxHeaderBytes) return null;
                if ((match == 0 || match == 2) && c == '\r') match++;
                else if ((match == 1 || match == 3) && c == '\n') match++;
                else match = (c == '\r') ? 1 : 0;
            }

            string text = Encoding.ASCII.GetString(head.ToArray());
            string[] lines = text.Split(new string[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length < 1) return null;

            string[] rl = lines[0].Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (rl.Length < 3) return null;

            Req req = new Req();
            req.Method = rl[0].ToUpperInvariant();
            string target = rl[1];
            int q = target.IndexOf('?');
            req.Path = q >= 0 ? target.Substring(0, q) : target;
            req.RawQuery = q >= 0 ? target.Substring(q + 1) : null;

            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                int c = lines[i].IndexOf(':');
                if (c <= 0) continue;
                string k = lines[i].Substring(0, c).Trim();
                string v = lines[i].Substring(c + 1).Trim();
                req.Headers[k] = v;
            }

            if (req.Method == "POST")
            {
                int len = 0;
                string cl = req.Header("content-length");
                if (cl != null) int.TryParse(cl, NumberStyles.Integer, CultureInfo.InvariantCulture, out len);
                if (len < 0 || len > MaxBodyBytes) return null;
                byte[] body = new byte[len];
                int got = 0;
                while (got < len)
                {
                    int n;
                    try { n = ns.Read(body, got, len - got); }
                    catch { return null; }
                    if (n <= 0) return null;
                    got += n;
                }
                req.Body = body;
            }
            return req;
        }

        private static void Route(NetworkStream ns, Req req)
        {
            // DNS-rebinding guard: only our own loopback host is accepted
            string host = req.Header("host");
            if (host == null || !IsLoopbackHost(host))
            {
                ServeText(ns, 403, "text/plain; charset=utf-8", "forbidden host");
                return;
            }

            if (req.Method == "GET" && req.Path == "/favicon.ico")
            {
                WriteHead(ns, 204, "image/x-icon", 0);
                return;
            }

            if (!TokenOk(req))
            {
                WriteJson(ns, 401, "{\"ok\":false,\"msg\":\"未授权：缺少或错误的令牌\"}");
                return;
            }

            if (req.Method == "GET" && (req.Path == "/" || req.Path == "/index.html"))
            {
                byte[] b = Encoding.UTF8.GetBytes(_html);
                WriteHead(ns, 200, "text/html; charset=utf-8", b.Length);
                ns.Write(b, 0, b.Length);
                return;
            }
            if (req.Method == "GET" && req.Path == "/api/ping")
            {
                WriteJson(ns, 200, "{\"ok\":true,\"root\":\"" + JsonEsc(_rootFull.TrimEnd(Path.DirectorySeparatorChar)) + "\"}");
                return;
            }
            if (req.Method == "POST")
            {
                Api(ns, req);
                return;
            }
            if (req.Method == "GET")
            {
                WriteJson(ns, 404, "{\"ok\":false,\"msg\":\"未找到\"}");
                return;
            }
            ServeText(ns, 405, "text/plain; charset=utf-8", "method not allowed");
        }

        private static bool IsLoopbackHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            string h = host.Trim();
            string portPart = null;

            if (h[0] == '[')
            {
                // bracketed IPv6 literal, e.g. [::1] or [::1]:8080
                int close = h.IndexOf(']');
                if (close < 0) return false;
                string rest = h.Substring(close + 1);
                if (rest.Length > 0)
                {
                    if (rest[0] != ':') return false;
                    portPart = rest.Substring(1);
                }
                h = h.Substring(1, close - 1);
            }
            else
            {
                int c = h.IndexOf(':');
                if (c >= 0) { portPart = h.Substring(c + 1); h = h.Substring(0, c); }
            }

            if (!(h == "127.0.0.1" || h == "localhost" || h == "::1")) return false;

            if (portPart != null)
            {
                int p;
                if (!int.TryParse(portPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out p)) return false;   // fail closed
                if (p != _port) return false;
            }
            return true;
        }

        private static bool TokenOk(Req req)
        {
            if (string.IsNullOrEmpty(_token)) return false;
            string t = req.QueryGet("t");
            if (string.IsNullOrEmpty(t)) t = req.Header("x-dt-token");
            if (string.IsNullOrEmpty(t)) return false;
            return FixedTimeEquals(t, _token);
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= (a[i] ^ b[i]);
            return diff == 0;
        }

        private static void Api(NetworkStream ns, Req req)
        {
            string body = req.BodyText();
            string path = JsonField(body, "path");
            string err;
            string full = SafePath(path, out err);
            if (full == null)
            {
                WriteJson(ns, 200, "{\"ok\":false,\"msg\":\"" + JsonEsc(err) + "\"}");
                return;
            }

            try
            {
                switch (req.Path)
                {
                    case "/api/open":
                        {
                            ProcessStartInfo psi = new ProcessStartInfo(full);
                            psi.UseShellExecute = true;
                            Process.Start(psi);
                            WriteJson(ns, 200, "{\"ok\":true,\"msg\":\"已打开\"}");
                            return;
                        }
                    case "/api/reveal":
                        {
                            // a trailing separator would escape the closing quote in the explorer command line
                            string sel = full.Length > 3
                                ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                : full;
                            ProcessStartInfo psi = new ProcessStartInfo("explorer.exe", "/select,\"" + sel + "\"");
                            psi.UseShellExecute = true;
                            Process.Start(psi);
                            WriteJson(ns, 200, "{\"ok\":true,\"msg\":\"已在资源管理器中显示\"}");
                            return;
                        }
                    case "/api/props":
                        {
                            ProcessStartInfo psi = new ProcessStartInfo(full);
                            psi.Verb = "properties";
                            psi.UseShellExecute = true;
                            psi.ErrorDialog = false;
                            Process.Start(psi);
                            WriteJson(ns, 200, "{\"ok\":true,\"msg\":\"已打开属性\"}");
                            return;
                        }
                    case "/api/delete":
                        {
                            // never recycle the scan root itself -- that would nuke the whole tree
                            string rootTrim = _rootFull.TrimEnd(Path.DirectorySeparatorChar);
                            if (full.Equals(rootTrim, StringComparison.OrdinalIgnoreCase)
                                || full.Equals(_rootFull, StringComparison.OrdinalIgnoreCase))
                            {
                                WriteJson(ns, 200, "{\"ok\":false,\"msg\":\"不能删除扫描根目录\"}");
                                return;
                            }
                            int rc = Recycle(full);
                            if (rc == 0) WriteJson(ns, 200, "{\"ok\":true,\"msg\":\"已移到回收站\"}");
                            else WriteJson(ns, 200, "{\"ok\":false,\"msg\":\"删除失败（代码 " + rc.ToString(CultureInfo.InvariantCulture) + "）\"}");
                            return;
                        }
                    default:
                        WriteJson(ns, 404, "{\"ok\":false,\"msg\":\"未知接口\"}");
                        return;
                }
            }
            catch (Exception ex)
            {
                WriteJson(ns, 200, "{\"ok\":false,\"msg\":\"" + JsonEsc("操作失败: " + ex.Message) + "\"}");
            }
        }

        /* --------------------------- path safety -------------------------- */

        private static string SafePath(string p, out string err)
        {
            err = null;
            if (string.IsNullOrEmpty(p)) { err = "路径为空"; return null; }
            if (p.IndexOf('\0') >= 0) { err = "非法路径"; return null; }

            string full;
            try { full = Path.GetFullPath(p); }
            catch { err = "非法路径"; return null; }

            // must stay inside the scanned root
            if (!full.StartsWith(_rootFull, StringComparison.OrdinalIgnoreCase)
                && !full.Equals(_rootFull.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                err = "超出扫描范围，已拒绝";
                return null;
            }

            bool isRoot = full.Equals(_rootFull, StringComparison.OrdinalIgnoreCase)
                || full.Equals(_rootFull.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
            if (!isRoot && !File.Exists(full) && !Directory.Exists(full))
            {
                err = "路径不存在";
                return null;
            }

            // refuse to act on a path that is itself a reparse point (could escape the root)
            try
            {
                FileAttributes at = File.GetAttributes(full);
                if ((at & FileAttributes.ReparsePoint) != 0) { err = "不支持对链接/挂载点的操作"; return null; }
            }
            catch { err = "无法读取属性"; return null; }

            // also refuse if any ancestor inside the root is a reparse point (link escape)
            string rootTrim = _rootFull.TrimEnd(Path.DirectorySeparatorChar);
            string cur = Path.GetDirectoryName(full);
            while (!string.IsNullOrEmpty(cur) && !cur.Equals(rootTrim, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    FileAttributes aa = File.GetAttributes(cur);
                    if ((aa & FileAttributes.ReparsePoint) != 0) { err = "路径经过链接/挂载点，已拒绝"; return null; }
                }
                catch { err = "无法读取属性"; return null; }
                string up = Path.GetDirectoryName(cur);
                if (up == cur) break;
                cur = up;
            }

            return full;
        }

        private static string JsonField(string json, string key)
        {
            if (json == null) return null;
            string needle = "\"" + key + "\"";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return null;
            i = json.IndexOf(':', i + needle.Length);
            if (i < 0) return null;
            i++;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\n' || json[i] == '\r')) i++;
            if (i >= json.Length || json[i] != '"') return null;
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < json.Length && json[i] != '"')
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    char n = json[i + 1];
                    if (n == 'n') sb.Append('\n');
                    else if (n == 'r') sb.Append('\r');
                    else if (n == 't') sb.Append('\t');
                    else if (n == '"') sb.Append('"');
                    else if (n == '\\') sb.Append('\\');
                    else if (n == '/') sb.Append('/');
                    else if (n == 'u' && i + 5 < json.Length)
                    {
                        int code;
                        if (int.TryParse(json.Substring(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                        { sb.Append((char)code); i += 4; }
                        else sb.Append(n);
                    }
                    else sb.Append(n);
                    i += 2;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        private static string JsonEsc(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /* --------------------------- responses ---------------------------- */

        private static void WriteHead(NetworkStream ns, int code, string ctype, int len)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(code.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(StatusText(code)).Append("\r\n");
            sb.Append("Content-Type: ").Append(ctype).Append("\r\n");
            sb.Append("Content-Length: ").Append(len.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("Cache-Control: no-store\r\n");
            sb.Append("X-Content-Type-Options: nosniff\r\n");
            sb.Append("X-Frame-Options: DENY\r\n");
            sb.Append("Referrer-Policy: no-referrer\r\n");
            sb.Append("Connection: close\r\n\r\n");
            byte[] h = Encoding.ASCII.GetBytes(sb.ToString());
            ns.Write(h, 0, h.Length);
        }

        private static string StatusText(int code)
        {
            switch (code)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                default: return "OK";
            }
        }

        private static void WriteJson(NetworkStream ns, int code, string json)
        {
            byte[] b = Encoding.UTF8.GetBytes(json);
            WriteHead(ns, code, "application/json; charset=utf-8", b.Length);
            ns.Write(b, 0, b.Length);
        }

        private static void ServeText(NetworkStream ns, int code, string ctype, string text)
        {
            byte[] b = Encoding.UTF8.GetBytes(text);
            WriteHead(ns, code, ctype, b.Length);
            ns.Write(b, 0, b.Length);
        }

        /* --------------------------- shell helpers ------------------------ */

        private static void OpenBrowser(string url)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(url);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch { }
        }

        private const uint FO_DELETE = 0x0003;
        private const ushort FOF_SILENT = 0x0004;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_ALLOWUNDO = 0x0040;   // -> recycle bin
        private const ushort FOF_NOERRORUI = 0x0400;

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

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        private static int Recycle(string full)
        {
            SHFILEOPSTRUCT op = new SHFILEOPSTRUCT();
            op.wFunc = FO_DELETE;
            op.pFrom = full + "\0";            // marshaler appends one more NUL -> double-terminated
            op.pTo = null;
            op.fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI);
            return SHFileOperation(ref op);
        }
    }
}
