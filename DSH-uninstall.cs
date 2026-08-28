// 卸载.exe - standalone windowed uninstaller for DSH-Launcher.
//
// Design notes:
//  - Only deletes what THIS launcher downloaded, all of which lives inside
//    its own folder (deepseek-harness\ and .launcher\ — the latter holds
//    the portable Node.js/pnpm runtime, the pnpm package store and the
//    npm cache). Shared Node-ecosystem caches (%LOCALAPPDATA%\npm-cache,
//    <drive>:\.pnpm-store) are NEVER touched: they do not belong to us.
//  - The account profile (~\.dsh, keys/settings) is opt-in via checkbox.
//  - Top banner panel is reserved for future image/gif decoration; the
//    xiezai logo already sits there.
//  - "--auto" runs with default selections and exits (for testing).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class Uninstaller
{
    private static string BaseDir { get { return AppDomain.CurrentDomain.BaseDirectory; } }
    private static string DshHome { get { return Path.Combine(BaseDir, "deepseek-harness"); } }
    private static string RuntimeDir { get { return Path.Combine(BaseDir, ".launcher"); } }
    private static string LauncherExe { get { return Path.Combine(BaseDir, "DSH-Launcher.exe"); } }
    private static string ProfileDir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh"); }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        bool auto = args.Any(a => a == "--auto" || a == "--yes" || a == "-y");
        try { SetProcessDPIAware(); } catch { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var form = new UninstallForm(auto);
        Application.Run(form);
        return form.FailedCount > 0 ? 1 : 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetDpiForSystem();

    // ---------- process helpers ----------

    internal static void KillOurProcesses()
    {
        // Launcher of THIS folder only (full-path match) + its process tree.
        foreach (var p in Process.GetProcessesByName("DSH-Launcher"))
        {
            try
            {
                string exePath = p.MainModule.FileName;
                if (!string.Equals(exePath, LauncherExe, StringComparison.OrdinalIgnoreCase)) continue;
                using (Process.Start(new ProcessStartInfo("taskkill",
                    "/PID " + p.Id + " /T /F") { UseShellExecute = false, CreateNoWindow = true })) { }
            }
            catch { }
        }
        // Leftover node.exe belonging to our portable runtime only.
        try
        {
            string nodePrefix = Path.Combine(RuntimeDir, @"runtime\node\").ToLowerInvariant();
            using (var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ExecutablePath FROM Win32_Process WHERE Name = 'node.exe'"))
            foreach (ManagementObject mo in searcher.Get())
            {
                string path = mo["ExecutablePath"] as string;
                if (path != null && path.ToLowerInvariant().StartsWith(nodePrefix))
                {
                    try
                    {
                        using (Process.Start(new ProcessStartInfo("taskkill",
                            "/PID " + mo["ProcessId"] + " /T /F")
                        { UseShellExecute = false, CreateNoWindow = true })) { }
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    // 移除开机自启动注册表项（若用户曾开启），避免卸载后残留指向已删除 exe 的启动项
    internal static void RemoveAutoStart()
    {
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (k != null) k.DeleteValue("DSH-Launcher", false);
            }
        }
        catch { }
    }

    // ---------- deletion engine ----------

    internal sealed class DeleteResult
    {
        public long Files;
        public long Bytes;
        public readonly List<string> Failed = new List<string>();
    }

    // File enumeration that does NOT descend into reparse points (junctions /
    // symlinked dirs). pnpm's node_modules is full of junctions; following
    // them counts the same files many times (and could escape our tree).
    internal static List<string> EnumerateRealFiles(string root)
    {
        var list = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { subdirs = new string[0]; }
            foreach (string d in subdirs)
            {
                if (!IsReparsePoint(d)) stack.Push(d);
            }
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { continue; }
            list.AddRange(files);
        }
        return list;
    }

    // All directories including reparse-point links themselves (so the links
    // get removed), but never descending into them.
    internal static List<string> EnumerateRealDirs(string root)
    {
        var list = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (string d in subdirs)
            {
                list.Add(d);
                if (!IsReparsePoint(d)) stack.Push(d);
            }
        }
        return list;
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }

    internal static DeleteResult DeleteTree(string root, Action<long, long, string> report, HardlinkDedupe dedupe)
    {
        var result = new DeleteResult();
        if (!Directory.Exists(root)) return result;

        var files = EnumerateRealFiles(root);

        long total = 0;
        var sizes = new long[files.Count];
        for (int i = 0; i < files.Count; i++)
        {
            long len = 0;
            try { len = new FileInfo(files[i]).Length; } catch { }
            if (dedupe != null) len = dedupe.Effective(files[i], len);
            sizes[i] = len;
            total += len;
        }

        long doneBytes = 0;
        for (int i = 0; i < files.Count; i++)
        {
            try
            {
                File.SetAttributes(files[i], FileAttributes.Normal);
                File.Delete(files[i]);
                result.Files++;
                result.Bytes += sizes[i];
            }
            catch { result.Failed.Add(files[i]); }
            doneBytes += sizes[i];
            if (i % 20 == 0 && report != null) report(doneBytes, total, files[i]);
        }

        // Retry the failures once (handles transient locks).
        for (int round = 0; round < 3 && result.Failed.Count > 0; round++)
        {
            Thread.Sleep(500);
            var retry = result.Failed.ToList();
            result.Failed.Clear();
            foreach (string f in retry)
            {
                try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); }
                catch { result.Failed.Add(f); }
            }
        }

        foreach (string d in EnumerateRealDirs(root).OrderByDescending(x => x.Length))
        {
            try { Directory.Delete(d, false); } catch { }
        }
        try { Directory.Delete(root, false); }
        catch { if (!result.Failed.Contains(root)) result.Failed.Add(root); }
        return result;
    }

    internal static string FormatSize(long bytes)
    {
        if (bytes >= 1073741824) return (bytes / 1073741824.0).ToString("0.0") + " GB";
        if (bytes >= 1048576) return (bytes / 1048576.0).ToString("0") + " MB";
        if (bytes >= 1024) return (bytes / 1024.0).ToString("0") + " KB";
        return bytes + " B";
    }

    // Counts the bytes of hardlinked files only once (per volume+file-index).
    // pnpm hardlinks store files into node_modules, so the same physical
    // bytes would otherwise show up in BOTH cards and double the totals.
    internal sealed class HardlinkDedupe
    {
        private readonly HashSet<string> seen = new HashSet<string>();

        internal long Effective(string path, long length)
        {
            if (length <= 0) return 0;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    BY_HANDLE_FILE_INFORMATION info;
                    if (GetFileInformationByHandle(fs.SafeFileHandle, out info) && info.nNumberOfLinks > 1)
                    {
                        string key = info.dwVolumeSerialNumber + ":" + info.nFileIndexHigh + ":" + info.nFileIndexLow;
                        if (!seen.Add(key)) return 0;
                    }
                }
            }
            catch { }
            return length;
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    // ---------- theme ----------

    internal static class Theme
    {
        // Blue palette matched to the xiezai mascot (navy hair, blue eyes).
        public static readonly Color Bg = Color.FromArgb(243, 246, 252);
        public static readonly Color Banner = Color.FromArgb(224, 234, 249);
        public static readonly Color Card = Color.White;
        public static readonly Color CardBorder = Color.FromArgb(206, 219, 243);
        public static readonly Color Text = Color.FromArgb(37, 47, 90);
        public static readonly Color Gray = Color.FromArgb(116, 126, 158);
        public static readonly Color Accent = Color.FromArgb(63, 96, 178);
        public static readonly Color Danger = Color.FromArgb(214, 69, 65);
        public static readonly Color DangerHover = Color.FromArgb(192, 57, 53);
        public static readonly Color Ok = Color.FromArgb(30, 140, 70);
        public static readonly Color Warn = Color.FromArgb(190, 130, 20);

        // Pixel-unit fonts: with SetProcessDPIAware, point fonts scale with
        // the system DPI while pixel layout does not, breaking the layout on
        // hi-DPI screens. Pixels keep both in the same unit, always.
        public static Font Title { get { return new Font("Microsoft YaHei UI", 30F, FontStyle.Bold, GraphicsUnit.Pixel); } }
        public static Font H1 { get { return new Font("Microsoft YaHei UI", 26F, FontStyle.Bold, GraphicsUnit.Pixel); } }
        public static Font Body { get { return new Font("Microsoft YaHei UI", 22F, FontStyle.Regular, GraphicsUnit.Pixel); } }
        public static Font Small { get { return new Font("Microsoft YaHei UI", 18F, FontStyle.Regular, GraphicsUnit.Pixel); } }
        public static Font Tiny { get { return new Font("Microsoft YaHei UI", 15F, FontStyle.Regular, GraphicsUnit.Pixel); } }

        public static Image LoadLogo(int size)
        {
            try
            {
                using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream("xiezai.png"))
                {
                    if (st == null) return null;
                    using (var img = Image.FromStream(st))
                    {
                        // High-quality downscale — the default Bitmap(img, w, h)
                        // constructor uses fast interpolation and looks blurry.
                        var bmp = new Bitmap(size, size);
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                            g.DrawImage(img, 0, 0, size, size);
                        }
                        return bmp;
                    }
                }
            }
            catch { return null; }
        }
    }

    // ---------- window ----------

    private sealed class UninstallForm : Form
    {
        private sealed class Item
        {
            public CheckBox Box;
            public Label SizeLabel;
            public string Path;
            public string Title;
            public long ScannedBytes = -1;
        }

        private readonly List<Item> items = new List<Item>();
        private readonly bool auto;
        private readonly Panel mainPanel = new Panel();
        private readonly Panel runPanel = new Panel();
        private readonly Panel donePanel = new Panel();
        private readonly Label stageLabel = new Label();
        private readonly ProgressBar bar = new ProgressBar();
        private readonly Label detailLabel = new Label();
        private readonly TextBox logBox = new TextBox();
        private readonly Label doneTitle = new Label();
        private readonly Label summaryLabel = new Label();
        private readonly Label keptLabel = new Label();
        private readonly Label countdownLabel = new Label();
        private readonly Button exitButton = new Button();
        private readonly System.Windows.Forms.Timer countdown = new System.Windows.Forms.Timer();
        private int countdownLeft = 8;
        private long totalBytes;
        private long deletedBytes;
        private bool exiting;

        public int FailedCount;

        public UninstallForm(bool autoMode)
        {
            auto = autoMode;
            Text = "卸载 DSH-Launcher";
            Width = 780;
            Height = 800;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            BackColor = Theme.Bg;
            Font = Theme.Body;
            try
            {
                using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream("xiezai.ico"))
                {
                    if (st != null) Icon = new Icon(st);
                }
            }
            catch { }

            BuildMainPanel();
            BuildRunPanel();
            BuildDonePanel();
            Controls.Add(mainPanel);
            Controls.Add(runPanel);
            Controls.Add(donePanel);
            runPanel.Visible = false;
            donePanel.Visible = false;

            countdown.Interval = 1000;
            countdown.Tick += (s, e) =>
            {
                countdownLeft--;
                if (countdownLeft <= 0) { countdown.Stop(); FinishAndSelfDelete(); }
                else countdownLabel.Text = "窗口将于 " + countdownLeft + " 秒后自动关闭，卸载程序将同时删除自身";
            };

            Shown += (s, e) =>
            {
                Task.Run((Action)ScanSizes);
                if (auto) BeginInvoke((Action)(() => StartUninstall()));
            };
        }

        // Top banner: big centered mascot image (decoration area; swap for
        // image/gif later), title + subtitle centered below it.
        private Panel MakeBanner(string subtitle)
        {
            int w = ClientSize.Width > 0 ? ClientSize.Width : 764;
            var banner = new Panel { Dock = DockStyle.Top, Height = 340, BackColor = Theme.Banner };
            // Render at PHYSICAL pixels: OnLoad scales the form up by the DPI
            // factor, which would stretch a logical-size bitmap and blur it.
            float dpiF = 1F;
            try { dpiF = GetDpiForSystem() / 96F; } catch { }
            if (dpiF < 1F) dpiF = 1F;
            var logo = Theme.LoadLogo((int)(264 * dpiF));
            if (logo != null)
            {
                var pic = new PictureBox
                {
                    Image = logo,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Left = (w - 264) / 2,
                    Top = 6,
                    Width = 264,
                    Height = 264,
                    BackColor = Color.Transparent,
                };
                banner.Controls.Add(pic);
            }
            banner.Controls.Add(new Label
            {
                Text = "卸载 DSH-Launcher",
                Font = Theme.H1,
                ForeColor = Theme.Text,
                Left = 0,
                Top = 274,
                Width = w,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.Transparent,
            });
            banner.Controls.Add(new Label
            {
                Text = subtitle,
                Font = Theme.Tiny,
                ForeColor = Theme.Gray,
                Left = 0,
                Top = 312,
                Width = w,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.Transparent,
            });
            return banner;
        }

        private Panel MakeCard(int y, out CheckBox boxOut, out Label sizeOut, string title, string note, bool required)
        {
            var card = new Panel
            {
                Left = 24,
                Top = y,
                Width = 716,
                Height = 62,
                BackColor = Theme.Card,
            };
            card.Paint += (s, e) =>
                e.Graphics.DrawRectangle(new Pen(Theme.CardBorder), 0, 0, card.Width - 1, card.Height - 1);

            var box = new CheckBox
            {
                Left = 14,
                Top = 4,
                Width = 372,
                Font = Theme.Body,
                ForeColor = Theme.Text,
                Text = title,
                Checked = required,
                Enabled = !required,
                BackColor = Color.Transparent,
            };
            var size = new Label
            {
                Left = 514,
                Top = 18,
                Width = 190,
                TextAlign = ContentAlignment.MiddleRight,
                Font = Theme.Tiny,
                ForeColor = Theme.Accent,
                Text = "统计中…",
                BackColor = Color.Transparent,
            };
            card.Controls.Add(box);
            card.Controls.Add(size);
            if (note != null)
            {
                card.Controls.Add(new Label
                {
                    Text = note,
                    Left = 38,
                    Top = 34,
                    Width = 560,
                    Height = 22,
                    Font = Theme.Tiny,
                    ForeColor = Theme.Gray,
                    BackColor = Color.Transparent,
                });
            }
            boxOut = box;
            sizeOut = size;
            return card;
        }

        private Button MakeButton(string text, int x, int y, int w, Color bg, Color fg, Color? hover)
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
            b.FlatAppearance.BorderColor = Theme.CardBorder;
            if (hover.HasValue)
            {
                b.MouseEnter += (s, e) => b.BackColor = hover.Value;
                b.MouseLeave += (s, e) => b.BackColor = bg;
            }
            return b;
        }

        private void BuildMainPanel()
        {
            mainPanel.Dock = DockStyle.Fill;
            mainPanel.BackColor = Theme.Bg;
            mainPanel.Controls.Add(MakeBanner("请选择需要删除的内容"));

            int y = 354;
            AddItem("程序、源码与依赖", "deepseek-harness\\", DshHome, true, ref y);
            AddItem("便携运行时与本地下载缓存", ".launcher\\（Node.js / pnpm / 包仓库 / WebView2 数据）", RuntimeDir, true, ref y);
            AddItem("账号配置与密钥（可选）", "删除后需重新登录。该目录与其他 DSH 安装共用（~\\.dsh）", ProfileDir, false, ref y);

            var note = new Label
            {
                Text = "卸载范围为本程序下载的程序与缓存文件，目录中您自行存放的内容不受影响。\r\n卸载完成后，本卸载程序将自动删除自身。",
                Font = Theme.Small,
                ForeColor = Theme.Gray,
                Left = 24,
                Top = y + 8,
                Width = 716,
                Height = 64,
                BackColor = Color.Transparent,
            };
            mainPanel.Controls.Add(note);

            var startButton = MakeButton("开始卸载", 24, y + 104, 220, Theme.Danger, Color.White, Theme.DangerHover);
            startButton.FlatAppearance.BorderSize = 0;
            startButton.Click += (s, e) => StartUninstall();
            var cancelButton = MakeButton("取消", 260, y + 104, 140, Theme.Card, Theme.Text, null);
            cancelButton.Click += (s, e) => Close();
            mainPanel.Controls.Add(startButton);
            mainPanel.Controls.Add(cancelButton);
            AcceptButton = startButton;
            CancelButton = cancelButton;
        }

        private void AddItem(string title, string note, string path, bool required, ref int y)
        {
            CheckBox box;
            Label size;
            var card = MakeCard(y, out box, out size, title, note, required);
            mainPanel.Controls.Add(card);
            items.Add(new Item { Box = box, SizeLabel = size, Path = path, Title = title });
            y += 74;
        }

        private void ScanSizes()
        {
            // One shared dedupe across all cards: hardlinked store files are
            // attributed to the first card scanned, not counted twice.
            var dedupe = new Uninstaller.HardlinkDedupe();
            foreach (var item in items)
            {
                var captured = item;
                long bytes = 0;
                long count = 0;
                if (Directory.Exists(item.Path))
                {
                    try
                    {
                        foreach (string f in Uninstaller.EnumerateRealFiles(item.Path))
                        {
                            long len = 0;
                            try { len = new FileInfo(f).Length; } catch { }
                            bytes += dedupe.Effective(f, len);
                            count++;
                            if (count % 300 == 0)
                            {
                                long b = bytes, c = count;
                                Ui(() => captured.SizeLabel.Text = c.ToString("N0") + " 个 · " + FormatSize(b));
                            }
                        }
                    }
                    catch { }
                }
                item.ScannedBytes = bytes;
                long bFinal = bytes, cFinal = count;
                Ui(() =>
                {
                    if (captured.ScannedBytes <= 0)
                    {
                        captured.Box.Enabled = false;
                        captured.SizeLabel.Text = "不存在";
                    }
                    else
                    {
                        captured.SizeLabel.Text = cFinal.ToString("N0") + " 个 · " + FormatSize(bFinal);
                    }
                });
            }
        }

        private void BuildRunPanel()
        {
            runPanel.Dock = DockStyle.Fill;
            runPanel.BackColor = Theme.Bg;
            runPanel.Controls.Add(MakeBanner("正在删除所选内容，请稍候"));

            stageLabel.Font = Theme.H1;
            stageLabel.ForeColor = Theme.Text;
            stageLabel.AutoSize = true;
            stageLabel.Left = 24;
            stageLabel.Top = 356;
            stageLabel.Text = "正在准备…";
            stageLabel.BackColor = Color.Transparent;

            bar.Left = 24;
            bar.Top = 400;
            bar.Width = 716;
            bar.Height = 16;

            detailLabel.Font = Theme.Small;
            detailLabel.ForeColor = Theme.Gray;
            detailLabel.Left = 24;
            detailLabel.Top = 428;
            detailLabel.Width = 716;
            detailLabel.Height = 56;
            detailLabel.BackColor = Color.Transparent;

            logBox.Left = 24;
            logBox.Top = 492;
            logBox.Width = 716;
            logBox.Height = 240;
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.Font = new Font("Consolas", 16F, FontStyle.Regular, GraphicsUnit.Pixel);
            logBox.BackColor = Color.White;
            logBox.BorderStyle = BorderStyle.FixedSingle;

            runPanel.Controls.Add(stageLabel);
            runPanel.Controls.Add(bar);
            runPanel.Controls.Add(detailLabel);
            runPanel.Controls.Add(logBox);
        }

        private void BuildDonePanel()
        {
            donePanel.Dock = DockStyle.Fill;
            donePanel.BackColor = Theme.Bg;
            donePanel.Controls.Add(MakeBanner("感谢使用"));

            doneTitle.Font = new Font("Microsoft YaHei UI", 32F, FontStyle.Bold, GraphicsUnit.Pixel);
            doneTitle.ForeColor = Theme.Ok;
            doneTitle.AutoSize = true;
            doneTitle.Left = 24;
            doneTitle.Top = 356;
            doneTitle.Text = "✓ 卸载完成";
            doneTitle.BackColor = Color.Transparent;

            summaryLabel.Font = Theme.Body;
            summaryLabel.ForeColor = Theme.Text;
            summaryLabel.Left = 24;
            summaryLabel.Top = 410;
            summaryLabel.Width = 716;
            summaryLabel.Height = 76;
            summaryLabel.BackColor = Color.Transparent;

            keptLabel.Font = Theme.Small;
            keptLabel.ForeColor = Theme.Gray;
            keptLabel.Left = 24;
            keptLabel.Top = 498;
            keptLabel.Width = 716;
            keptLabel.Height = 140;
            keptLabel.BackColor = Color.Transparent;

            countdownLabel.Font = Theme.Body;
            countdownLabel.ForeColor = Theme.Text;
            countdownLabel.Left = 24;
            countdownLabel.Top = 648;
            countdownLabel.Width = 716;
            countdownLabel.BackColor = Color.Transparent;

            exitButton.Text = "立即退出";
            exitButton.Left = 24;
            exitButton.Top = 690;
            exitButton.Width = 200;
            exitButton.Height = 52;
            exitButton.FlatStyle = FlatStyle.Flat;
            exitButton.BackColor = Theme.Accent;
            exitButton.ForeColor = Color.White;
            exitButton.FlatAppearance.BorderSize = 0;
            exitButton.Click += (s, e) => FinishAndSelfDelete();

            donePanel.Controls.Add(doneTitle);
            donePanel.Controls.Add(summaryLabel);
            donePanel.Controls.Add(keptLabel);
            donePanel.Controls.Add(countdownLabel);
            donePanel.Controls.Add(exitButton);
        }

        private void Ui(Action action)
        {
            try { if (IsHandleCreated) BeginInvoke(action); } catch { }
        }

        private void Log(string line)
        {
            Ui(() => logBox.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + line + "\r\n"));
        }

        private void StartUninstall()
        {
            // Disabled-but-checked boxes are the REQUIRED core items — they
            // must be included. (Nonexistent dirs are a harmless no-op.)
            var selected = items.Where(i => i.Box.Checked).ToList();
            mainPanel.Visible = false;
            donePanel.Visible = false;
            runPanel.Visible = true;
            Task.Run(() => RunUninstall(selected));
        }

        private void RunUninstall(List<Item> selected)
        {
            var allFailed = new List<string>();

            SetStage("正在停止相关进程…", "");
            Log("停止启动器与服务进程…");
            KillOurProcesses();
            Log("移除开机自启动项…");
            RemoveAutoStart();
            Thread.Sleep(1500);
            Log("进程已停止");

            totalBytes = 0;
            var dedupe = new Uninstaller.HardlinkDedupe();
            var paths = selected.Select(i => i.Path).Where(Directory.Exists).ToList();
            foreach (string p in paths)
            {
                try
                {
                    foreach (string f in Uninstaller.EnumerateRealFiles(p))
                    {
                        long len = 0;
                        try { len = new FileInfo(f).Length; } catch { }
                        totalBytes += dedupe.Effective(f, len);
                    }
                }
                catch { }
            }
            deletedBytes = 0;

            foreach (string path in paths)
            {
                string shortName = Shorten(path);
                SetStage("正在删除：" + Path.GetFileName(path.TrimEnd('\\')), "");
                Log("开始删除 " + shortName + " …");
                var res = Uninstaller.DeleteTree(path, (done, total, current) =>
                {
                    long currentDeleted = deletedBytes + done;
                    int percent = totalBytes > 0 ? (int)(currentDeleted * 100 / totalBytes) : 0;
                    Ui(() =>
                    {
                        bar.Value = Math.Max(0, Math.Min(100, percent));
                        detailLabel.Text = shortName + "\r\n已释放 " + FormatSize(currentDeleted)
                            + " / " + FormatSize(totalBytes) + "（" + percent + "%）";
                    });
                }, dedupe);
                deletedBytes += res.Bytes;
                allFailed.AddRange(res.Failed);
                if (res.Failed.Count == 0)
                    Log("✓ 已删除 " + shortName + "（" + res.Files + " 个文件，释放 " + FormatSize(res.Bytes) + "）");
                else
                    Log("△ " + shortName + " 有 " + res.Failed.Count + " 项被占用未能删除");
            }

            FailedCount = allFailed.Count;
            Ui(() =>
            {
                bar.Value = 100;
                detailLabel.Text = "";
                ShowDone(allFailed, selected);
            });
        }

        private void SetStage(string text, string detail)
        {
            Ui(() =>
            {
                stageLabel.Text = text;
                detailLabel.Text = detail;
            });
        }

        private void ShowDone(List<string> failed, List<Item> selected)
        {
            runPanel.Visible = false;
            donePanel.Visible = true;
            summaryLabel.Text = "已释放约 " + FormatSize(deletedBytes)
                + (failed.Count == 0
                    ? "，本程序下载的内容已全部删除。"
                    : "。\r\n有 " + failed.Count + " 个文件因被占用未能删除，可稍后手动删除。");

            var kept = new List<string>();
            if (!selected.Any(i => i.Path == ProfileDir) && Directory.Exists(ProfileDir))
                kept.Add("账号配置与密钥：" + ProfileDir);
            kept.Add("系统共享缓存（npm-cache、pnpm 全局仓库）");
            keptLabel.Text = "以下内容被保留：\r\n · " + string.Join("\r\n · ", kept);

            if (failed.Count > 0)
            {
                doneTitle.Text = "△ 卸载基本完成";
                doneTitle.ForeColor = Theme.Warn;
            }

            if (auto) { FinishAndSelfDelete(); return; }
            countdownLeft = 8;
            countdownLabel.Text = "窗口将于 " + countdownLeft + " 秒后自动关闭，卸载程序将同时删除自身";
            countdown.Start();
        }

        private void FinishAndSelfDelete()
        {
            if (exiting) return;
            exiting = true;
            countdown.Stop();
            try
            {
                // Detached cmd waits for this process to exit, then deletes
                // the launcher, this uninstaller, and the folder if empty
                // (rmdir has no /s: the user's own files keep it alive).
                Process.Start(new ProcessStartInfo("cmd.exe",
                    "/c ping -n 3 127.0.0.1 >nul & del /f /q \"" + LauncherExe
                    + "\" & del /f /q \"" + Application.ExecutablePath
                    + "\" & ping -n 2 127.0.0.1 >nul & rmdir \"" + BaseDir.TrimEnd('\\')
                    + "\" & ping -n 2 127.0.0.1 >nul & rmdir \"" + BaseDir.TrimEnd('\\') + "\"")
                { UseShellExecute = false, CreateNoWindow = true });
            }
            catch { }
            Application.Exit();
        }

        private static string Shorten(string path)
        {
            return path.Length <= 60 ? path : "…" + path.Substring(path.Length - 59);
        }

        // Manual DPI scaling must run AFTER the window handle exists:
        // Control.Scale called in the constructor updates the .NET size but
        // the OS window is still created unscaled (verified empirically).
        // Pixel-unit fonts scale here too, keeping text and layout in sync.
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try
            {
                float f = GetDpiForSystem() / 96F;
                if (f > 1.05F) Scale(new SizeF(f, f));
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (runPanel.Visible && !donePanel.Visible && !exiting)
            {
                MessageBox.Show(this, "卸载正在进行中，请等待完成。", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }
    }
}
