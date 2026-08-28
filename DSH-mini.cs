// DSH-mini.exe - DeepSeek Harness ultra-light bootstrap launcher (v2).
//
// Single ~1 MB file. First run downloads Node.js, pnpm and the
// deepseek-harness sources from the network (with mirror fallback,
// real-time progress bar, speed/ETA, and staged error messages with
// retry). Afterwards it starts the built server and embeds the Web UI.
// The uninstaller (卸载.exe) is embedded as a resource and dropped next
// to this exe on every start, so the download stays a single file.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class Program
{
    private const string Url = "http://127.0.0.1:3080";

    // All download links, mirror lists, version pins and network-tuning knobs
    // live in the embedded sources.json (developer-maintained, kept OUT of
    // this source file so a link refresh is a one-line edit + rebuild instead
    // of a code change). LoadConfig() populates Cfg once at startup.
    private static DownloadConfig Cfg;

    private static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
    private static string DshHome { get { return Path.Combine(BaseDir, "deepseek-harness"); } }
    private static string RuntimeDir { get { return Path.Combine(BaseDir, ".launcher"); } }
    private static string NodeExe { get { return Path.Combine(RuntimeDir, @"runtime\node\node.exe"); } }
    private static string PnpmDir { get { return Path.Combine(RuntimeDir, @"runtime\pnpm"); } }
    private static string PnpmMjs { get { return Path.Combine(PnpmDir, @"dist\pnpm.mjs"); } }
    private static string ShaFile { get { return Path.Combine(RuntimeDir, "source.sha"); } }
    private static string ServerLog { get { return Path.Combine(RuntimeDir, "server.log"); } }
    private static string BuildLog { get { return Path.Combine(RuntimeDir, "build.log"); } }
    private static string StampFile { get { return Path.Combine(RuntimeDir, "build.stamp"); } }
    private static string InstallStampFile { get { return Path.Combine(RuntimeDir, "install.stamp"); } }

    [STAThread]
    private static int Main(string[] args)
    {
        Directory.CreateDirectory(RuntimeDir);
        AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbedded;
        ExtractNativeLoader();
        DropUninstaller();
        EnsurePnpmShim();
        try
        {
            // .NET Framework defaults to old TLS; mirrors need 1.2+.
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | (SecurityProtocolType)12288 /* Tls13 */;
        }
        catch { }
        try { SetProcessDPIAware(); } catch { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // One launcher instance per folder: a second launch would race the
        // first one's download/install state in .launcher and both servers
        // would fight over port 3080.
        bool firstInstance;
        string mutexName;
        using (var sha = SHA256.Create())
        {
            mutexName = "DSH-mini-" + BitConverter.ToString(
                sha.ComputeHash(Encoding.UTF8.GetBytes(BaseDir.ToLowerInvariant())))
                .Replace("-", "").Substring(0, 16);
        }
        var instanceMutex = new Mutex(true, mutexName, out firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("DSH-mini 已经在运行中（同一文件夹只允许一个实例）。",
                "DeepSeek Harness", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        try
        {
            try
            {
                Cfg = LoadConfig();
            }
            catch (Exception ex)
            {
                MessageBox.Show("下载源配置加载失败：\r\n" + ex.Message,
                    "DeepSeek Harness", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 0;
            }
            Application.Run(new MainForm());
        }
        finally
        {
            try { instanceMutex.ReleaseMutex(); } catch { }
            instanceMutex.Dispose();
        }
        return 0;
    }

    private static Assembly ResolveEmbedded(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name + ".dll";
        using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        {
            if (st == null) return null;
            var ms = new MemoryStream();
            st.CopyTo(ms);
            return Assembly.Load(ms.ToArray());
        }
    }

    private static void ExtractNativeLoader()
    {
        string target = Path.Combine(RuntimeDir, "WebView2Loader.dll");
        using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream("WebView2Loader.dll"))
        {
            if (st != null && (!File.Exists(target) || new FileInfo(target).Length != st.Length))
            {
                var ms = new MemoryStream();
                st.CopyTo(ms);
                File.WriteAllBytes(target, ms.ToArray());
            }
        }
        SetDllDirectory(RuntimeDir);
    }

    // The uninstaller ships inside this exe; drop it next to us so the
    // user always has a visible way to remove everything later.
    private static void DropUninstaller()
    {
        try
        {
            string target = Path.Combine(BaseDir, "卸载.exe");
            using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream("uninstall.exe"))
            {
                if (st == null) return;
                var ms = new MemoryStream();
                st.CopyTo(ms);
                byte[] bytes = ms.ToArray();
                if (File.Exists(target) && new FileInfo(target).Length == bytes.Length) return;
                File.WriteAllBytes(target, bytes);
            }
        }
        catch { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);
    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")]
    private static extern int GetDpiForSystem();
    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hwnd);

    private static void Log(string line)
    {
        try { File.AppendAllText(BuildLog, "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + line + Environment.NewLine); } catch { }
    }

    private static string PnpmShim { get { return Path.Combine(Path.GetDirectoryName(NodeExe), "pnpm.cmd"); } }

    // Upstream scripts/build.ts spawns "pnpm" BY NAME, which only resolves
    // through PATH. We invoke pnpm.mjs via node by absolute path, so on a
    // machine without a global pnpm the web-frontend build step fails with
    // "'pnpm' 不是内部或外部命令". Drop a pnpm.cmd shim next to node.exe
    // (NewProc prepends that dir to PATH) so the name always resolves.
    private static void EnsurePnpmShim()
    {
        try
        {
            if (!File.Exists(PnpmMjs)) return;
            string content = "@\"%~dp0node.exe\" \"" + PnpmMjs + "\" %*\r\n";
            if (File.Exists(PnpmShim) && File.ReadAllText(PnpmShim) == content) return;
            File.WriteAllText(PnpmShim, content);
        }
        catch { }
    }

    // ---------- download config (embedded sources.json) ----------

    // Mirrors the sources.json schema. Every field is populated by
    // LoadConfig(); nothing here is hardcoded in code.
    private sealed class DownloadConfig
    {
        public List<string> NodeUrls = new List<string>();
        public List<string> PnpmZipUrls = new List<string>();
        public List<string> PnpmTgzUrls = new List<string>();
        public string PinnedSha = "";
        public string RepoApiUrl = "";
        public List<string> RepoZipTemplates = new List<string>();
        public string WebView2Bootstrapper = "";
        public List<string> NpmRegistryUrls = new List<string>();
        public bool ProbeEnabled = true;
        public int ProbeTimeoutMs = 4000;
        public int StallTimeoutMs = 20000;
        public int OverallTimeoutMs = 900000;
        public int FirstSourceAttempts = 2;
        public int RetryDelayMs = 1500;
        public int ProbeSampleBytes = 262144;
        public int InstallTimeoutMs = 1800000;
        public int BuildTimeoutMs = 1200000;
        public int FetchTimeoutMs = 600000;
        public int FetchRetries = 3;
        public int NetworkConcurrency = 8;
    }

    // Minimal dependency-free JSON parser for the embedded sources.json.
    // Supports objects, arrays, strings (full escape sequences), numbers,
    // true/false/null, plus // and /* */ comments between tokens. Values come
    // back as Dictionary<string,object>, List<object>, string, double, bool
    // or null. Comment stripping happens only in SkipWs(), which is never
    // reached from inside a string literal, so "https://" inside a URL value
    // is preserved verbatim.
    private static class Json
    {
        public static object Parse(string text)
        {
            var r = new Reader(text);
            object v = r.Value();
            r.SkipWs();
            if (!r.AtEnd) throw new FormatException("JSON 解析失败：多余内容，位置 " + r.Pos);
            return v;
        }

        private sealed class Reader
        {
            private readonly string s;
            private int i;

            public Reader(string s) { this.s = s; }

            public int Pos { get { return i; } }
            public bool AtEnd { get { return i >= s.Length; } }

            public void SkipWs()
            {
                while (i < s.Length)
                {
                    char c = s[i];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { i++; continue; }
                    if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                    {
                        i += 2;
                        while (i < s.Length && s[i] != '\n') i++;
                        continue;
                    }
                    if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
                    {
                        i += 2;
                        int end = s.IndexOf("*/", i, StringComparison.Ordinal);
                        if (end < 0) throw new FormatException("JSON 块注释未闭合");
                        i = end + 2;
                        continue;
                    }
                    break;
                }
            }

            private char Peek()
            {
                SkipWs();
                if (i >= s.Length) throw new FormatException("JSON 意外结束");
                return s[i];
            }

            private bool Try(char c)
            {
                SkipWs();
                if (i < s.Length && s[i] == c) { i++; return true; }
                return false;
            }

            private void Expect(char c)
            {
                if (!Try(c)) throw new FormatException("JSON 期望 '" + c + "'，位置 " + i);
            }

            public object Value()
            {
                char c = Peek();
                if (c == '{') return Obj();
                if (c == '[') return Arr();
                if (c == '"') return Str();
                if (c == 't') { Lit("true"); return true; }
                if (c == 'f') { Lit("false"); return false; }
                if (c == 'n') { Lit("null"); return null; }
                if (c == '-' || (c >= '0' && c <= '9')) return Num();
                throw new FormatException("JSON 非法字符 '" + c + "'，位置 " + i);
            }

            private void Lit(string word)
            {
                for (int k = 0; k < word.Length; k++)
                {
                    if (i + k >= s.Length || s[i + k] != word[k])
                        throw new FormatException("JSON 非法字面量，位置 " + i);
                }
                i += word.Length;
            }

            private Dictionary<string, object> Obj()
            {
                var map = new Dictionary<string, object>();
                Expect('{');
                if (Try('}')) return map;
                while (true)
                {
                    SkipWs();
                    if (Peek() != '"') throw new FormatException("JSON 对象键必须为字符串，位置 " + i);
                    string key = Str();
                    Expect(':');
                    map[key] = Value();
                    if (Try('}')) return map;
                    Expect(',');
                }
            }

            private List<object> Arr()
            {
                var list = new List<object>();
                Expect('[');
                if (Try(']')) return list;
                while (true)
                {
                    list.Add(Value());
                    if (Try(']')) return list;
                    Expect(',');
                }
            }

            private string Str()
            {
                Expect('"');
                var sb = new StringBuilder();
                while (true)
                {
                    if (i >= s.Length) throw new FormatException("JSON 字符串未闭合");
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c == '\\')
                    {
                        if (i >= s.Length) throw new FormatException("JSON 转义未闭合");
                        char e = s[i++];
                        switch (e)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                if (i + 4 > s.Length) throw new FormatException("JSON \\u 转义不完整");
                                sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                                i += 4;
                                break;
                            default: throw new FormatException("JSON 非法转义 '\\" + e + "'");
                        }
                    }
                    else sb.Append(c);
                }
            }

            private double Num()
            {
                int start = i;
                if (s[i] == '-') i++;
                while (i < s.Length && ((s[i] >= '0' && s[i] <= '9') || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '+' || s[i] == '-')) i++;
                double v;
                if (!double.TryParse(s.Substring(start, i - start), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out v))
                    throw new FormatException("JSON 非法数字，位置 " + start);
                return v;
            }
        }
    }

    private static Dictionary<string, object> CfgObj(object v, string where)
    {
        var d = v as Dictionary<string, object>;
        if (d == null) throw new FormatException("sources.json 中 " + where + " 应为对象");
        return d;
    }

    private static object CfgGet(Dictionary<string, object> o, string key)
    {
        object v;
        o.TryGetValue(key, out v);
        return v;
    }

    private static string CfgStr(Dictionary<string, object> o, string key)
    {
        return CfgGet(o, key) as string;
    }

    private static List<string> CfgStrList(Dictionary<string, object> o, string key)
    {
        var list = CfgGet(o, key) as List<object>;
        var result = new List<string>();
        if (list == null) return result;
        foreach (object item in list)
        {
            string str = item as string;
            if (str != null) result.Add(str);
        }
        return result;
    }

    private static int CfgInt(Dictionary<string, object> o, string key, int def)
    {
        object v = CfgGet(o, key);
        if (v is double) return (int)(double)v;
        if (v is string) { int n; if (int.TryParse((string)v, out n)) return n; }
        return def;
    }

    private static bool CfgBool(Dictionary<string, object> o, string key, bool def)
    {
        object v = CfgGet(o, key);
        return v is bool ? (bool)v : def;
    }

    private static DownloadConfig LoadConfig()
    {
        using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream("sources.json"))
        {
            if (st == null)
                throw new InvalidOperationException("缺少内嵌资源 sources.json（构建时未打包）");
            using (var sr = new StreamReader(st, Encoding.UTF8))
            {
                var root = CfgObj(Json.Parse(sr.ReadToEnd()), "根节点");
                var node = CfgObj(CfgGet(root, "node"), "node");
                var pnpm = CfgObj(CfgGet(root, "pnpm"), "pnpm");
                var repo = CfgObj(CfgGet(root, "repo"), "repo");
                object npmObj = CfgGet(root, "npm");
                var npmUrls = npmObj != null ? CfgStrList(CfgObj(npmObj, "npm"), "registry") : new List<string>();
                if (npmUrls.Count == 0) npmUrls.Add("https://registry.npmjs.org");

                var cfg = new DownloadConfig
                {
                    NodeUrls = CfgStrList(node, "urls"),
                    PnpmZipUrls = CfgStrList(pnpm, "zip"),
                    PnpmTgzUrls = CfgStrList(pnpm, "tgz"),
                    PinnedSha = CfgStr(repo, "pinnedSha") ?? "",
                    RepoApiUrl = CfgStr(repo, "apiUrl") ?? "",
                    RepoZipTemplates = CfgStrList(repo, "zipTemplates"),
                    WebView2Bootstrapper = CfgStr(root, "webView2Bootstrapper") ?? "",
                };
                cfg.NpmRegistryUrls = npmUrls;

                object tuningObj = CfgGet(root, "tuning");
                if (tuningObj != null)
                {
                    var tuning = CfgObj(tuningObj, "tuning");
                    cfg.ProbeEnabled = CfgBool(tuning, "probeEnabled", true);
                    cfg.ProbeTimeoutMs = CfgInt(tuning, "probeTimeoutMs", 4000);
                    cfg.StallTimeoutMs = CfgInt(tuning, "stallTimeoutMs", 20000);
                    cfg.OverallTimeoutMs = CfgInt(tuning, "overallTimeoutMs", 900000);
                    cfg.FirstSourceAttempts = CfgInt(tuning, "firstSourceAttempts", 2);
                    cfg.RetryDelayMs = CfgInt(tuning, "retryDelayMs", 1500);
                    cfg.ProbeSampleBytes = CfgInt(tuning, "probeSampleBytes", 262144);
                    cfg.InstallTimeoutMs = CfgInt(tuning, "installTimeoutMs", 1800000);
                    cfg.BuildTimeoutMs = CfgInt(tuning, "buildTimeoutMs", 1200000);
                    cfg.FetchTimeoutMs = CfgInt(tuning, "fetchTimeoutMs", 600000);
                    cfg.FetchRetries = CfgInt(tuning, "fetchRetries", 3);
                    cfg.NetworkConcurrency = CfgInt(tuning, "networkConcurrency", 8);
                }

                // Required fields: fail loud on a malformed config instead of
                // silently downloading nothing.
                if (cfg.NodeUrls.Count == 0) throw new FormatException("sources.json: node.urls 为空");
                if (cfg.PnpmZipUrls.Count == 0 && cfg.PnpmTgzUrls.Count == 0) throw new FormatException("sources.json: pnpm 没有任何下载源");
                if (cfg.PinnedSha.Length == 0) throw new FormatException("sources.json: repo.pinnedSha 为空");
                if (cfg.RepoZipTemplates.Count == 0) throw new FormatException("sources.json: repo.zipTemplates 为空");
                if (cfg.StallTimeoutMs < 1000) cfg.StallTimeoutMs = 1000;
                if (cfg.ProbeTimeoutMs < 500) cfg.ProbeTimeoutMs = 500;
                if (cfg.FirstSourceAttempts < 1) cfg.FirstSourceAttempts = 1;

                return cfg;
            }
        }
    }

    // ---------- download: probe-based source selection + stall/timeout ----------

    private delegate void ProgressHandler(long got, long? total);

    private static HttpClient NewHttp()
    {
        var client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DSH-mini-launcher");
        return client;
    }

    // One probe's outcome: time-to-first-byte (latency) plus a small
    // throughput sample over the first probeSampleBytes. Throughput is the
    // primary ranking signal for large downloads; latency breaks ties.
    private sealed class ProbeResult
    {
        public double LatencyMs;
        public double ThroughputMbps; // MB/s over the sample window
    }

    // Probe one URL by downloading only the first sampleBytes (Range GET).
    // Returns null when the source is unreachable / errors within timeoutMs.
    // The read loop is hard-capped at sampleBytes, so even a server that
    // ignores the Range header only costs us a bounded amount of traffic.
    private static async Task<ProbeResult> ProbeAsync(string url, int timeoutMs, int sampleBytes)
    {
        try
        {
            using (var client = NewHttp())
            {
                client.Timeout = TimeSpan.FromMilliseconds(timeoutMs);
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                using (var cts = new CancellationTokenSource(timeoutMs))
                {
                    req.Headers.Range = new RangeHeaderValue(0, sampleBytes - 1);
                    var sw = Stopwatch.StartNew();
                    using (var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                    {
                        resp.EnsureSuccessStatusCode();
                        using (Stream input = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        {
                            var buf = new byte[16384];
                            long bytes = 0;
                            int read = await input.ReadAsync(buf, 0, buf.Length, cts.Token).ConfigureAwait(false);
                            if (read <= 0) return null;
                            double latency = sw.Elapsed.TotalMilliseconds;
                            bytes += read;
                            while (bytes < sampleBytes)
                            {
                                read = await input.ReadAsync(buf, 0, buf.Length, cts.Token).ConfigureAwait(false);
                                if (read <= 0) break;
                                bytes += read;
                            }
                            sw.Stop();
                            double seconds = Math.Max(1, sw.Elapsed.TotalMilliseconds) / 1000.0;
                            return new ProbeResult
                            {
                                LatencyMs = latency,
                                ThroughputMbps = bytes / 1048576.0 / seconds,
                            };
                        }
                    }
                }
            }
        }
        catch { return null; }
    }

    // Reorder source indices from best (reachable, highest throughput) to
    // worst (unreachable keep their file order).
    private static int[] RankSources(ProbeResult[] probes)
    {
        return Enumerable.Range(0, probes.Length)
            .OrderBy(i => probes[i] == null ? 1 : 0)
            .ThenByDescending(i => probes[i] == null ? 0 : probes[i].ThroughputMbps)
            .ThenBy(i => probes[i] == null ? double.MaxValue : probes[i].LatencyMs)
            .ToArray();
    }

    // One read with a stall watchdog: if no bytes arrive for stallTimeoutMs
    // the read is cancelled and reported as a stall instead of hanging.
    private static async Task<int> ReadWithStallTimeout(Stream s, byte[] buf, int off, int count,
        int stallTimeoutMs, CancellationToken ct)
    {
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(stallTimeoutMs);
            try
            {
                return await s.ReadAsync(buf, off, count, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The overall download timeout cancels the parent token — that
                // must keep propagating (DownloadFileAsync re-wraps it into a
                // clearer message). A stall only cancels THIS read's linked
                // token, so it is reported as a stall here.
                if (ct.IsCancellationRequested) throw;
                throw new TimeoutException("下载停滞：连续 " + (stallTimeoutMs / 1000) + " 秒没有收到任何数据");
            }
        }
    }

    private static async Task DownloadFileAsync(string url, string dest, ProgressHandler progress,
        int stallTimeoutMs, int overallTimeoutMs)
    {
        string tmp = dest + ".download";
        Directory.CreateDirectory(Path.GetDirectoryName(dest));
        using (var cts = new CancellationTokenSource(overallTimeoutMs))
        using (var client = NewHttp())
        {
            try
            {
                using (var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    long? total = resp.Content.Headers.ContentLength;
                    using (Stream input = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (Stream output = File.Create(tmp))
                    {
                        var buf = new byte[262144];
                        long got = 0;
                        int read;
                        while ((read = await ReadWithStallTimeout(input, buf, 0, buf.Length, stallTimeoutMs, cts.Token).ConfigureAwait(false)) > 0)
                        {
                            await output.WriteAsync(buf, 0, read, cts.Token).ConfigureAwait(false);
                            got += read;
                            if (progress != null) progress(got, total);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (cts.IsCancellationRequested)
                    throw new TimeoutException("下载超时：超过 " + (overallTimeoutMs / 60000) + " 分钟仍未完成");
                throw;
            }
        }
        if (File.Exists(dest)) File.Delete(dest);
        File.Move(tmp, dest);
    }

    private static void ValidateZip(string path)
    {
        using (var zip = ZipFile.OpenRead(path))
        {
            if (zip.Entries.Count == 0) throw new InvalidDataException("zip 为空");
        }
    }

    // Full download-with-recovery pipeline:
    //  1. probe every candidate concurrently and prefer the fastest reachable
    //     source for the current network conditions;
    //  2. attempt sources in that order — the best source gets extra attempts
    //     with backoff; every attempt has stall + overall timeouts and leaves
    //     no partial file behind on failure;
    //  3. when the probe shows EVERY source unreachable, fail fast with a
    //     "network down" error instead of burning minutes retrying.
    private static async Task DownloadWithFallbackAsync(string label, List<string> urls, string dest,
        bool validateAsZip, Action<string> setStatus, ProgressHandler progress)
    {
        var order = Enumerable.Range(0, urls.Count).ToArray();

        if (Cfg.ProbeEnabled && urls.Count > 1)
        {
            setStatus(label + "：测速选择最快下载源…");
            ProbeResult[] probes = null;
            try
            {
                probes = await Task.WhenAll(urls.Select(u => ProbeAsync(u, Cfg.ProbeTimeoutMs, Cfg.ProbeSampleBytes))).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log(label + " 测速异常（按配置顺序下载）: " + ex.Message);
            }
            if (probes != null)
            {
                order = RankSources(probes);
                for (int i = 0; i < urls.Count; i++)
                {
                    string info = probes[i] == null ? "不可达"
                        : probes[i].ThroughputMbps.ToString("0.0") + " MB/s · " + probes[i].LatencyMs.ToString("0") + " ms";
                    Log(label + " 测速[" + (Array.IndexOf(order, i) + 1) + "] " + urls[i] + " -> " + info);
                }
                if (probes.All(p => p == null))
                    throw new InvalidOperationException("网络连接不可用：所有下载源均无法连通，请检查网络后重试");
            }
        }

        var failures = new List<string>();
        for (int rank = 0; rank < order.Length; rank++)
        {
            string url = urls[order[rank]];
            int attempts = rank == 0 ? Cfg.FirstSourceAttempts : 1;
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    if (rank > 0 && attempt == 1)
                        setStatus(label + "：切换到备用源 " + rank + " …");
                    else if (attempt > 1)
                        setStatus(label + "：第 " + attempt + " 次重试…");
                    if (attempt > 1) await Task.Delay(Cfg.RetryDelayMs).ConfigureAwait(false);
                    await DownloadFileAsync(url, dest, progress, Cfg.StallTimeoutMs, Cfg.OverallTimeoutMs).ConfigureAwait(false);
                    if (validateAsZip) ValidateZip(dest);
                    Log(label + " 下载完成 <- " + url);
                    return;
                }
                catch (Exception ex)
                {
                    failures.Add(url + " -> " + ex.Message);
                    Log(label + " 下载失败(第 " + attempt + " 次): " + ex.Message + " <- " + url);
                    try { if (File.Exists(dest)) File.Delete(dest); } catch { }
                    try { if (File.Exists(dest + ".download")) File.Delete(dest + ".download"); } catch { }
                }
            }
        }
        throw new InvalidOperationException(label + " 全部下载源均失败：\r\n" + string.Join("\r\n", failures));
    }

    // ---------- archive extraction ----------

    // Join an archive entry path onto its destination and reject anything
    // that would escape it ("zip slip"). The archives come from mirrors
    // over the network, so their entry names are untrusted input.
    private static string SafeCombine(string destDir, string rel)
    {
        string root = Path.GetFullPath(destDir).TrimEnd('\\') + "\\";
        string target = Path.GetFullPath(Path.Combine(root, rel));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("压缩包包含越界路径条目: " + rel);
        return target;
    }

    private static void ExtractZipFiltered(string zipPath, string destDir, Func<string, bool> include)
    {
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in zip.Entries)
            {
                if (!include(entry.FullName)) continue;
                string target = SafeCombine(destDir, entry.FullName.Replace('/', '\\'));
                if (entry.FullName.EndsWith("/"))
                {
                    Directory.CreateDirectory(target);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, true);
                }
            }
        }
    }

    private static int ReadFull(Stream s, byte[] buf, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int r = s.Read(buf, offset + total, count - total);
            if (r <= 0) break;
            total += r;
        }
        return total;
    }

    private static void SkipBytes(Stream s, long count)
    {
        var buf = new byte[65536];
        while (count > 0)
        {
            int r = s.Read(buf, 0, (int)Math.Min(buf.Length, count));
            if (r <= 0) break;
            count -= r;
        }
    }

    // Minimal tar.gz reader (npm tarballs use short ustar names; GNU 'L'
    // long-name entries are handled as well).
    private static void ExtractTarGz(string tgzPath, string destDir, string stripPrefix, Func<string, bool> include)
    {
        using (var fs = File.OpenRead(tgzPath))
        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
        {
            var header = new byte[512];
            string pendingLongName = null;
            while (true)
            {
                if (ReadFull(gz, header, 0, 512) < 512) break;
                string name = Encoding.UTF8.GetString(header, 0, 100).TrimEnd('\0');
                string sizeText = Encoding.ASCII.GetString(header, 124, 12).Trim('\0', ' ');
                if (sizeText.Length == 0) break;
                long size = Convert.ToInt64(sizeText, 8);
                char type = (char)header[156];
                long dataPad = (512 - (size % 512)) % 512;

                if (type == 'L') // GNU long name for the NEXT entry
                {
                    var nameBuf = new byte[(int)size];
                    ReadFull(gz, nameBuf, 0, (int)size);
                    pendingLongName = Encoding.UTF8.GetString(nameBuf).TrimEnd('\0');
                    SkipBytes(gz, dataPad);
                    continue;
                }
                if (pendingLongName != null) { name = pendingLongName; pendingLongName = null; }

                if (type == 'x' || type == 'g') { SkipBytes(gz, size + dataPad); continue; }

                string rel = name.Replace('/', '\\');
                if (stripPrefix != null && rel.StartsWith(stripPrefix, StringComparison.Ordinal))
                    rel = rel.Substring(stripPrefix.Length);
                bool wanted = rel.Length > 0 && (include == null || include(name));
                if (type == '5')
                {
                    if (wanted) Directory.CreateDirectory(SafeCombine(destDir, rel));
                    SkipBytes(gz, size + dataPad);
                }
                else if (type == '0' || type == '\0')
                {
                    if (wanted)
                    {
                        string target = SafeCombine(destDir, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (var outFile = File.Create(target))
                        {
                            long left = size;
                            var buf = new byte[65536];
                            while (left > 0)
                            {
                                int r = gz.Read(buf, 0, (int)Math.Min(buf.Length, left));
                                if (r <= 0) break;
                                outFile.Write(buf, 0, r);
                                left -= r;
                            }
                        }
                    }
                    else SkipBytes(gz, size);
                    SkipBytes(gz, dataPad);
                }
                else { SkipBytes(gz, size + dataPad); }
            }
        }
    }

    // ---------- sha resolution ----------

    private static string ResolveMasterSha(Action<string> setStatus)
    {
        try
        {
            using (var client = NewHttp())
            {
                string json = client.GetStringAsync(Cfg.RepoApiUrl).Result;
                var m = System.Text.RegularExpressions.Regex.Match(json, "\"sha\"\\s*:\\s*\"([0-9a-f]{40})\"");
                if (m.Success) return m.Groups[1].Value;
            }
        }
        catch (Exception ex)
        {
            Log("GitHub API 不可达: " + ex.Message);
        }
        setStatus("无法访问 GitHub API，改用内置已知版本 " + Cfg.PinnedSha.Substring(0, 7) + " ...");
        return Cfg.PinnedSha;
    }

    // ---------- process helpers ----------

    private static ProcessStartInfo NewProc(string file, string args, string workdir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            WorkingDirectory = workdir,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (File.Exists(NodeExe))
        {
            psi.EnvironmentVariables["PATH"] =
                Path.GetDirectoryName(NodeExe) + ";" + psi.EnvironmentVariables["PATH"];
        }
        return psi;
    }

    // ---------- subprocess run + failure classification ----------

    // Coarse reason for a failed install/build subprocess, detected by
    // scanning its output. Used to give a targeted message and to decide
    // whether an automatic mirror switch is worth trying.
    private enum FailKind
    {
        // Higher value = higher priority; Classify only ever upgrades.
        None = 0,
        Permission,
        DiskFull,
        Network,
        Timeout,
        Other,
    }

    private sealed class RunResult
    {
        public int ExitCode;
        public bool TimedOut;
        public FailKind Kind = FailKind.None;
        public string Tail = "";
        // True when the subprocess warned it couldn't create a bin shim —
        // typically because a package's "bin" points at a build output
        // (e.g. lib/bin.js) that only exists after `pnpm run build`.
        public bool BinLinkWarned;
    }

    // Upgrade `kind` in place when a line matches a known failure signature
    // (priority Timeout > Network > DiskFull > Permission).
    private static void Classify(string line, ref FailKind kind)
    {
        string s = line.ToLowerInvariant();
        if (s.Contains("aborted due to timeout") || s.Contains("timeouterror")
            || s.Contains("timed out") || s.Contains("operation timed out"))
        {
            if (kind < FailKind.Timeout) kind = FailKind.Timeout;
            return;
        }
        if (s.Contains("error (23)") || s.Contains("econnreset") || s.Contains("etimedout")
            || s.Contains("enotfound") || s.Contains("eai_again") || s.Contains("getaddrinfo")
            || s.Contains("socket hang up") || s.Contains("unable to resolve")
            || s.Contains("network") || s.Contains("certificate"))
        {
            if (kind < FailKind.Network) kind = FailKind.Network;
            return;
        }
        if (s.Contains("enospc") || s.Contains("no space left") || s.Contains("disk full")
            || s.Contains("not enough space"))
        {
            if (kind < FailKind.DiskFull) kind = FailKind.DiskFull;
            return;
        }
        if (s.Contains("eacces") || s.Contains("eperm") || s.Contains("permission denied")
            || s.Contains("access is denied") || s.Contains("operation not permitted"))
        {
            if (kind < FailKind.Permission) kind = FailKind.Permission;
        }
    }

    private static bool IsNetworkFailure(RunResult r)
    {
        return r.TimedOut || r.Kind == FailKind.Timeout || r.Kind == FailKind.Network;
    }

    private static string DescribeFailKind(FailKind k)
    {
        switch (k)
        {
            case FailKind.Timeout: return "网络超时";
            case FailKind.Network: return "网络连接不稳定或不可用";
            case FailKind.DiskFull: return "磁盘空间不足";
            case FailKind.Permission: return "权限不足";
            default: return "未知错误";
        }
    }

    private static string DescribeRunFailure(string stage, RunResult r, int timeoutMs)
    {
        if (r.TimedOut)
            return stage + "失败：超时（超过 " + (timeoutMs / 60000) + " 分钟未完成，已自动终止）";
        return stage + "失败（" + DescribeFailKind(r.Kind) + "）";
    }

    private static string SuggestFor(FailKind k)
    {
        switch (k)
        {
            case FailKind.Timeout:
            case FailKind.Network:
                return "当前网络访问依赖源超时或不稳定，可点击“重试”再次尝试（已尝试自动切换镜像）。";
            case FailKind.DiskFull:
                return "磁盘空间不足，请清理磁盘后重试。";
            case FailKind.Permission:
                return "权限不足，请检查杀毒软件或系统安全策略是否拦截了本程序。";
            default:
                return "详细日志见 .launcher\\build.log。";
        }
    }

    private static RunResult RunLogged(string file, string args, string workdir, string commitHash, int timeoutMs, string registry)
    {
        var result = new RunResult();
        try
        {
            var psi = NewProc(file, args, workdir);
            if (commitHash != null) psi.EnvironmentVariables["DSH_CLIENT_COMMIT_HASH"] = commitHash;
            // Keep npm-ecosystem downloads (e.g. sharp's libvips) inside
            // .launcher so uninstall removes everything it fetched.
            psi.EnvironmentVariables["npm_config_cache"] = Path.Combine(RuntimeDir, "npm-cache");
            // Belt-and-suspenders for any nested `npm` a postinstall may run.
            if (!string.IsNullOrEmpty(registry)) psi.EnvironmentVariables["npm_config_registry"] = registry;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            // node/tsdown emit UTF-8; without this the log shows GBK mojibake.
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            using (var p = Process.Start(psi))
            {
                var sync = new object();
                var tail = new Queue<string>();
                var kind = FailKind.None;
                var binLinkWarned = false;
                DataReceivedEventHandler handler = (s, e) =>
                {
                    if (e.Data == null) return;
                    lock (sync)
                    {
                        File.AppendAllText(BuildLog, e.Data + Environment.NewLine);
                        tail.Enqueue(e.Data);
                        while (tail.Count > 60) tail.Dequeue();
                        Classify(e.Data, ref kind);
                        if (e.Data.ToLowerInvariant().Contains("failed to create bin")) binLinkWarned = true;
                    }
                };
                p.OutputDataReceived += handler;
                p.ErrorDataReceived += handler;
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                if (!p.WaitForExit(timeoutMs))
                {
                    result.TimedOut = true;
                    try
                    {
                        using (Process.Start(new ProcessStartInfo("taskkill", "/PID " + p.Id + " /T /F")
                        { UseShellExecute = false, CreateNoWindow = true })) { }
                    }
                    catch { }
                    try { if (!p.WaitForExit(3000)) p.Kill(); } catch { }
                    lock (sync) File.AppendAllText(BuildLog, "[DSH-mini] 进程超时(" + timeoutMs + "ms)，已强制结束" + Environment.NewLine);
                }
                // Let the async stdout/stderr readers drain their final lines
                // so the captured tail is as complete as possible.
                Thread.Sleep(120);
                result.ExitCode = p.ExitCode;
                result.Kind = result.TimedOut ? FailKind.Timeout : kind;
                result.BinLinkWarned = binLinkWarned;
                lock (sync) result.Tail = string.Join(Environment.NewLine, tail);
                return result;
            }
        }
        catch (Exception ex)
        {
            Log("进程启动失败: " + ex.Message);
            result.ExitCode = 1;
            result.Kind = FailKind.Other;
            result.Tail = ex.Message;
            return result;
        }
    }

    private static RunResult RunPnpm(string args, string workdir, string commitHash, int timeoutMs, string registry)
    {
        return RunLogged(NodeExe, "\"" + PnpmMjs + "\" " + args, workdir, commitHash, timeoutMs, registry);
    }

    // ---------- build freshness ----------

    private static readonly string[] SentinelFiles =
    {
        "package.json", "pnpm-lock.yaml", "pnpm-workspace.yaml", "tsdown.config.ts",
        "tsconfig.json", @"apps\cli\package.json", @"apps\web\package.json", @"website\package.json",
    };

    // Inputs that determine whether `pnpm install` needs to re-run.
    private static readonly string[] InstallInputFiles =
    {
        "package.json", "pnpm-lock.yaml", "pnpm-workspace.yaml",
    };

    private static string FingerprintOf(string prefix, string[] rels)
    {
        var sb = new StringBuilder(prefix + "\n");
        foreach (string rel in rels)
        {
            string p = Path.Combine(DshHome, rel);
            if (!File.Exists(p)) continue;
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(p))
            {
                sb.Append(rel).Append('=')
                  .Append(BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant())
                  .Append('\n');
            }
        }
        return sb.ToString();
    }

    private static string ContentFingerprint()
    {
        return FingerprintOf("content", SentinelFiles);
    }

    private static string InstallFingerprint()
    {
        return FingerprintOf("install", InstallInputFiles);
    }

    private static bool MatchesStamp()
    {
        string binJs = Path.Combine(DshHome, @"apps\cli\lib\bin.js");
        if (!File.Exists(binJs) || !File.Exists(StampFile)) return false;
        return File.ReadAllText(StampFile) == ContentFingerprint();
    }

    // Install is "done" only when node_modules exists AND the install.stamp
    // (written last, on success) matches the current package manifests. An
    // interrupted install leaves no stamp, so the next run re-installs —
    // pnpm resumes from its store instead of re-downloading everything.
    private static bool InstallDone()
    {
        if (!Directory.Exists(Path.Combine(DshHome, "node_modules"))) return false;
        if (!File.Exists(InstallStampFile)) return false;
        try { return File.ReadAllText(InstallStampFile) == InstallFingerprint(); }
        catch { return false; }
    }

    // Source is "present" only when BOTH the completion marker (source.sha,
    // written last after a full extraction) AND the extracted tree's own
    // package.json still exist. source.sha can outlive a deleted / quarantined
    // deepseek-harness directory, so checking only the marker would let the
    // launcher skip the download and run install/build in a missing directory.
    private static bool SourcePresent()
    {
        return File.Exists(ShaFile)
            && File.Exists(Path.Combine(DshHome, "package.json"));
    }

    // Probe the configured npm registries and return them fastest-first.
    // Metadata requests are tiny, so reachability + latency are the signal.
    private static List<string> OrderRegistries()
    {
        var regs = Cfg.NpmRegistryUrls;
        if (regs.Count <= 1) return new List<string>(regs);
        try
        {
            var probes = Task.WhenAll(regs.Select(u => ProbeAsync(u, Cfg.ProbeTimeoutMs, 16384))).GetAwaiter().GetResult();
            return RankSources(probes).Select(i => regs[i]).ToList();
        }
        catch
        {
            return new List<string>(regs);
        }
    }

    // ---------- bootstrap stages ----------

    private sealed class StageContext
    {
        public Action<string> SetStatus;
        public Action<int?, string> SetProgress; // percent (null = marquee), detail text
    }

    private static ProgressHandler DownloadProgress(StageContext ctx, string label)
    {
        var clock = Stopwatch.StartNew();
        long lastTick = 0, lastGot = 0;
        double speed = 0;
        return (got, total) =>
        {
            long now = clock.ElapsedMilliseconds;
            if (now - lastTick < 200) return;
            double instant = (got - lastGot) / 1048576.0 / Math.Max(1, now - lastTick) * 1000.0;
            speed = speed <= 0 ? instant : speed * 0.7 + instant * 0.3;
            lastTick = now;
            lastGot = got;
            int? percent = total.HasValue && total.Value > 0 ? (int?)(got * 100 / total.Value) : null;
            string detail = label + "  " + got / 1048576 + (total.HasValue ? " / " + total.Value / 1048576 : "") + " MB"
                + " · " + speed.ToString("0.0") + " MB/s";
            if (total.HasValue && speed > 0.05)
            {
                long remainSec = (long)((total.Value - got) / 1048576.0 / speed);
                detail += " · 剩余约 " + (remainSec >= 60 ? (remainSec / 60) + " 分 " + (remainSec % 60) + " 秒" : remainSec + " 秒");
            }
            ctx.SetProgress(percent, detail);
        };
    }

    private static bool Bootstrap(StageContext ctx, out string error)
    {
        error = null;
        try
        {
            // 1/4 Node.js
            if (!File.Exists(NodeExe))
            {
                string zip = Path.Combine(RuntimeDir, "node.zip");
                ctx.SetStatus("[1/4] 下载 Node.js");
                DownloadWithFallbackAsync("Node.js", Cfg.NodeUrls, zip, true, ctx.SetStatus, DownloadProgress(ctx, "Node.js")).GetAwaiter().GetResult();
                ctx.SetStatus("[1/4] 解压 Node.js...");
                ctx.SetProgress(null, "解压中...");
                using (var nodeZip = ZipFile.OpenRead(zip))
                {
                    foreach (var entry in nodeZip.Entries)
                    {
                        if (entry.FullName.EndsWith("/node.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(NodeExe));
                            entry.ExtractToFile(NodeExe, true);
                            break;
                        }
                    }
                }
                if (!File.Exists(NodeExe)) throw new InvalidDataException("Node.js 压缩包中未找到 node.exe");
                File.Delete(zip);
            }
            // 2/4 pnpm (only dist\ is needed; run via node). Probe ALL pnpm
            // sources (GitHub release zip + npm tarballs) together so the
            // fastest mirror wins regardless of format, instead of always
            // starting with the official GitHub zip (often slow on CN links).
            if (!File.Exists(PnpmMjs))
            {
                ctx.SetStatus("[2/4] 下载 pnpm");
                ctx.SetProgress(null, "");

                // Unified source list: url -> isTgz (the format selects the
                // matching extractor once the winner is downloaded).
                var sources = new List<KeyValuePair<string, bool>>();
                foreach (string u in Cfg.PnpmZipUrls) sources.Add(new KeyValuePair<string, bool>(u, false));
                foreach (string u in Cfg.PnpmTgzUrls) sources.Add(new KeyValuePair<string, bool>(u, true));

                var order = Enumerable.Range(0, sources.Count).ToArray();
                if (Cfg.ProbeEnabled && sources.Count > 1)
                {
                    ctx.SetStatus("[2/4] 测速选择最快 pnpm 源…");
                    ProbeResult[] probes = null;
                    try
                    {
                        probes = Task.WhenAll(sources.Select(s => ProbeAsync(s.Key, Cfg.ProbeTimeoutMs, Cfg.ProbeSampleBytes))).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        Log("pnpm 测速异常（按配置顺序下载）: " + ex.Message);
                    }
                    if (probes != null)
                    {
                        order = RankSources(probes);
                        for (int i = 0; i < sources.Count; i++)
                        {
                            string info = probes[i] == null ? "不可达" : probes[i].ThroughputMbps.ToString("0.0") + " MB/s";
                            Log("pnpm 测速[" + (Array.IndexOf(order, i) + 1) + "] " + sources[i].Key + " -> " + info);
                        }
                        if (probes.All(p => p == null))
                            throw new InvalidOperationException("网络连接不可用：所有 pnpm 源均无法连通，请检查网络后重试");
                    }
                }

                bool downloaded = false;
                var failures = new List<string>();
                foreach (int idx in order)
                {
                    var src = sources[idx];
                    string url = src.Key;
                    bool isTgz = src.Value;
                    string dest = Path.Combine(RuntimeDir, isTgz ? "pnpm.tgz" : "pnpm.zip");
                    try
                    {
                        DownloadWithFallbackAsync("pnpm", new List<string> { url }, dest, !isTgz, ctx.SetStatus, DownloadProgress(ctx, "pnpm")).GetAwaiter().GetResult();
                        ctx.SetStatus("[2/4] 解压 pnpm...");
                        if (isTgz)
                            ExtractTarGz(dest, PnpmDir, "package\\", name => name.StartsWith("package/dist/", StringComparison.Ordinal));
                        else
                            ExtractZipFiltered(dest, PnpmDir, name => name.StartsWith("dist/", StringComparison.Ordinal));
                        File.Delete(dest);
                        downloaded = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        failures.Add(url + " -> " + ex.Message);
                        Log("pnpm 下载失败: " + ex.Message + " <- " + url);
                        try { if (File.Exists(dest)) File.Delete(dest); } catch { }
                    }
                }
                if (!downloaded)
                {
                    error = "pnpm 下载失败：\r\n" + string.Join("\r\n", failures);
                    return false;
                }
            }
            // 3/4 sources — re-download unless BOTH source.sha (written last,
            // after a full extraction) and the extracted package.json exist.
            // This covers an interrupted extraction AND a source directory that
            // was deleted/quarantined while source.sha survived.
            string sha = File.Exists(ShaFile) ? File.ReadAllText(ShaFile).Trim() : null;
            if (!SourcePresent())
            {
                ctx.SetStatus("[3/4] 查询 deepseek-harness 最新提交...");
                ctx.SetProgress(null, "");
                sha = ResolveMasterSha(ctx.SetStatus);
                var urls = Cfg.RepoZipTemplates.Select(t => string.Format(t, sha)).ToList();
                string zip = Path.Combine(RuntimeDir, "source.zip");
                DownloadWithFallbackAsync("deepseek-harness 源码", urls, zip, true, ctx.SetStatus, DownloadProgress(ctx, "源码")).GetAwaiter().GetResult();
                ctx.SetStatus("[3/4] 解压源码...");
                ctx.SetProgress(null, "解压中...");
                using (var archive = ZipFile.OpenRead(zip))
                {
                    foreach (var entry in archive.Entries)
                    {
                        string full = entry.FullName.Replace('/', '\\');
                        int cut = full.IndexOf('\\');
                        if (cut < 0 || full.Length <= cut + 1) continue;
                        string target = SafeCombine(DshHome, full.Substring(cut + 1));
                        if (entry.FullName.EndsWith("/")) Directory.CreateDirectory(target);
                        else
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(target));
                            entry.ExtractToFile(target, true);
                        }
                    }
                }
                File.Delete(zip);
                File.WriteAllText(ShaFile, sha);
            }
            if (string.IsNullOrEmpty(sha) || sha.Length < 7) sha = Cfg.PinnedSha;
            // 4/4 deps + build (shim must exist before build.ts spawns "pnpm")
            EnsurePnpmShim();
            RunResult install = null;
            if (!InstallDone())
            {
                // --store-dir keeps pnpm's package store inside .launcher;
                // otherwise it leaks to <drive>:\.pnpm-store and survives
                // uninstall (verified: pnpm v11 ignores npm_config_store_dir).
                string store = Path.Combine(RuntimeDir, "pnpm-store");
                string common = "--store-dir \"" + store + "\""
                    + " --fetch-timeout " + Cfg.FetchTimeoutMs
                    + " --fetch-retries " + Cfg.FetchRetries
                    + " --network-concurrency " + Cfg.NetworkConcurrency;
                var registries = OrderRegistries();

                string usedRegistry = null;
                for (int i = 0; i < registries.Count; i++)
                {
                    usedRegistry = registries[i];
                    ctx.SetStatus("[4/4] 安装依赖（源 " + (i + 1) + "/" + registries.Count + "）...");
                    ctx.SetProgress(null, "pnpm install 运行中…");
                    install = RunPnpm("install " + common + " --registry " + usedRegistry, DshHome, null, Cfg.InstallTimeoutMs, usedRegistry);
                    if (install.ExitCode == 0) break;
                    if (IsNetworkFailure(install) && i + 1 < registries.Count)
                    {
                        Log("依赖安装失败(" + DescribeFailKind(install.Kind) + ")，自动切换镜像重试 <- " + usedRegistry);
                        continue;
                    }
                    break;
                }
                if (install.ExitCode != 0)
                {
                    error = DescribeRunFailure("依赖安装", install, Cfg.InstallTimeoutMs)
                        + "（源：" + usedRegistry + "）\r\n" + SuggestFor(install.Kind)
                        + "\r\n\r\n" + install.Tail;
                    return false;
                }
                File.WriteAllText(InstallStampFile, InstallFingerprint());
            }
            if (!MatchesStamp())
            {
                ctx.SetStatus("[4/4] 构建项目（约 1-2 分钟，请勿关闭）...");
                ctx.SetProgress(null, "pnpm run build 运行中...");
                var build = RunPnpm("run build", DshHome, sha, Cfg.BuildTimeoutMs, null);
                if (build.ExitCode != 0)
                {
                    error = DescribeRunFailure("项目构建", build, Cfg.BuildTimeoutMs)
                        + "\r\n" + SuggestFor(build.Kind)
                        + "\r\n\r\n" + build.Tail;
                    return false;
                }
                File.WriteAllText(StampFile, ContentFingerprint());

                // Ordering repair: a package's "bin" often points at lib/bin.js,
                // which only exists AFTER this build. pnpm links bins during
                // `install` (before build), so on a fresh install that shim is
                // missing. Re-run an OFFLINE install now that the target exists
                // to (re)create it. Non-fatal: the launcher invokes lib/bin.js
                // directly, so this only repairs the `dsh` command shims the
                // running app may spawn.
                if (install == null || install.BinLinkWarned)
                {
                    ctx.SetStatus("[4/4] 修复命令链接（构建产物 bin）...");
                    ctx.SetProgress(null, "pnpm install --offline 运行中…");
                    var relink = RunPnpm("install --offline --store-dir \"" + Path.Combine(RuntimeDir, "pnpm-store") + "\"",
                        DshHome, null, Cfg.InstallTimeoutMs, null);
                    if (relink.ExitCode == 0)
                        Log("构建后已重链 bin（修复 install 阶段因构建产物未就绪而缺失的 dsh 等命令）");
                    else
                        Log("构建后重链 bin 未成功（不影响启动器自身，启动器直接调用 lib/bin.js）: " + relink.Tail);
                }
            }
            // Final gate before reporting success: the server entry must exist,
            // otherwise the launcher would only fail later at server start.
            if (!File.Exists(Path.Combine(DshHome, @"apps\cli\lib\bin.js")))
            {
                error = "构建未生成服务入口 apps\\cli\\lib\\bin.js，请重试";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log("Bootstrap 异常: " + ex);
            error = ex.Message;
            return false;
        }
    }

    // ---------- server ----------

    internal static bool IsServerUp()
    {
        try
        {
            using (var client = new TcpClient())
            {
                var ar = client.BeginConnect("127.0.0.1", 3080, null, null);
                bool ok = ar.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(800));
                if (!ok) return false;
                client.EndConnect(ar);
                return true;
            }
        }
        catch { return false; }
    }

    // ---------- UI ----------

    // Palette shared with the uninstaller (dafeiyu/xiezai blue theme).
    private static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(219, 234, 254);
        public static readonly Color Card = Color.White;
        public static readonly Color CardBorder = Color.FromArgb(206, 219, 243);
        public static readonly Color Text = Color.FromArgb(37, 47, 90);
        public static readonly Color Gray = Color.FromArgb(116, 126, 158);
        public static readonly Color Accent = Color.FromArgb(63, 96, 178);
        public static readonly Color AccentHover = Color.FromArgb(50, 80, 156);
        public static readonly Color Danger = Color.FromArgb(214, 69, 65);

        // Pixel-unit fonts: they scale together with the pixel layout when
        // the form is DPI-scaled in OnLoad, keeping everything proportional.
        public static Font Title { get { return new Font("Microsoft YaHei UI", 30F, FontStyle.Bold, GraphicsUnit.Pixel); } }
        public static Font H1 { get { return new Font("Microsoft YaHei UI", 24F, FontStyle.Bold, GraphicsUnit.Pixel); } }
        public static Font Body { get { return new Font("Microsoft YaHei UI", 20F, FontStyle.Regular, GraphicsUnit.Pixel); } }
        public static Font Small { get { return new Font("Microsoft YaHei UI", 16F, FontStyle.Regular, GraphicsUnit.Pixel); } }
        public static Font Tiny { get { return new Font("Microsoft YaHei UI", 14F, FontStyle.Regular, GraphicsUnit.Pixel); } }

        public static Image LoadImage(string resourceName)
        {
            try
            {
                using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                {
                    if (st == null) return null;
                    using (var img = Image.FromStream(st))
                        return new Bitmap(img); // copy so the stream can close
                }
            }
            catch { return null; }
        }

        // High-quality downscale; render at PHYSICAL pixels so the later
        // DPI form-scaling shows the image 1:1 instead of stretching it.
        public static Image ScaleTo(Image src, int size)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(src, 0, 0, size, size);
            }
            return bmp;
        }

        // Color fade helper: labels cannot do real alpha, so "fading" text
        // means lerping its color from the background to the target color.
        public static Color Lerp(Color from, Color to, float t)
        {
            if (t < 0F) t = 0F;
            if (t > 1F) t = 1F;
            return Color.FromArgb(
                (int)(from.R + (to.R - from.R) * t),
                (int)(from.G + (to.G - from.G) * t),
                (int)(from.B + (to.B - from.B) * t));
        }

        public static float EaseOutCubic(float t)
        {
            if (t < 0F) t = 0F;
            if (t > 1F) t = 1F;
            float u = 1F - t;
            return 1F - u * u * u;
        }

        public static float EaseInCubic(float t)
        {
            if (t < 0F) t = 0F;
            if (t > 1F) t = 1F;
            return t * t * t;
        }
    }

    // The ENTIRE loading screen (mascot + glow + stage text + progress bar
    // + detail text) is owner-drawn by this ONE double-buffered panel. The
    // previous design stacked separate child controls (transparent Labels +
    // an owner-drawn bar) under a layered (Opacity-animated) startup window;
    // on some machines those children rendered as solid black boxes for the
    // whole loading phase — the classic "transparent / unpainted child under
    // a layered window" WinForms artifact. With zero child controls a frame
    // is either drawn complete or the solid background simply shows again:
    // black boxes are structurally impossible.
    //
    // The marquee position and progress fill advance on a thread-pool timer,
    // independent of the UI thread; paints simply coalesce — at most ONE
    // invalidate is ever queued, so a flooded message queue never starves the
    // WinForms (WM_TIMER) transition timers. The breathing phase is NOT
    // accumulated on that timer: it is derived from a monotonic Stopwatch at
    // paint time, so the mascot motion stays smooth and drift-free even when
    // the UI thread is briefly busy (process spawns, WebView2 startup).
    private sealed class BoardPanel : Panel
    {
        private static readonly Color Track = Color.FromArgb(0xBE, 0xD7, 0xF8);
        private static readonly Color FillA = Color.FromArgb(0x6E, 0x9F, 0xF5);
        private static readonly Color FillB = Color.FromArgb(0x4D, 0x86, 0xF0);

        private Image src;
        private Image glow;
        private Image glowScaled;
        private int glowScaledSize = -1;
        private readonly Dictionary<int, Image> fishCache = new Dictionary<int, Image>();

        private readonly System.Threading.Timer timer;
        private int invalidatePending;
        // Breathing phase is derived from a monotonic clock at paint time
        // (not accumulated on the timer thread), so the mascot's motion stays
        // smooth and drift-free even when the UI thread coalesces repaints.
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private const double BreathRadPerSec = 0.8125; // = 0.013 rad per 16 ms tick
        private double marqueePos = -0.4;
        private int value;
        private float shown;

        private float dpiScale = 1F;
        private Font stageFont;
        private Font detailFont;
        private string stage = "";
        private string detail = "";

        // 0 = hidden (small, sunken, transparent), 1 = fully entered.
        // Driven by the entrance animation; stays 1 afterwards.
        public float Entrance = 1F;
        // 0 = normal, 1 = fully dissolved (fades out while gently growing,
        // like the mascot "swimming into" the app). Driven by the exit
        // animation right before the web UI takes over.
        public float Exit = 0F;
        // 0 = text and bar fully blended into the background, 1 = normal.
        // Driven by the entrance (fade-in) and exit (sink-out) animations.
        public float TextAlpha = 1F;
        // Stage-line crossfade used when the stage message changes.
        public float StageAlpha = 1F;
        public bool Marquee { get; private set; }

        public BoardPanel()
        {
            DoubleBuffered = true;
            BackColor = Theme.Bg;
            timer = new System.Threading.Timer(_ =>
            {
                // 16 ms tick: advance the marquee / progress fill and queue
                // at most one repaint. The breathing phase itself is derived
                // from `clock` in OnPaint, so it never skips or drifts.
                if (Marquee)
                {
                    marqueePos += 0.011;
                    if (marqueePos > 1.05) marqueePos = -0.45;
                }
                else if (shown < value)
                {
                    // ease the fill towards the latest reported progress so
                    // stage jumps read as motion instead of a hard cut
                    shown += Math.Max(0.4F, (value - shown) * 0.10F);
                    if (shown > value) shown = value;
                }
                try
                {
                    if (IsHandleCreated && !IsDisposed
                        && System.Threading.Interlocked.Exchange(ref invalidatePending, 1) == 0)
                    {
                        BeginInvoke(new Action(() =>
                        {
                            invalidatePending = 0;
                            if (!IsDisposed) Invalidate();
                        }));
                    }
                }
                catch { invalidatePending = 0; }
            }, null, -1, -1);
        }

        public void SetImages(Image mascot, Image glowImage)
        {
            src = mascot;
            glow = glowImage;
        }

        // Called once the window's own DPI is known (and on every layout
        // pass): pixel fonts are rebuilt at physical size so text stays
        // proportional to the scaled layout.
        public void SetMetrics(float dpi)
        {
            if (dpi == dpiScale && stageFont != null) return;
            dpiScale = dpi;
            SetFonts(ScaleFont(Theme.H1, dpi), ScaleFont(Theme.Small, dpi));
        }

        private void SetFonts(Font stageF, Font detailF)
        {
            if (stageFont != null) stageFont.Dispose();
            if (detailFont != null) detailFont.Dispose();
            stageFont = stageF;
            detailFont = detailF;
        }

        private static Font ScaleFont(Font f, float dpi)
        {
            Font scaled = new Font(f.FontFamily, f.Size * dpi, f.Style, GraphicsUnit.Pixel);
            f.Dispose();
            return scaled;
        }

        public string StageText
        {
            get { return stage; }
            set { string v = value ?? ""; if (stage != v) { stage = v; Invalidate(); } }
        }

        public string DetailText
        {
            get { return detail; }
            set { string v = value ?? ""; if (detail != v) { detail = v; Invalidate(); } }
        }

        public void StartAnim() { timer.Change(0, 16); }
        public void StopAnim() { timer.Change(-1, -1); }
        public void StartMarquee() { Marquee = true; Invalidate(); }
        public void StopMarquee() { Marquee = false; Invalidate(); }

        public void SetValue(int v)
        {
            Marquee = false;
            value = Math.Max(0, Math.Min(100, v));
            if (value == 0) shown = 0F; // resets snap, growth animates
            Invalidate();
        }

        private int S(int v) { return (int)(v * dpiScale); }

        private Image FishAt(int size)
        {
            Image hit;
            if (fishCache.TryGetValue(size, out hit)) return hit;
            if (fishCache.Count > 64)
            {
                foreach (var im in fishCache.Values) im.Dispose();
                fishCache.Clear();
            }
            var bmp = Theme.ScaleTo(src, size);
            fishCache[size] = bmp;
            return bmp;
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); // erases the whole client area with solid Bg
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;

            int cw = Width;
            int ch = Height;
            if (cw <= 0 || ch <= 0) return;

            // --- layout: fish + stage + progress + detail as one vertically
            // centered block (same rules the old ApplyLayout() applied to
            // the separate controls). The fish has a hard MAX and a MIN; it
            // shrinks only when the window is too small to fit the block.
            int stageH = S(36);
            int barH = S(16);
            int detailH = S(28);
            int textH = S(20) + stageH + S(26) + barH + S(16) + detailH;
            int pulseSize = Math.Min(S(560), Math.Min(ch - textH - S(48), cw - S(48)));
            if (pulseSize < S(200)) pulseSize = S(200); // floor matches the progress bar
            int block = pulseSize + textH;
            int top = Math.Max(S(24), (ch - block) / 2);
            int stageTop = top + pulseSize + S(20);
            int barTop = stageTop + stageH + S(26);
            int detailTop = barTop + barH + S(16);
            int barW = Math.Max(S(200), Math.Min(S(640), cw - S(48)));
            int barL = (cw - barW) / 2;

            // --- mascot: constant-brightness pulse; the glow is a
            // pre-rendered Gaussian blur whose alpha reaches zero at the
            // canvas edge, so it blends without any hard rim. Resampling is
            // cached — the per-frame paint is a plain 1:1 blit.
            double breathT = clock.Elapsed.TotalSeconds;
            double phase = (breathT * BreathRadPerSec) % (2.0 * Math.PI);
            double wave = (Math.Sin(phase) + 1.0) / 2.0;   // 0..1
            float enter = Theme.EaseOutCubic(Entrance);
            float exitE = Theme.EaseInCubic(Exit);
            float vis = enter * (1F - exitE);
            if (src != null && vis > 0.001F)
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                int box = pulseSize;
                if (glow != null)
                {
                    if (glowScaledSize != box)
                    {
                        if (glowScaled != null) glowScaled.Dispose();
                        glowScaled = Theme.ScaleTo(glow, box);
                        glowScaledSize = box;
                    }
                    float glowAlpha = (0.15F + 0.75F * (float)wave) * vis;
                    var cm = new System.Drawing.Imaging.ColorMatrix();
                    cm.Matrix33 = glowAlpha;
                    using (var ia = new System.Drawing.Imaging.ImageAttributes())
                    {
                        ia.SetColorMatrix(cm);
                        g.DrawImage(glowScaled, (cw - box) / 2, top, box, box);
                    }
                }

                // fish breathes very gently (±1.5 %) around 52 % of its box;
                // the entrance adds a scale-up from 85 % and a slight upward
                // drift; the exit grows it a touch (+10 %) while dissolving
                float scale = 0.52F * (0.985F + 0.03F * (float)wave)
                    * (0.85F + 0.15F * enter) * (1F + 0.10F * exitE);
                int size = Math.Max(8, (int)(box * scale));
                int x = (cw - size) / 2;
                int y = top + (box - size) / 2 + (int)((1F - enter) * box * 0.05F);
                var cm2 = new System.Drawing.Imaging.ColorMatrix();
                cm2.Matrix33 = vis;
                using (var ia2 = new System.Drawing.Imaging.ImageAttributes())
                {
                    ia2.SetColorMatrix(cm2);
                    var fish = FishAt(size);
                    g.DrawImage(fish, new Rectangle(x, y, size, size),
                        0, 0, fish.Width, fish.Height, GraphicsUnit.Pixel, ia2);
                }
            }

            float ta = TextAlpha;
            if (ta <= 0.001F) return;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            if (stageFont == null)
            {
                stageFont = Theme.H1;
                detailFont = Theme.Small;
            }

            // --- stage + detail text (GDI text has no alpha channel, so
            // "fading" means lerping the color from the background)
            using (var fmt = new StringFormat())
            {
                fmt.Alignment = StringAlignment.Center;
                fmt.LineAlignment = StringAlignment.Center;
                if (stage.Length > 0)
                {
                    using (var b = new SolidBrush(Theme.Lerp(Theme.Bg, Theme.Text, ta * StageAlpha)))
                        g.DrawString(stage, stageFont, b, new RectangleF(0, stageTop, cw, stageH), fmt);
                }
                if (detail.Length > 0)
                {
                    using (var b = new SolidBrush(Theme.Lerp(Theme.Bg, Theme.Gray, ta)))
                        g.DrawString(detail, detailFont, b, new RectangleF(0, detailTop, cw, detailH), fmt);
                }
            }

            // --- light-blue rounded progress bar (DeepSeek style): soft
            // track + accent fill, with a sliding marquee segment for
            // indeterminate stages
            var track = new Rectangle(barL, barTop, barW - 1, barH - 1);
            Color trackColor = Theme.Lerp(Theme.Bg, Track, ta);
            Color fillA = Theme.Lerp(Theme.Bg, FillA, ta);
            Color fillB = Theme.Lerp(Theme.Bg, FillB, ta);
            using (var path = RoundRect(track, barH / 2))
            using (var trackBrush = new SolidBrush(trackColor))
            {
                g.FillPath(trackBrush, path);
                g.SetClip(path);
                if (Marquee)
                {
                    int segW = (int)(track.Width * 0.38);
                    int sx = barL + (int)(marqueePos * track.Width);
                    var seg = new Rectangle(sx, barTop, segW, track.Height + 1);
                    using (var b = new System.Drawing.Drawing2D.LinearGradientBrush(
                        seg, fillA, fillB, 0F))
                    {
                        g.FillRectangle(b, seg);
                    }
                }
                else if (shown > 0.5F)
                {
                    int fw = Math.Max(barH, (int)(track.Width * shown / 100.0));
                    var fr = new Rectangle(barL, barTop, fw, track.Height + 1);
                    using (var b = new System.Drawing.Drawing2D.LinearGradientBrush(
                        fr, fillA, fillB, 0F))
                    {
                        g.FillRectangle(b, fr);
                    }
                }
                g.ResetClip();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { timer.Dispose(); } catch { }
                foreach (var im in fishCache.Values) im.Dispose();
                fishCache.Clear();
                if (glowScaled != null) { glowScaled.Dispose(); glowScaled = null; }
                if (stageFont != null) { stageFont.Dispose(); stageFont = null; }
                if (detailFont != null) { detailFont.Dispose(); detailFont = null; }
            }
            base.Dispose(disposing);
        }
    }

    // Borderless snapshot window used for the final hand-off cross-fade.
    // WebView2 is a native child window and punches through any in-form
    // overlay (airspace restriction), so the fade-out of the loading
    // screen's background board lives in this separate layered window.
    private sealed class OverlayForm : Form
    {
        private readonly Bitmap shot;

        protected override bool ShowWithoutActivation { get { return true; } }

        public OverlayForm(Bitmap shot)
        {
            this.shot = shot;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            BackColor = Theme.Bg;
            BackgroundImage = shot;
            BackgroundImageLayout = ImageLayout.None;
            DoubleBuffered = true;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && shot != null) shot.Dispose();
        }
    }

    // A timer whose ticks are marshaled onto the UI thread as POSTED
    // messages (BeginInvoke) instead of WM_TIMER. GetMessage serves
    // WM_TIMER dead last — below even WM_PAINT — so a continuously
    // self-repainting animation starves every WinForms timer (the exit
    // animation once stalled 31 s because of this). Posted messages queue
    // fairly (FIFO), keeping transitions responsive no matter how fast
    // the animation repaints.
    private sealed class UiTimer : IDisposable
    {
        private readonly Control host;
        private readonly System.Threading.Timer timer;
        private readonly int interval;
        private volatile bool stopped = true;

        public event EventHandler Tick;

        public UiTimer(Control host, int intervalMs)
        {
            this.host = host;
            interval = intervalMs;
            timer = new System.Threading.Timer(_ =>
            {
                if (stopped) return;
                try
                {
                    if (host.IsHandleCreated && !host.IsDisposed)
                    {
                        host.BeginInvoke(new Action(() =>
                        {
                            // a tick handler throwing on the UI thread would
                            // otherwise take the whole launcher down
                            try
                            {
                                var t = Tick;
                                if (!stopped && t != null && !host.IsDisposed) t(this, EventArgs.Empty);
                            }
                            catch { }
                        }));
                    }
                }
                catch { }
            }, null, -1, -1);
        }

        public void Start() { stopped = false; timer.Change(interval, interval); }
        public void Stop() { stopped = true; try { timer.Change(-1, -1); } catch { } }
        public void Dispose() { Stop(); timer.Dispose(); }
    }

    private sealed class MainForm : Form
    {
        private Microsoft.Web.WebView2.WinForms.WebView2 webView;

        // Three boot screens (at most one visible at a time) + the web view.
        // The loading screen panel owner-draws its whole content (mascot,
        // stage text, progress bar, detail text) — no child controls, so
        // nothing on it can render as a black unpainted box.
        private readonly Panel confirmPanel = new Panel();
        private readonly BoardPanel loadPanel = new BoardPanel();
        private readonly Panel errorPanel = new Panel();

        private readonly Panel listHost = new Panel();
        private readonly Label errorTitle = new Label();
        private readonly TextBox errorBox = new TextBox();
        private Button retryButton;
        // references kept for the dynamic re-centering pass
        private PictureBox confirmMascot;
        private Label confirmTitle;
        private Label confirmSub;
        private Label confirmHint;
        private Button confirmStart;
        private Button confirmExit;
        private PictureBox errorMascot;
        private Label errorHint;
        private Button errorExit;
        private bool layoutReady;
        private readonly UiTimer poll;
        private UiTimer fadeTimer;
        private UiTimer entranceTimer;
        private UiTimer exitTimer;
        private Task webViewReady;
        private bool webViewFailed;
        private bool exiting;
        private bool navigateStarted;
        private bool pageReady;
        private int pageReadyTick;
        private UiTimer stageFadeTimer;
        private string stagePending;
        private float stageFade = 1F;
        private bool stageFadingOut;
        private Action pendingFadeAction;
        private bool fadingOut;
        private float dpiF = 1F;
        private int loadShownTick;
        private Process serverProcess;
        private StreamWriter serverLog;
        private int waitedSeconds;
        private bool navigated;
        private bool serverCheckBusy;
        private bool busy;

        public MainForm()
        {
            Text = "DeepSeek Harness";
            Width = 1440;
            Height = 900;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            Font = Theme.Small;
            // Deliberately NO layered (Opacity-animated) startup: under a
            // layered window WinForms child controls can render as solid
            // black boxes until they repaint. The boot screens are solid-
            // color panels whose content is either owner-drawn (loading
            // board) or opaque with a matching BackColor, so the window can
            // simply show — the content itself plays the entrance animation.
            try
            {
                using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream("dafeiyu.ico"))
                {
                    if (st != null) Icon = new Icon(st);
                }
            }
            catch { }

            // dpiF is finalized in OnLoad via GetDpiForWindow (the window
            // DPI is authoritative); images are pre-rendered at 2x so they
            // stay crisp whether the form ends up scaled or not.
            var dafeiyu = Theme.LoadImage("dafeiyu.png");
            var errorImg = Theme.LoadImage("error.png");
            var glowImg = Theme.LoadImage("dafeiyu-glow.png");

            BuildConfirmPanel(dafeiyu);
            BuildLoadPanel(dafeiyu, glowImg);
            BuildErrorPanel(errorImg);

            // The load screen IS the first thing shown (no blank window,
            // no hard cut): its content plays a short entrance animation
            // (fish scales up + fades in, text and bar follow), then the
            // screen stays up for a minimum moment even when the server
            // starts instantly.
            confirmPanel.Visible = false;
            loadPanel.Visible = true;
            errorPanel.Visible = false;
            Controls.Add(confirmPanel);
            Controls.Add(loadPanel);
            Controls.Add(errorPanel);

            poll = new UiTimer(this, 1000);
            poll.Tick += OnPoll;

            layoutReady = true;
        }

        private Label CenterLabel(string text, Font font, Color color, int top, int height)
        {
            return new Label
            {
                Text = text,
                Font = font,
                ForeColor = color,
                Left = 0,
                Top = top,
                Width = ClientSize.Width,
                Height = height,
                TextAlign = ContentAlignment.MiddleCenter,
                // opaque, matching the solid panel behind: transparent
                // labels can render as black boxes while the window is
                // layered (dip-fade) on some machines
                BackColor = Theme.Bg,
            };
        }

        private Button MakeButton(string text, int x, int y, int w, Color bg, Color fg, bool accent)
        {
            var b = new Button
            {
                Text = text,
                Left = x,
                Top = y,
                Width = w,
                Height = 52,
                BackColor = bg,
                ForeColor = fg,
                FlatStyle = FlatStyle.Flat,
                Font = Theme.Body,
            };
            if (accent)
            {
                b.FlatAppearance.BorderSize = 0;
                b.MouseEnter += (s, e) => b.BackColor = Theme.AccentHover;
                b.MouseLeave += (s, e) => b.BackColor = bg;
            }
            else
            {
                b.FlatAppearance.BorderColor = Theme.CardBorder;
            }
            return b;
        }

        // Screen 1: first-run confirmation, listing exactly what is missing
        // and the URLs it will be downloaded from, one card per component.
        // Positions here are only the initial guess; ApplyLayout() re-centers
        // everything against the live ClientSize (called after DPI scaling
        // and on every window resize).
        private void BuildConfirmPanel(Image mascot)
        {
            int cw = ClientSize.Width;
            confirmPanel.Dock = DockStyle.Fill;
            confirmPanel.BackColor = Theme.Bg;

            if (mascot != null)
            {
                confirmMascot = new PictureBox
                {
                    Image = Theme.ScaleTo(mascot, 300),
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Left = (cw - 150) / 2,
                    Top = 20,
                    Width = 150,
                    Height = 150,
                    BackColor = Theme.Bg,
                };
                confirmPanel.Controls.Add(confirmMascot);
            }
            confirmTitle = CenterLabel("首次运行需要下载组件", Theme.Title, Theme.Text, 180, 44);
            confirmPanel.Controls.Add(confirmTitle);
            confirmSub = CenterLabel("检测到以下组件缺失，将从以下地址下载：", Theme.Small, Theme.Gray, 230, 26);
            confirmPanel.Controls.Add(confirmSub);

            listHost.Left = (cw - 820) / 2;
            listHost.Top = 268;
            listHost.Width = 820;
            listHost.Height = 320;
            listHost.AutoScroll = true;
            listHost.BackColor = Theme.Bg;
            confirmPanel.Controls.Add(listHost);

            confirmHint = CenterLabel(
                "所有文件均保存在本程序目录内，可通过同目录下的“卸载.exe”完整移除。",
                Theme.Tiny, Theme.Gray, 604, 24);
            confirmPanel.Controls.Add(confirmHint);

            int groupX = (cw - 240 - 24 - 140) / 2;
            confirmStart = MakeButton("开始下载并安装", groupX, 672, 240, Theme.Accent, Color.White, true);
            confirmStart.Click += (s, e) =>
            {
                ShowLoad();
                loadPanel.StageText = "正在初始化…";
                loadPanel.DetailText = "";
                loadPanel.StartMarquee();
                StartBootstrap();
            };
            confirmExit = MakeButton("退出", groupX + 264, 672, 140, Theme.Card, Theme.Text, false);
            confirmExit.Click += (s, e) => Close();
            confirmPanel.Controls.Add(confirmStart);
            confirmPanel.Controls.Add(confirmExit);
        }

        // One component row on the confirm screen: bold name, then the
        // download URL(s) it comes from. Cards are created at runtime in
        // OnShown — AFTER OnLoad's DPI Scale() — so every size and font
        // must be multiplied by dpiF by hand (logical px * dpiF).
        private int S(int v) { return (int)(v * dpiF); }

        private Font ScaledFont(Font f)
        {
            return new Font(f.FontFamily, f.Size * dpiF, f.Style, GraphicsUnit.Pixel);
        }

        private Panel MakeComponentCard(int y, string name, string[] urls)
        {
            var card = new Panel
            {
                Left = 0,
                Top = y,
                Width = S(796),
                Height = S(40 + 22 * urls.Length),
                BackColor = Theme.Card,
            };
            card.Paint += (s, e) =>
                e.Graphics.DrawRectangle(new Pen(Theme.CardBorder), 0, 0, card.Width - 1, card.Height - 1);
            card.Controls.Add(new Label
            {
                Text = name,
                Font = ScaledFont(Theme.Body),
                ForeColor = Theme.Text,
                Left = S(18),
                Top = S(8),
                Width = S(760),
                Height = S(28),
                BackColor = Theme.Card,
            });
            for (int i = 0; i < urls.Length; i++)
            {
                card.Controls.Add(new Label
                {
                    Text = urls[i],
                    Font = ScaledFont(Theme.Tiny),
                    ForeColor = Theme.Gray,
                    Left = S(18),
                    Top = S(40 + 22 * i),
                    Width = S(760),
                    Height = S(20),
                    BackColor = Theme.Card,
                });
            }
            return card;
        }

        // Screen 2: loading — pulsing dafeiyu + stage + progress bar, all
        // owner-drawn by the load board itself (no child controls).
        private void BuildLoadPanel(Image mascot, Image glowImg)
        {
            loadPanel.Dock = DockStyle.Fill;
            loadPanel.BackColor = Theme.Bg;
            loadPanel.SetImages(mascot, glowImg);
            loadPanel.Entrance = 0F;  // revealed by the entrance animation
            loadPanel.TextAlpha = 0F; // text and bar fade in via the entrance
            loadPanel.StageText = "正在初始化…";
        }

        // Screen 3: failure — crying mascot centered + details + retry/exit.
        private void BuildErrorPanel(Image mascot)
        {
            int cw = ClientSize.Width;
            errorPanel.Dock = DockStyle.Fill;
            errorPanel.BackColor = Theme.Bg;

            if (mascot != null)
            {
                errorMascot = new PictureBox
                {
                    Image = Theme.ScaleTo(mascot, 480),
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Left = (cw - 240) / 2,
                    Top = 96,
                    Width = 240,
                    Height = 240,
                    BackColor = Theme.Bg,
                };
                errorPanel.Controls.Add(errorMascot);
            }

            errorTitle.Font = Theme.H1;
            errorTitle.ForeColor = Theme.Danger;
            errorTitle.Left = 0;
            errorTitle.Top = 352;
            errorTitle.Width = cw;
            errorTitle.Height = 36;
            errorTitle.TextAlign = ContentAlignment.MiddleCenter;
            errorTitle.Text = "初始化失败";
            errorTitle.BackColor = Theme.Bg;
            errorPanel.Controls.Add(errorTitle);

            errorBox.Left = (cw - 780) / 2;
            errorBox.Top = 406;
            errorBox.Width = 780;
            errorBox.Height = 200;
            errorBox.Multiline = true;
            errorBox.ReadOnly = true;
            errorBox.ScrollBars = ScrollBars.Vertical;
            errorBox.Font = Theme.Small;
            errorBox.BackColor = Theme.Card;
            errorBox.ForeColor = Theme.Text;
            errorBox.BorderStyle = BorderStyle.FixedSingle;
            errorPanel.Controls.Add(errorBox);

            errorHint = CenterLabel("可检查网络连接后重试；详细日志见 .launcher\\build.log",
                Theme.Tiny, Theme.Gray, 620, 24);
            errorPanel.Controls.Add(errorHint);

            int groupX = (cw - 180 - 24 - 140) / 2;
            retryButton = MakeButton("重试", groupX, 668, 180, Theme.Accent, Color.White, true);
            retryButton.Click += (s, e) =>
            {
                ShowLoad();
                loadPanel.StageText = "正在初始化…";
                loadPanel.DetailText = "";
                loadPanel.StartMarquee();
                // Re-probe before doing any heavy work: when the failure was
                // "another instance owns 3080", downloading everything first
                // and only then failing at server start would waste minutes.
                Task.Run(() =>
                {
                    bool up = IsServerUp();
                    Ui(() =>
                    {
                        if (IsDisposed) return;
                        if (up && NeedsInstall())
                        {
                            ShowError("检测到另一个实例正在运行",
                                "3080 端口已被另一个 DeepSeek Harness 实例占用（可能是从其他文件夹启动的）。\r\n请先关闭那个实例，再点“重试”继续首次下载安装。",
                                true);
                            return;
                        }
                        StartBootstrap();
                    });
                });
            };
            errorExit = MakeButton("退出", groupX + 204, 668, 140, Theme.Card, Theme.Text, false);
            errorExit.Click += (s, e) => Close();
            errorPanel.Controls.Add(retryButton);
            errorPanel.Controls.Add(errorExit);
        }

        // ---------- dynamic centering ----------

        // Re-center every boot screen against the CURRENT client size, so
        // any screen ratio / DPI combination and any window resize keeps a
        // balanced layout. Control sizes are already DPI-scaled by this
        // point; gaps are scaled from logical units via S().
        private void ApplyLayout()
        {
            if (!layoutReady) return;
            int cw = ClientSize.Width;
            int ch = ClientSize.Height;
            if (cw <= 0 || ch <= 0) return;

            // --- loading screen: the board lays out its own content
            // (fish + stage + progress + detail as one centered block);
            // just hand it the finalized DPI scale so pixel fonts are
            // rebuilt at physical size
            loadPanel.SetMetrics(dpiF);

            // --- confirm screen: mascot, title, sub, card list (flexible
            // height), hint, button row — centered as a block. On short
            // windows the mascot hides first, then the list shrinks.
            bool showMascot = confirmMascot != null && ch >= S(640);
            if (confirmMascot != null) confirmMascot.Visible = showMascot;
            int fixedH = (showMascot ? confirmMascot.Height + S(10) : 0)
                + confirmTitle.Height + S(6) + confirmSub.Height + S(12)
                + S(16) + confirmHint.Height + S(24) + confirmStart.Height;
            int listH = Math.Max(S(110), Math.Min(S(340), ch - fixedH - S(72)));
            int blockC = fixedH + listH;
            int topC = Math.Max(S(12), (ch - blockC) / 2);
            if (showMascot)
            {
                confirmMascot.Left = (cw - confirmMascot.Width) / 2;
                confirmMascot.Top = topC;
                topC = confirmMascot.Bottom + S(10);
            }
            confirmTitle.Left = 0;
            confirmTitle.Width = cw;
            confirmTitle.Top = topC;
            confirmSub.Left = 0;
            confirmSub.Width = cw;
            confirmSub.Top = confirmTitle.Bottom + S(6);
            listHost.Left = (cw - listHost.Width) / 2;
            listHost.Top = confirmSub.Bottom + S(12);
            listHost.Height = listH;
            confirmHint.Left = 0;
            confirmHint.Width = cw;
            confirmHint.Top = listHost.Bottom + S(16);
            int groupW = confirmStart.Width + S(24) + confirmExit.Width;
            confirmStart.Left = (cw - groupW) / 2;
            confirmStart.Top = confirmHint.Bottom + S(24);
            confirmExit.Left = confirmStart.Right + S(24);
            confirmExit.Top = confirmStart.Top;

            // --- error screen: mascot, title, detail box (flexible height),
            // hint, buttons
            int fixedE = (errorMascot != null ? errorMascot.Height + S(16) : 0)
                + errorTitle.Height + S(14) + S(10)
                + errorHint.Height + S(26) + retryButton.Height;
            errorBox.Height = Math.Max(S(90), Math.Min(S(200), ch - fixedE - S(48)));
            int blockE = fixedE + errorBox.Height;
            int topE = Math.Max(S(20), (ch - blockE) / 2);
            if (errorMascot != null)
            {
                errorMascot.Left = (cw - errorMascot.Width) / 2;
                errorMascot.Top = topE;
                topE = errorMascot.Bottom + S(16);
            }
            errorTitle.Left = 0;
            errorTitle.Width = cw;
            errorTitle.Top = topE;
            errorBox.Left = (cw - errorBox.Width) / 2;
            errorBox.Top = errorTitle.Bottom + S(14);
            errorHint.Left = 0;
            errorHint.Width = cw;
            errorHint.Top = errorBox.Bottom + S(10);
            int groupE = retryButton.Width + S(24) + errorExit.Width;
            retryButton.Left = (cw - groupE) / 2;
            retryButton.Top = errorHint.Bottom + S(26);
            errorExit.Left = retryButton.Right + S(24);
            errorExit.Top = retryButton.Top;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ApplyLayout();
        }

        // ---------- screen switching ----------

        // Gentle dip-fade between boot screens to avoid a hard visual cut.
        // The form dips to 40 % opacity, swaps the visible panel at the
        // bottom of the dip, then fades back in. WebView2 must never be
        // visible while Opacity < 1 (airspace), so the web-view switch is
        // done instantly and forces Opacity back to 1 first.
        private void FadeTo(Action switchAction)
        {
            if (entranceTimer != null)
            {
                entranceTimer.Stop();
                entranceTimer.Dispose();
                entranceTimer = null;
            }
            pendingFadeAction = switchAction;
            if (fadeTimer == null)
            {
                fadeTimer = new UiTimer(this, 16);
                fadeTimer.Tick += OnFadeTick;
            }
            fadingOut = true;
            fadeTimer.Start();
        }

        private void OnFadeTick(object sender, EventArgs e)
        {
            if (fadingOut)
            {
                if (Opacity > 0.4)
                {
                    Opacity = Math.Max(0.4, Opacity - 0.12);
                    return;
                }
                fadingOut = false;
                var action = pendingFadeAction;
                pendingFadeAction = null;
                if (action != null) action();
            }
            Opacity = Math.Min(1.0, Opacity + 0.12);
            if (Opacity >= 1.0) fadeTimer.Stop();
        }

        private void ShowConfirm()
        {
            FadeTo(() =>
            {
                loadPanel.StopAnim();
                loadPanel.Visible = false;
                errorPanel.Visible = false;
                confirmPanel.Visible = true;
            });
        }

        private void ShowLoad()
        {
            FadeTo(() =>
            {
                confirmPanel.Visible = false;
                errorPanel.Visible = false;
                loadPanel.Visible = true;
                loadShownTick = Environment.TickCount;
                // re-entering from another screen skips the entrance
                // animation — everything appears in its final state
                loadPanel.Entrance = 1F;
                loadPanel.Exit = 0F;
                if (stageFadeTimer != null) stageFadeTimer.Stop();
                stageFade = 1F;
                loadPanel.StageAlpha = 1F;
                loadPanel.TextAlpha = 1F;
                loadPanel.StartAnim();
                loadPanel.StartMarquee();
            });
        }

        private void ShowError(string title, string detail, bool canRetry)
        {
            FadeTo(() =>
            {
                loadPanel.StopAnim();
                confirmPanel.Visible = false;
                loadPanel.Visible = false;
                errorTitle.Text = title;
                errorBox.Text = detail;
                retryButton.Visible = canRetry;
                errorPanel.Visible = true;
            });
        }

        private void HideAllBootPanels()
        {
            // WebView2 cannot render under a layered (Opacity < 1) window:
            // cancel any in-flight fade and restore full opacity first.
            if (fadeTimer != null) fadeTimer.Stop();
            if (entranceTimer != null)
            {
                entranceTimer.Stop();
                entranceTimer.Dispose();
                entranceTimer = null;
            }
            if (exitTimer != null)
            {
                exitTimer.Stop();
                exitTimer.Dispose();
                exitTimer = null;
            }
            if (stageFadeTimer != null) stageFadeTimer.Stop();
            stageFade = 1F;
            pendingFadeAction = null;
            Opacity = 1.0;
            loadPanel.StopAnim();
            loadPanel.StopMarquee();
            confirmPanel.Visible = false;
            loadPanel.Visible = false;
            errorPanel.Visible = false;
        }

        // ---------- install detection ----------

        private static bool NeedsInstall()
        {
            if (!File.Exists(NodeExe)) return true;
            if (!File.Exists(PnpmMjs)) return true;
            if (!SourcePresent()) return true;
            if (!InstallDone()) return true;
            if (!MatchesStamp()) return true;
            return false;
        }

        private void BuildConfirmList()
        {
            listHost.Controls.Clear();
            int y = 0;
            if (!File.Exists(NodeExe))
            {
                listHost.Controls.Add(MakeComponentCard(y, "Node.js 运行时 v24.15.0",
                    Cfg.NodeUrls.Select((u, i) => i == 0 ? u : "镜像：" + u).ToArray()));
                y += S(96);
            }
            if (!File.Exists(PnpmMjs))
            {
                var pnpmList = new List<string>();
                if (Cfg.PnpmZipUrls.Count > 0) pnpmList.Add(Cfg.PnpmZipUrls[0]);
                foreach (string u in Cfg.PnpmTgzUrls) pnpmList.Add("镜像：" + u);
                listHost.Controls.Add(MakeComponentCard(y, "pnpm 包管理器 v11.7.0", pnpmList.ToArray()));
                y += S(96);
            }
            if (!SourcePresent())
            {
                listHost.Controls.Add(MakeComponentCard(y, "deepseek-harness 源码（GitHub master 分支）", new[]
                {
                    "https://github.com/deepseek-ai/deepseek-harness",
                    "镜像：https://ghfast.top/ 、https://gh-proxy.com/",
                }));
                y += S(96);
            }
            if (!InstallDone() || !MatchesStamp())
            {
                listHost.Controls.Add(MakeComponentCard(y, "项目依赖（由 pnpm 自动下载安装）", new[]
                {
                    "https://registry.npmmirror.com/",
                    "镜像：https://registry.npmjs.org/",
                }));
                y += S(74);
            }
        }

        // ---------- lifecycle ----------

        private void InitWebView()
        {
            webView = new Microsoft.Web.WebView2.WinForms.WebView2 { Dock = DockStyle.Fill, Visible = false };
            // match the boot screens so the page hand-off has no white flash
            webView.DefaultBackgroundColor = Theme.Bg;
            Controls.Add(webView);
            webView.BringToFront();
        }

        // WebView2 spins up real browser processes, which briefly stalls the
        // UI thread. Deferring it until the entrance animation has finished
        // keeps the intro perfectly smooth; the init then overlaps with the
        // server wait instead.
        private async Task InitWebViewDeferred()
        {
            try
            {
                await Task.Delay(950);
                InitWebView();
                string udf = Path.Combine(RuntimeDir, "webview2-data");
                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, udf, null);
                await webView.EnsureCoreWebView2Async(env);
                webView.CoreWebView2.NavigationCompleted += (s, args) =>
                {
                    if (args.IsSuccess)
                    {
                        pageReady = true;
                        pageReadyTick = Environment.TickCount;
                    }
                    else if (navigateStarted && !navigated)
                    {
                        ShowError("页面加载失败",
                            "工作台页面加载未成功（HTTP 状态码：" + args.HttpStatusCode + "）。\r\n可尝试重新打开本程序。", true);
                    }
                };
            }
            catch (Exception ex)
            {
                webViewFailed = true;
                try { poll.Stop(); } catch { }
                ShowError("WebView2 初始化失败",
                    ex.Message + "\r\n\r\n本程序需要 Microsoft WebView2 运行时（多数 Windows 10/11 已自带）。\r\n可从以下地址获取安装程序：\r\n" + Cfg.WebView2Bootstrapper,
                    false);
            }
        }

        private StageContext MakeStageContext()
        {
            return new StageContext
            {
                SetStatus = msg => Ui(() => SetStageText(msg)),
                SetProgress = (percent, detail) => Ui(() =>
                {
                    if (percent.HasValue)
                    {
                        loadPanel.SetValue(percent.Value);
                    }
                    else if (!loadPanel.Marquee)
                    {
                        loadPanel.StartMarquee();
                    }
                    loadPanel.DetailText = detail;
                }),
            };
        }

        private void Ui(Action action)
        {
            try { if (IsHandleCreated) BeginInvoke(action); } catch { }
        }

        // Stage text changes during download/install used to hard-cut.
        // This fades the current line into the background, swaps it, and
        // fades the new line back in (~240 ms total).
        private void SetStageText(string msg)
        {
            if (loadPanel.StageText == msg) return;
            if (!loadPanel.Visible || exiting)
            {
                loadPanel.StageText = msg;
                return;
            }
            stagePending = msg;
            if (stageFadeTimer == null)
            {
                stageFadeTimer = new UiTimer(this, 16);
                stageFadeTimer.Tick += (s, ev) =>
                {
                    if (stageFadingOut)
                    {
                        stageFade -= 0.16F;
                        if (stageFade <= 0F)
                        {
                            stageFade = 0F;
                            loadPanel.StageText = stagePending;
                            stageFadingOut = false;
                        }
                    }
                    else
                    {
                        stageFade += 0.16F;
                        if (stageFade >= 1F)
                        {
                            stageFade = 1F;
                            stageFadeTimer.Stop();
                        }
                    }
                    loadPanel.StageAlpha = stageFade;
                };
            }
            stageFadingOut = true;
            stageFadeTimer.Start();
        }

        // DPI scaling must run AFTER the window handle exists (Scale in the
        // constructor does not resize the actual OS window). Use the WINDOW's
        // own DPI — GetDpiForSystem() reports the logon-time primary display
        // (96 here) while this monitor actually runs at 144. Pixel fonts
        // scale along, keeping text and layout proportional.
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try
            {
                float f = GetDpiForWindow(Handle) / 96F;
                if (f < 1F) f = 1F;
                dpiF = f;
                if (f > 1.05F)
                {
                    Scale(new SizeF(f, f));
                    // Scale grows the window right-down from its CenterScreen
                    // spot — pull it back to center before the fade-in makes
                    // anything visible.
                    var wa = Screen.FromHandle(Handle).WorkingArea;
                    Location = new Point(
                        wa.Left + Math.Max(0, (wa.Width - Width) / 2),
                        wa.Top + Math.Max(0, (wa.Height - Height) / 2));
                }
                ApplyLayout();
            }
            catch { }

            // soft entrance: the fish scales up and fades in first
            // (0 %–60 % of the animation), the stage text, progress bar
            // and detail text follow (35 %–100 %). ~600 ms total.
            loadShownTick = Environment.TickCount;
            loadPanel.StartAnim();
            loadPanel.StartMarquee();
            float enterT = 0F;
            entranceTimer = new UiTimer(this, 10);
            entranceTimer.Tick += (s, ev) =>
            {
                enterT += 0.012F; // ~830 ms, slow enough to read as a reveal
                if (enterT >= 1F)
                {
                    enterT = 1F;
                    entranceTimer.Stop();
                    entranceTimer.Dispose();
                    entranceTimer = null;
                }
                loadPanel.Entrance = enterT;
                float textT = (enterT - 0.35F) / 0.65F;
                loadPanel.TextAlpha = Theme.EaseOutCubic(textT);
            };
            entranceTimer.Start();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // kicks off in the background; the entrance animation plays
            // first, so its frame pacing is never disturbed
            webViewReady = InitWebViewDeferred();
            // the very first server probe also runs off the UI thread: a
            // stuck socket could otherwise freeze the entrance animation
            // for up to 800 ms
            Task.Run(() =>
            {
                bool up = IsServerUp();
                Ui(() =>
                {
                    if (IsDisposed) return;
                    bool installed = !NeedsInstall();
                    // Only attach to an already-running server when THIS
                    // folder is fully installed. Otherwise the 3080 service
                    // belongs to a different copy (another folder's instance)
                    // — attaching would silently open someone else's
                    // workbench, and after a real first-run install the port
                    // clash would break our own server start anyway.
                    if (up && installed)
                    {
                        poll.Start();
                        OnPoll(null, EventArgs.Empty);
                        return;
                    }

                    if (!installed)
                    {
                        if (up)
                        {
                            ShowError("检测到另一个实例正在运行",
                                "3080 端口已被另一个 DeepSeek Harness 实例占用（可能是从其他文件夹启动的）。\r\n请先关闭那个实例，再点“重试”继续首次下载安装。",
                                true);
                            return;
                        }
                        BuildConfirmList();
                        ShowConfirm();
                        return;
                    }

                    // The load screen is already visible from the constructor —
                    // do NOT route through ShowLoad() here, or the dip-fade would
                    // hide and reshow it (a visible flicker on every fast start).
                    if (!loadPanel.Visible) ShowLoad();
                    loadPanel.StageText = "正在初始化…";
                    poll.Start();
                    StartServerDelayed();
                });
            });
        }

        // Node's process spin-up competes with the entrance animation for
        // CPU and starved the UI thread (the intro played as a blank
        // screen). Give the intro a head start before spawning the server.
        private async void StartServerDelayed()
        {
            try { await Task.Delay(700); } catch { }
            if (navigated || IsDisposed) return;
            StartServer();
            OnPoll(null, EventArgs.Empty);
        }

        private void StartBootstrap()
        {
            busy = true;
            var ctx = MakeStageContext();
            Task.Run(() =>
            {
                string error;
                bool ok = Bootstrap(ctx, out error);
                Ui(() =>
                {
                    busy = false;
                    if (!ok)
                    {
                        loadPanel.SetValue(0);
                        ShowError("初始化失败",
                            error + "\r\n\r\n详细日志见 .launcher\\build.log", true);
                        return;
                    }
                    if (IsServerUp())
                    {
                        // Server stayed (or came) up on its own — e.g. retry
                        // after a page-load failure. Re-navigate instead of
                        // spawning a duplicate server process on the same port.
                        navigateStarted = false;
                        pageReady = false;
                        loadShownTick = Environment.TickCount;
                        poll.Start();
                        OnPoll(null, EventArgs.Empty);
                        return;
                    }
                    StartServer();
                    poll.Start();
                    OnPoll(null, EventArgs.Empty);
                });
            });
        }

        private void StartServer()
        {
            try
            {
                var psi = NewProc(NodeExe, "apps\\cli\\lib\\bin.js web --no-open", DshHome);
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                serverLog = new StreamWriter(ServerLog, false);
                serverProcess = Process.Start(psi);
                DataReceivedEventHandler handler = (s, ev) =>
                {
                    if (ev.Data == null) return;
                    try { lock (serverLog) { serverLog.WriteLine(ev.Data); serverLog.Flush(); } } catch { }
                };
                serverProcess.OutputDataReceived += handler;
                serverProcess.ErrorDataReceived += handler;
                serverProcess.BeginOutputReadLine();
                serverProcess.BeginErrorReadLine();
                loadPanel.StageText = "正在启动服务…";
                loadPanel.DetailText = "约需 5-20 秒";
            }
            catch (Exception ex)
            {
                ShowError("服务启动失败", ex.Message, true);
            }
        }

        private void OnPoll(object sender, EventArgs e)
        {
            if (navigated || serverCheckBusy) return;
            if (webViewFailed)
            {
                poll.Stop();
                return;
            }
            // The HTTP probe is a blocking socket call — run it on a worker
            // thread so the loading animation (also timer-driven, but pumped
            // through Invalidate) never stalls on network latency.
            serverCheckBusy = true;
            Task.Run(() =>
            {
                bool up = IsServerUp();
                Ui(() =>
                {
                    serverCheckBusy = false;
                    if (navigated || IsDisposed) return;
                    if (up) OnServerUp(); else OnServerWaiting();
                });
            });
        }

        private void OnServerUp()
        {
            // Let the loading screen live for a minimum moment — an
            // instant hard cut to the web UI reads as a flicker. The
            // WebView2 must also be fully initialized before hand-off.
            if (Environment.TickCount - loadShownTick < 1200) return;
            if (webView == null || webView.CoreWebView2 == null) return;
            // Pre-navigate while the WebView2 is still hidden behind the
            // loading screen. The page loads "underneath the animation";
            // only once it has finished rendering do we dissolve the
            // loading screen away — no white flash, no layout jank.
            if (!navigateStarted)
            {
                navigateStarted = true;
                webView.CoreWebView2.Navigate(Url);
                return;
            }
            if (!pageReady) return;
            // give the renderer a few frames to settle after load
            if (Environment.TickCount - pageReadyTick < 350) return;
            navigated = true;
            poll.Stop();
            BeginExitToWeb();
        }

        private void OnServerWaiting()
        {
            waitedSeconds++;
            if (serverProcess != null && serverProcess.HasExited)
            {
                poll.Stop();
                ShowError("服务进程意外退出", "服务进程已退出，详细日志见 .launcher\\server.log", true);
            }
            else if (waitedSeconds > 90)
            {
                poll.Stop();
                ShowError("服务启动超时", "90 秒内未打开 3080 端口，详细日志见 .launcher\\server.log", true);
            }
        }

        // Soft hand-off into the web UI: the mascot dissolves while gently
        // growing (as if swimming into the workbench), text and the progress
        // bar sink back into the background, then the WebView2 takes over.
        // ~450 ms, ease-in so the departure accelerates.
        private void BeginExitToWeb()
        {
            if (exiting) return;
            exiting = true;
            float exitT = 0F;
            exitTimer = new UiTimer(this, 10);
            exitTimer.Tick += async (s, ev) =>
            {
                exitT += 0.022F;
                if (exitT > 1F) exitT = 1F;
                float e2 = Theme.EaseInCubic(exitT);
                loadPanel.Exit = exitT;
                // text and the bar sink back into the background
                loadPanel.TextAlpha = 1F - e2;
                if (exitT >= 1F)
                {
                    exitTimer.Stop();
                    exitTimer.Dispose();
                    exitTimer = null;
                    // make sure the deferred WebView2 init has finished
                    try { if (webViewReady != null) await webViewReady; } catch { }
                    CrossFadeToWeb();
                }
            };
            exitTimer.Start();
        }

        // Final hand-off: the loading screen's background board fades out
        // over the already-rendered web UI. A snapshot of the load panel is
        // shown in a separate layered overlay window (WebView2 cannot be
        // overdrawn inside the same window), the real web view is revealed
        // underneath, and the overlay fades to transparent.
        private void CrossFadeToWeb()
        {
            Bitmap snap = null;
            try
            {
                snap = new Bitmap(loadPanel.Width, loadPanel.Height);
                loadPanel.DrawToBitmap(snap, new Rectangle(0, 0, snap.Width, snap.Height));
            }
            catch
            {
                if (snap != null) { snap.Dispose(); snap = null; }
            }

            HideAllBootPanels();
            if (webView == null || webView.CoreWebView2 == null)
            {
                if (snap != null) snap.Dispose();
                return;
            }
            webView.Visible = true;
            if (snap == null || IsDisposed) return;

            var overlay = new OverlayForm(snap)
            {
                Bounds = new Rectangle(PointToScreen(Point.Empty), ClientSize),
            };
            overlay.Show(this); // owned: stays above the main window, no taskbar entry
            var fade = new UiTimer(this, 16);
            fade.Tick += (s, e) =>
            {
                overlay.Opacity -= 0.07; // ~240 ms fade
                if (overlay.Opacity <= 0.03)
                {
                    fade.Stop();
                    fade.Dispose();
                    overlay.Close();
                    overlay.Dispose();
                }
            };
            fade.Start();
        }

        private void KillServerTree()
        {
            using (Process.Start(new ProcessStartInfo("taskkill", "/PID " + serverProcess.Id + " /T /F")
            { UseShellExecute = false, CreateNoWindow = true })) { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy)
            {
                MessageBox.Show(this,
                    "正在下载 / 安装 / 构建，关闭将导致状态不完整，请等待完成。",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                e.Cancel = true;
                return;
            }
            try { loadPanel.StopAnim(); } catch { }
            if (serverProcess != null && !serverProcess.HasExited)
            {
                try { KillServerTree(); } catch { }
            }
            try { if (serverLog != null) serverLog.Dispose(); } catch { }
            base.OnFormClosing(e);
        }
    }
}
