// ============================================================================
// CodeBuddy 工作套件 - 桌面壳
//
// 把索引库从"浏览器里的一个标签页"变成"一个独立窗口"：
//   · 启动时自动拉起服务（已在跑则跳过）
//   · 用 Edge 的 --app 模式开无地址栏窗口，再把它嵌入本窗体
//   · 最小化 / 关闭按钮 -> 收进右下角托盘，双击托盘恢复
//   · 托盘右键菜单：显示窗口、打开面板、关闭窗口、停止服务并退出
//   · 不写注册表 Run、不建启动项 —— 只有手动点快捷方式才启动
//
// 嵌入失败（找不到 Edge、拿不到窗口句柄）时自动降级为"外部窗口 + 托盘控制显隐"，
// 不会因为环境差异而无法使用。
//
// 编译：
//   csc.exe /target:winexe /out:CodeBuddyWorkKit.Shell.exe
//           /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll Shell.cs
// ============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CodeBuddyWorkKit
{
    internal static class ShellProgram
    {
        private static Mutex _single;

        /// <summary>
        /// Cross-process "show yourself" signal. Double-clicking the desktop
        /// shortcut while the window is already in the tray must raise the window,
        /// not pop up an "already running" dialog.
        /// </summary>
        internal static EventWaitHandle ShowSignal;

        /// <summary>Raised for uncaught exceptions so the main window can show them.</summary>
        internal static event Action<string, string> GlobalError;

        internal static void ReportGlobal(string source, Exception ex)
        {
            Action<string, string> handler = GlobalError;
            if (handler != null)
            {
                try { handler(source, ex == null ? "未知异常" : ex.Message); } catch { }
            }
            ShellForm.Log("GLOBAL " + source + ": " + ex);
        }

        [STAThread]
        internal static int Main(string[] args)
        {
            bool created;
            _single = new Mutex(true, "CodeBuddyWorkKit.Shell", out created);

            if (!created)
            {
                // Second instance: wake the first one up and leave quietly.
                try
                {
                    EventWaitHandle signal = EventWaitHandle.OpenExisting("CodeBuddyWorkKit.Show");
                    signal.Set();
                    signal.Close();
                }
                catch
                {
                    MessageBox.Show(
                        "套件已经在运行，请查看右下角托盘图标。",
                        "CodeBuddy 工作套件",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                return 0;
            }

            ShowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "CodeBuddyWorkKit.Show");

            // 兜住所有未捕获异常：写日志 + 进报错列表，而不是静默退出
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                ReportGlobal("界面异常", e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                ReportGlobal("未捕获异常", e.ExceptionObject as Exception);
            };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ShellForm(args));
            return 0;
        }
    }

    internal class ShellForm : Form
    {
        // ---------------------------------------------------------------- Win32
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetParent(IntPtr child, IntPtr parent);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int cmd);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_WINDOWEDGE = 0x00000100;
        private const int WS_EX_CLIENTEDGE = 0x00000200;
        private const int WS_EX_STATICEDGE = 0x00020000;
        private const int WS_EX_DLGMODALFRAME = 0x00000001;
        private const int WS_EX_APPWINDOW = 0x00040000;

        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_NOACTIVATE = 0x0010;

        // DWMWA_NCRENDERING_POLICY = 2, DWMNCRP_DISABLED
        private const int DWMWA_NCRENDERING_POLICY = 2;
        // DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_DONOTROUND (Win11 22H2+)
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, bool erase);

        [DllImport("user32.dll")]
        private static extern bool UpdateWindow(IntPtr hWnd);

        private struct RECT
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        private const int GWL_STYLE = -16;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_CHILD = 0x40000000;
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const int SW_MAXIMIZE = 3;
        private const int SW_RESTORE = 9;
        private const int SW_MINIMIZE = 6;

        // ---------------------------------------------------------------- state
        private readonly string _kitRoot;
        private readonly string _exeDir;
        private int _port = 47910;
        private int _pulse = 47821;
        private int _tuner = 47822;
        private string _host = "127.0.0.1";

        private Process _edge;
        private IntPtr _pageHwnd = IntPtr.Zero;
        private bool _embedded;
        private NotifyIcon _tray;

        // ---- 顶部工具条 / 错误上报 / 健康自检 -------------------------------
        private sealed class ShellError
        {
            internal DateTime At;
            internal string Source = "";
            internal string Message = "";
        }

        private const int TOP_BAR_HEIGHT = 42;

        private readonly List<ShellError> _errors = new List<ShellError>();
        private Panel _topBar;
        private Label _statusLabel;
        private Panel _errorButton;
        private Label _errorBadge;
        private Label _errorText;
        private System.Windows.Forms.Timer _healthTimer;
        private DateTime _lastHealthTick = DateTime.MinValue;
        private int _healthFailStreak;
        private int _lastDownCount = -1;
        private bool _closingForReal;
        private bool _stopServicesOnExit;
        private System.Windows.Forms.Timer _attachTimer;
        private DateTime _attachDeadline;
        private HashSet<uint> _preExistingEdge = new HashSet<uint>();
        private string _profileDir = "";

        // --- guard notifications (Pulse 的重复进程告警推到托盘气泡)
        private sealed class GuardAlert
        {
            internal string Level = "";
            internal string Key = "";
            internal int Count;
            internal long At;
            internal string Id { get { return Level + "@" + At + "#" + Key; } }
        }

        private System.Windows.Forms.Timer _guardTimer;
        private readonly List<GuardAlert> _recentAlerts = new List<GuardAlert>();
        private string _lastAlertId = "";
        private DateTime _lastNotifyAt = DateTime.MinValue;
        private bool _notifyMuted;
        private ToolStripMenuItem _guardMenu;
        private Icon _iconNormal;
        private Icon _iconAlert;

        internal ShellForm(string[] args)
        {
            _exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            _kitRoot = ResolveKitRoot(_exeDir);
            ReadConfig();
            ParseArgs(args);

            Text = "CodeBuddy 工作套件";
            // Fit the work area: a fixed 1360x900 window overflows a 1366x768 laptop.
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            Width = Math.Min(1360, work.Width - 40);
            Height = Math.Min(900, work.Height - 40);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(960, 640);
            BackColor = Color.FromArgb(18, 24, 34);
            Font = new Font("Microsoft YaHei UI", 9F);
            try { Icon = SystemIcons.Application; } catch { }

            BuildTopBar();
            BuildTray();
            Load += OnLoaded;
            Resize += OnResized;
            FormClosing += OnFormClosing;

            // Listen for a second launch: raise the window instead of ignoring it.
            Thread watcher = new Thread(delegate()
            {
                while (true)
                {
                    try
                    {
                        if (ShellProgram.ShowSignal == null) break;
                        ShellProgram.ShowSignal.WaitOne();
                    }
                    catch
                    {
                        break;
                    }
                    if (IsDisposed) break;
                    try
                    {
                        Log("show signal received");
                        BeginInvoke(new Action(RestoreWindow));
                    }
                    catch
                    {
                        break;
                    }
                }
            });
            watcher.IsBackground = true;
            watcher.Start();

            StartGuardWatcher();
            StartHealthWatch();

            // 未捕获异常也进报错列表，而不是弹一个没人看的对话框
            ShellProgram.GlobalError += AddError;
        }

        /// <summary>CLI overrides, mainly so the shell can be tested without a deploy.json.</summary>
        private void ParseArgs(string[] args)
        {
            for (int i = 0; i < args.Length; i += 1)
            {
                if (args[i] == "--pulse-port" && i + 1 < args.Length)
                {
                    int value;
                    if (int.TryParse(args[i + 1], out value) && value > 0) _pulse = value;
                    i += 1;
                }
                else if (args[i] == "--codevault-port" && i + 1 < args.Length)
                {
                    int value;
                    if (int.TryParse(args[i + 1], out value) && value > 0) _port = value;
                    i += 1;
                }
                else if (args[i] == "--mute")
                {
                    _notifyMuted = true;
                }
            }
        }

        // ---------------------------------------------------------------- guard watch

        /// <summary>
        /// Poll Pulse's /api/guard on a timer. New warn/lock alerts become a tray
        /// balloon; the tray icon switches to a warning glyph while any group is
        /// still locked. Polling (not pushing) keeps the shell independent of Pulse
        /// being up, and costs one local HTTP request every 5 seconds.
        /// </summary>
        private void StartGuardWatcher()
        {
            _guardTimer = new System.Windows.Forms.Timer();
            _guardTimer.Interval = 5000;
            _guardTimer.Tick += delegate { CheckGuard(); };
            _guardTimer.Start();
            CheckGuard();
        }

        private void CheckGuard()
        {
            List<GuardAlert> alerts;
            bool anyLocked;
            if (!TryReadGuard(out alerts, out anyLocked)) return;

            // Tray glyph: red-ish warning while something is locked
            try
            {
                Icon target = anyLocked ? _iconAlert : _iconNormal;
                if (target != null && _tray.Icon != target) _tray.Icon = target;
            }
            catch
            {
                /* icon swap is cosmetic */
            }

            bool added = false;
            foreach (GuardAlert alert in alerts)
            {
                if (_recentAlerts.Exists(a => a.Id == alert.Id)) continue;
                _recentAlerts.Add(alert);
                added = true;
            }
            while (_recentAlerts.Count > 8) _recentAlerts.RemoveAt(0);

            if (added)
            {
                GuardAlert newest = _recentAlerts[_recentAlerts.Count - 1];
                if (newest.Id != _lastAlertId)
                {
                    _lastAlertId = newest.Id;
                    Notify(newest);
                }
            }

            RefreshGuardMenu();
        }

        private bool TryReadGuard(out List<GuardAlert> alerts, out bool anyLocked)
        {
            alerts = new List<GuardAlert>();
            anyLocked = false;
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + _pulse + "/api/guard");
                request.Timeout = 2000;
                request.Proxy = null;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    string json = reader.ReadToEnd();
                    JavaScriptSerializer serializer = new JavaScriptSerializer();
                    serializer.MaxJsonLength = 1024 * 1024;
                    Dictionary<string, object> data = serializer.Deserialize<Dictionary<string, object>>(json);
                    if (data == null) return false;

                    object rawAlerts;
                    if (data.TryGetValue("alerts", out rawAlerts) && rawAlerts is ArrayList)
                    {
                        foreach (object item in (ArrayList)rawAlerts)
                        {
                            Dictionary<string, object> map = item as Dictionary<string, object>;
                            if (map == null) continue;
                            string level = map.ContainsKey("level") ? Convert.ToString(map["level"]) : "";
                            // Only actionable alerts go to the tray; "clear"/"unlock"
                            // are noise the dashboard already shows.
                            if (level != "warn" && level != "lock") continue;
                            GuardAlert alert = new GuardAlert();
                            alert.Level = level;
                            alert.Key = map.ContainsKey("key") ? Convert.ToString(map["key"]) : "";
                            alert.Count = map.ContainsKey("count") ? ToInt(map["count"], 0) : 0;
                            alert.At = map.ContainsKey("at") ? ToLong(map["at"]) : 0;
                            alerts.Add(alert);
                        }
                    }

                    object rawGroups;
                    if (data.TryGetValue("groups", out rawGroups) && rawGroups is ArrayList)
                    {
                        foreach (object item in (ArrayList)rawGroups)
                        {
                            Dictionary<string, object> map = item as Dictionary<string, object>;
                            if (map == null) continue;
                            if (map.ContainsKey("status") && Convert.ToString(map["status"]) == "locked") anyLocked = true;
                        }
                    }
                    return true;
                }
            }
            catch
            {
                // Pulse not running / restarted: stay silent, retry on next tick.
                return false;
            }
        }

        private static long ToLong(object value)
        {
            long parsed;
            return long.TryParse(Convert.ToString(value), out parsed) ? parsed : 0;
        }

        /// <summary>
        /// Borderless, non-activating popup in the bottom-right corner.
        ///
        /// NotifyIcon.ShowBalloonTip alone is unreliable on Windows 10/11: without a
        /// registered AppUserModelID the shell silently drops the balloon, and Focus
        /// Assist does the same. Drawing our own window guarantees the warning is
        /// visible while still looking like a system notification.
        /// </summary>
        private sealed class ToastForm : Form
        {
            // NOTE: WS_EX_NOACTIVATE + ShowWithoutActivation looked correct on paper
            // but the window was created with Visible=true and never composited on
            // this box (screenshot showed the dashboard, logs said visible=True).
            // A plain TopMost form is less elegant (it takes focus for a moment) but
            // it actually renders, which is the whole point of a warning.
            internal ToastForm()
            {
                ShowInTaskbar = false;
                FormBorderStyle = FormBorderStyle.None;
                TopMost = true;
                StartPosition = FormStartPosition.Manual;
                BackColor = Color.FromArgb(32, 38, 48);
            }
        }

        private void ShowToast(string title, string body, bool isError, Action onClick)
        {
            try
            {
                ToastForm toast = new ToastForm();
                toast.Width = 380;
                toast.Height = 96;

                Rectangle work = Screen.PrimaryScreen.WorkingArea;
                toast.Location = new Point(work.Right - toast.Width - 14, work.Bottom - toast.Height - 14);

                Panel bar = new Panel();
                bar.Width = 4;
                bar.Height = toast.Height;
                bar.BackColor = isError ? Color.FromArgb(232, 94, 94) : Color.FromArgb(233, 176, 62);
                toast.Controls.Add(bar);

                Label head = new Label();
                head.Text = title;
                head.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
                head.ForeColor = isError ? Color.FromArgb(255, 176, 176) : Color.FromArgb(243, 207, 138);
                head.AutoSize = false;
                head.Bounds = new Rectangle(16, 10, toast.Width - 26, 24);
                toast.Controls.Add(head);

                Label text = new Label();
                text.Text = body;
                text.Font = new Font("Microsoft YaHei UI", 8.5F);
                text.ForeColor = Color.FromArgb(198, 210, 226);
                text.AutoSize = false;
                text.Bounds = new Rectangle(16, 36, toast.Width - 26, 50);
                toast.Controls.Add(text);

                EventHandler jump = delegate
                {
                    try { toast.Close(); } catch { }
                    if (onClick != null) onClick();
                };
                toast.Click += jump;
                head.Click += jump;
                text.Click += jump;

                System.Windows.Forms.Timer closer = new System.Windows.Forms.Timer();
                // 20s: long enough that a user glancing away still catches it;
                // clicking it opens the dashboard, so it does not need to be loud.
                closer.Interval = 20000;
                closer.Tick += delegate
                {
                    closer.Stop();
                    closer.Dispose();
                    try { toast.Close(); } catch { }
                };
                // Owned window: guaranteed to sit above the shell (whose embedded
                // Edge window is a separate HWND and would otherwise cover it).
                // When the shell is in the tray there is no visible owner, so the
                // TopMost flag carries the popup on its own.
                if (Visible && WindowState != FormWindowState.Minimized) toast.Owner = this;

                toast.Show();
                toast.BringToFront();
                closer.Start();
                Log(
                    "toast shown: " + title + " at " + toast.Bounds.X + "," + toast.Bounds.Y +
                    " " + toast.Bounds.Width + "x" + toast.Bounds.Height + " owner=" + (toast.Owner != null)
                );
            }
            catch (Exception ex)
            {
                Log("toast failed: " + ex.Message);
            }
        }

        private void Notify(GuardAlert alert)
        {
            if (_notifyMuted || _tray == null) return;
            // Do not let a burst of alerts strobe the tray.
            if ((DateTime.Now - _lastNotifyAt).TotalSeconds < 8) return;
            _lastNotifyAt = DateTime.Now;

            string key = ShortKey(alert.Key);
            string title;
            string body;
            ToolTipIcon icon;
            if (alert.Level == "lock")
            {
                title = "重复进程已锁死并清理";
                body = key + Environment.NewLine + "峰值 " + alert.Count + " 个，已保留最早一个、清理其余。";
                icon = ToolTipIcon.Error;
            }
            else
            {
                title = "检测到重复测试进程";
                body = key + Environment.NewLine + "当前 " + alert.Count + " 个；继续增加会自动锁死并清理。";
                icon = ToolTipIcon.Warning;
            }

            // Our own popup first (always visible), then the system balloon as a
            // bonus when the OS allows it.
            ShowToast(title, body, alert.Level == "lock", delegate
            {
                OpenExternal("http://127.0.0.1:" + _pulse + "/");
            });

            try
            {
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = body;
                _tray.BalloonTipIcon = icon;
                _tray.ShowBalloonTip(12000);
            }
            catch
            {
                /* balloon is best-effort */
            }
            Log("notify " + alert.Level + " " + key + " count=" + alert.Count);
        }

        /// <summary>"project|node --test x.mjs" -> "node --test x.mjs"（气泡宽度有限）</summary>
        private static string ShortKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "未知分组";
            int bar = key.IndexOf('|');
            string tail = bar >= 0 ? key.Substring(bar + 1) : key;
            return tail.Length > 42 ? tail.Substring(0, 42) + "…" : tail;
        }

        private void RefreshGuardMenu()
        {
            if (_guardMenu == null) return;
            try
            {
                _guardMenu.DropDownItems.Clear();
                if (_recentAlerts.Count == 0)
                {
                    ToolStripMenuItem empty = new ToolStripMenuItem("暂无告警");
                    empty.Enabled = false;
                    _guardMenu.DropDownItems.Add(empty);
                }
                else
                {
                    for (int i = _recentAlerts.Count - 1; i >= 0 && i >= _recentAlerts.Count - 5; i -= 1)
                    {
                        GuardAlert alert = _recentAlerts[i];
                        // DateTimeOffset.FromUnixTimeMilliseconds is .NET 4.6+;
                        // compute from the epoch so the build works on 4.0 too.
                        string stamp = alert.At > 0
                            ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                                .AddMilliseconds(alert.At).ToLocalTime().ToString("HH:mm:ss")
                            : "";
                        string text = (alert.Level == "lock" ? "[锁死] " : "[告警] ") + stamp + "  " + ShortKey(alert.Key);
                        ToolStripMenuItem item = new ToolStripMenuItem(text);
                        item.Click += delegate { OpenExternal("http://127.0.0.1:" + _pulse + "/"); };
                        _guardMenu.DropDownItems.Add(item);
                    }
                    _guardMenu.DropDownItems.Add(new ToolStripSeparator());
                    ToolStripMenuItem clear = new ToolStripMenuItem("清空列表");
                    clear.Click += delegate
                    {
                        _recentAlerts.Clear();
                        RefreshGuardMenu();
                    };
                    _guardMenu.DropDownItems.Add(clear);
                }
                _guardMenu.Text = _recentAlerts.Count > 0
                    ? "重复进程告警（" + _recentAlerts.Count + "）"
                    : "重复进程告警";
            }
            catch
            {
                /* menu refresh must never break polling */
            }
        }

        // ---------------------------------------------------------------- setup
        private static string ResolveKitRoot(string exeDir)
        {
            if (File.Exists(Path.Combine(exeDir, "config", "deploy.json"))) return exeDir;
            string parent = Path.GetDirectoryName(exeDir);
            if (!string.IsNullOrEmpty(parent) &&
                File.Exists(Path.Combine(parent, "config", "deploy.json"))) return parent;
            return exeDir;
        }

        private void ReadConfig()
        {
            try
            {
                string file = Path.Combine(_kitRoot, "config", "deploy.json");
                if (!File.Exists(file)) return;
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> cfg = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(file, Encoding.UTF8));
                if (cfg == null) return;

                if (cfg.ContainsKey("host") && cfg["host"] != null) _host = Convert.ToString(cfg["host"]);
                object ports;
                if (cfg.TryGetValue("ports", out ports))
                {
                    Dictionary<string, object> map = ports as Dictionary<string, object>;
                    if (map != null)
                    {
                        if (map.ContainsKey("codevault")) _port = ToInt(map["codevault"], _port);
                        if (map.ContainsKey("pulse")) _pulse = ToInt(map["pulse"], _pulse);
                        if (map.ContainsKey("tuner")) _tuner = ToInt(map["tuner"], _tuner);
                    }
                }
            }
            catch
            {
                // keep defaults; the page will still try 47910
            }
        }

        private static int ToInt(object value, int fallback)
        {
            int parsed;
            return int.TryParse(Convert.ToString(value), out parsed) && parsed > 0 ? parsed : fallback;
        }

        private void BuildTray()
        {
            _iconNormal = SystemIcons.Application;
            _iconAlert = SystemIcons.Warning;

            _tray = new NotifyIcon();
            _tray.Icon = _iconNormal;
            _tray.Text = "CodeBuddy 工作套件";
            _tray.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = new Font("Microsoft YaHei UI", 9F);

            ToolStripMenuItem show = new ToolStripMenuItem("显示主窗口");
            show.Font = new Font(menu.Font, FontStyle.Bold);
            show.Click += delegate { RestoreWindow(); };
            menu.Items.Add(show);

            menu.Items.Add(new ToolStripSeparator());

            // 重复测试进程告警：Pulse 的 /api/guard 轮询结果
            _guardMenu = new ToolStripMenuItem("重复进程告警");
            _guardMenu.DropDownItems.Add(new ToolStripMenuItem("暂无告警"));
            menu.Items.Add(_guardMenu);

            ToolStripMenuItem mute = new ToolStripMenuItem("静音气泡通知");
            mute.CheckOnClick = true;
            mute.Checked = _notifyMuted;
            mute.Click += delegate { _notifyMuted = mute.Checked; };
            menu.Items.Add(mute);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem index = new ToolStripMenuItem("在浏览器打开索引库");
            index.Click += delegate { OpenExternal("http://127.0.0.1:" + _port + "/"); };
            menu.Items.Add(index);

            ToolStripMenuItem pulse = new ToolStripMenuItem("打开进度看板 Pulse");
            pulse.Click += delegate { OpenExternal("http://127.0.0.1:" + _pulse + "/"); };
            menu.Items.Add(pulse);

            ToolStripMenuItem tuner = new ToolStripMenuItem("打开降温面板 Tuner");
            tuner.Click += delegate { OpenExternal("http://127.0.0.1:" + _tuner + "/"); };
            menu.Items.Add(tuner);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem hide = new ToolStripMenuItem("隐藏到托盘");
            hide.Click += delegate { HideToTray(false); };
            menu.Items.Add(hide);

            ToolStripMenuItem closeWin = new ToolStripMenuItem("关闭窗口（服务继续运行）");
            closeWin.Click += delegate
            {
                _stopServicesOnExit = false;
                Close();
            };
            menu.Items.Add(closeWin);

            ToolStripMenuItem stopAll = new ToolStripMenuItem("停止服务并退出");
            stopAll.Click += delegate
            {
                if (MessageBox.Show(
                        "将关闭窗口并停止索引库、Pulse、Tuner 三个服务。\n确认退出？",
                        "停止服务并退出",
                        MessageBoxButtons.OKCancel,
                        MessageBoxIcon.Question) != DialogResult.OK) return;
                _stopServicesOnExit = true;
                Close();
            };
            menu.Items.Add(stopAll);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exit = new ToolStripMenuItem("退出");
            exit.Click += delegate
            {
                _stopServicesOnExit = false;
                Close();
            };
            menu.Items.Add(exit);

            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { RestoreWindow(); };
            // Clicking the balloon jumps to the Pulse dashboard, which has the
            // full alert list and the 清理/解锁 buttons.
            _tray.BalloonTipClicked += delegate { OpenExternal("http://127.0.0.1:" + _pulse + "/"); };
            _tray.BalloonTipTitle = "CodeBuddy 工作套件";
            _tray.BalloonTipText = "窗口已收进托盘，双击图标恢复。";
        }

        // ---------------------------------------------------------------- lifecycle
        private void OnLoaded(object sender, EventArgs e)
        {
            Cursor = Cursors.WaitCursor;
            try
            {
                if (!PortOpen(_port))
                {
                    _tray.BalloonTipText = "正在启动索引库服务…";
                    StartLauncher("codevault");
                    WaitHealth("http://127.0.0.1:" + _port + "/api/health", 25);
                }
                LaunchWindow();
            }
            finally
            {
                Cursor = Cursors.Default;
                // 构造时 TopBar 尺寸还没定，按钮会落在负坐标；这里补一次布局
                LayoutTopBar();
                if (_topBar != null) _topBar.BringToFront();
            }
        }

        private void OnResized(object sender, EventArgs e)
        {
            if (WindowState == FormWindowState.Minimized) HideToTray(true);
            else if (_embedded) LayoutEmbedded();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_closingForReal) return;

            // 点窗体右上角 X：默认收进托盘，而不是退出（避免误关导致服务变孤岛）
            if (e.CloseReason == CloseReason.UserClosing && !_stopServicesOnExit)
            {
                e.Cancel = true;
                HideToTray(true);
                return;
            }

            _closingForReal = true;
            KillEdge();
            CleanupProfile();
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            if (_stopServicesOnExit) StopServices();
        }

        // ---------------------------------------------------------------- window
        private string EdgePath()
        {
            string[] candidates = new string[]
            {
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"
            };
            foreach (string path in candidates)
            {
                if (File.Exists(path)) return path;
            }
            return null;
        }

        private void LaunchWindow()
        {
            string url = "http://127.0.0.1:" + _port + "/";
            string edge = EdgePath();
            if (edge == null)
            {
                // 没有 Edge：退回默认浏览器，托盘仍可用
                OpenExternal(url);
                _tray.BalloonTipText = "未找到 Edge，已用默认浏览器打开。";
                _tray.ShowBalloonTip(4000);
                return;
            }

            string profile = Path.Combine(_kitRoot, "state", "shell-profile");
            try { Directory.CreateDirectory(profile); } catch { }

            ProcessStartInfo psi = new ProcessStartInfo(edge);
            psi.Arguments = string.Format(
                "--app={0} --window-size=1320,860 --user-data-dir=\"{1}\" --no-first-run --no-default-browser-check",
                url, profile);
            psi.UseShellExecute = false;

            _preExistingEdge = SnapshotEdgePids();

            try
            {
                _edge = Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "启动窗口失败：" + ex.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                OpenExternal(url);
                return;
            }

            // Edge 需要几百毫秒才建出主窗口，轮询取句柄
            _attachDeadline = DateTime.UtcNow.AddSeconds(15);
            _attachTimer = new System.Windows.Forms.Timer();
            _attachTimer.Interval = 300;
            _attachTimer.Tick += delegate
            {
                if (_pageHwnd == IntPtr.Zero)
                {
                    // Process.MainWindowHandle is unreliable here: the --app window
                    // usually belongs to a CHILD process of the one we spawned, so
                    // the wrapper reports 0. Enumerate windows owned by any msedge
                    // process that did not exist before our launch.
                    _pageHwnd = FindNewEdgeWindow(_preExistingEdge, "CodeVault");
                    if (_pageHwnd == IntPtr.Zero && _edge != null && !_edge.HasExited)
                    {
                        _edge.Refresh();
                        if (_edge.MainWindowHandle != IntPtr.Zero) _pageHwnd = _edge.MainWindowHandle;
                    }
                }

                if (_pageHwnd != IntPtr.Zero)
                {
                    _attachTimer.Stop();
                    Embed();
                }
                else if (DateTime.UtcNow > _attachDeadline)
                {
                    _attachTimer.Stop();
                    // 拿不到句柄：保持外部窗口，靠托盘控制
                    _tray.BalloonTipText = "窗口为独立模式，关闭请用托盘右键菜单。";
                    _tray.ShowBalloonTip(4000);
                }
            };
            _attachTimer.Start();
        }

        /** PIDs of msedge processes that existed before our launch. */
        private static HashSet<uint> SnapshotEdgePids()
        {
            HashSet<uint> set = new HashSet<uint>();
            try
            {
                foreach (Process p in Process.GetProcessesByName("msedge")) set.Add((uint)p.Id);
            }
            catch
            {
                /* permissions: treat as empty */
            }
            return set;
        }

        /**
         * Find the top-level, visible, titled window owned by an msedge process that
         * did not exist before our launch. This is what actually works for --app:
         * the window lives in a child process, not the one we started.
         */
        private static IntPtr FindNewEdgeWindow(HashSet<uint> before, string titlePart)
        {
            HashSet<uint> fresh = new HashSet<uint>();
            try
            {
                foreach (Process p in Process.GetProcessesByName("msedge"))
                {
                    uint pid = (uint)p.Id;
                    if (!before.Contains(pid)) fresh.Add(pid);
                }
            }
            catch
            {
                return IntPtr.Zero;
            }

            // When the user already has Edge open, `msedge --app` may be handed to an
            // existing process, so no new pid appears. Fall back to matching by title
            // across all Edge windows in that case.
            bool restrictToFresh = fresh.Count > 0;
            IntPtr found = IntPtr.Zero;

            EnumWindows(
                delegate(IntPtr hWnd, IntPtr lParam)
                {
                    uint pid;
                    GetWindowThreadProcessId(hWnd, out pid);
                    if (restrictToFresh && !fresh.Contains(pid)) return true;
                    if (!IsWindowVisible(hWnd)) return true;
                    StringBuilder title = new StringBuilder(512);
                    GetWindowText(hWnd, title, title.Capacity);
                    if (title.Length == 0) return true; // toolbars / message-only windows
                    if (!string.IsNullOrEmpty(titlePart) &&
                        title.ToString().IndexOf(titlePart, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        return true;
                    }

                    // 尺寸校验：--app 窗口是我们指定的 1320x860；用户自己的浏览窗口
                    // 尺寸随机，标题却可能同样是 "CodeVault"。不校验就会嵌错窗口。
                    RECT rect;
                    if (!GetWindowRect(hWnd, out rect)) return true;
                    int width = rect.Right - rect.Left;
                    int height = rect.Bottom - rect.Top;
                    if (width < 900 || height < 500) return true;

                    found = hWnd;
                    return false; // stop enumerating
                },
                IntPtr.Zero);

            if (found != IntPtr.Zero)
            {
                StringBuilder label = new StringBuilder(512);
                GetWindowText(found, label, label.Capacity);
                Log("window matched: " + label.ToString() + " fresh=" + fresh.Count);
            }
            return found;
        }

        private void Embed()
        {
            Log("embed attempt, hwnd=" + _pageHwnd + " style=" + GetWindowLong(_pageHwnd, GWL_STYLE));
            try
            {
                // Cross-process reparenting must happen while the child is hidden:
                // reparenting a visible Chrome window makes it pump layout messages
                // against a parent that lives in another process, which can hang.
                ShowWindow(_pageHwnd, SW_HIDE);
                Thread.Sleep(80);

                IntPtr previous = SetParent(_pageHwnd, Handle);
                int err = Marshal.GetLastWin32Error();
                Log("setparent prev=" + previous + " err=" + err);
                if (previous == IntPtr.Zero && err != 0)
                {
                    // Not fatal: stays an external window, the tray still controls it.
                    ShowWindow(_pageHwnd, SW_SHOW);
                    return;
                }

                int style = GetWindowLong(_pageHwnd, GWL_STYLE);
                style = (style & ~WS_CAPTION) & ~WS_THICKFRAME;
                style |= WS_CHILD;
                SetWindowLong(_pageHwnd, GWL_STYLE, style);

                // Chrome draws its own window edge through the extended style: leaving
                // WS_EX_CLIENTEDGE/WINDOWEDGE in place is what looked like "multiple
                // browser frames" inside the shell window.
                int exStyle = GetWindowLong(_pageHwnd, GWL_EXSTYLE);
                exStyle &= ~(WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE |
                             WS_EX_DLGMODALFRAME | WS_EX_APPWINDOW);
                SetWindowLong(_pageHwnd, GWL_EXSTYLE, exStyle);

                // Disable the non-client area and the Win11 rounded corners, otherwise
                // a 1px bright edge + shadow ring remain visible around the page.
                try
                {
                    int noNc = 2; // DWMNCRP_DISABLED
                    DwmSetWindowAttribute(_pageHwnd, DWMWA_NCRENDERING_POLICY, ref noNc, 4);
                    int noRound = 1; // DWMWCP_DONOTROUND
                    DwmSetWindowAttribute(_pageHwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref noRound, 4);
                }
                catch
                {
                    /* older Windows: cosmetic only */
                }

                ShowWindow(_pageHwnd, SW_SHOW);
                _embedded = true;
                LayoutEmbedded();
                Activate();
                Log("embed ok");
            }
            catch (Exception ex)
            {
                Log("embed exception: " + ex.Message);
                _embedded = false;
                try { ShowWindow(_pageHwnd, SW_SHOW); } catch { }
            }
        }

        // ================================================================ 顶部工具条

        /// <summary>
        /// 顶部工具条：状态文字 + 报错按钮（带计数徽章）+ 设置按钮。
        ///
        /// 为什么一定要单独留一条：嵌入的 Edge 窗口是另一个进程的 HWND，永远画在
        /// 本窗体控件之上。按钮如果放在页面区域里会被完全遮住，点了也没反应。
        /// </summary>
        private void BuildTopBar()
        {
            _topBar = new Panel();
            _topBar.Dock = DockStyle.Top;
            _topBar.Height = TOP_BAR_HEIGHT;
            _topBar.BackColor = Color.FromArgb(24, 32, 44);
            _topBar.Padding = new Padding(12, 0, 12, 0);
            _topBar.Resize += delegate { LayoutTopBar(); };
            Controls.Add(_topBar);

            _statusLabel = new Label();
            _statusLabel.AutoSize = false;
            _statusLabel.Bounds = new Rectangle(12, 6, 460, 30);
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            _statusLabel.ForeColor = Color.FromArgb(148, 164, 184);
            _statusLabel.Font = new Font("Microsoft YaHei UI", 8.5F);
            _statusLabel.Text = "正在启动…";
            _topBar.Controls.Add(_statusLabel);

            // 报错按钮
            _errorButton = new Panel();
            _errorButton.Size = new Size(84, 28);
            _errorButton.BackColor = Color.FromArgb(58, 72, 92);
            _errorButton.BorderStyle = BorderStyle.FixedSingle;
            _errorButton.Cursor = Cursors.Hand;
            _errorButton.Click += delegate { ShowErrorDialog(); };
            _topBar.Controls.Add(_errorButton);

            _errorText = new Label();
            _errorText.Text = "报错";
            _errorText.Dock = DockStyle.Fill;
            _errorText.TextAlign = ContentAlignment.MiddleCenter;
            _errorText.ForeColor = Color.FromArgb(150, 158, 170);
            _errorText.Font = new Font("Microsoft YaHei UI", 8.5F);
            _errorText.Click += delegate { ShowErrorDialog(); };
            _errorButton.Controls.Add(_errorText);

            // 数量徽章：贴在按钮右上角
            _errorBadge = new Label();
            _errorBadge.AutoSize = false;
            _errorBadge.Size = new Size(20, 15);
            _errorBadge.Location = new Point(64, -6);
            _errorBadge.TextAlign = ContentAlignment.MiddleCenter;
            _errorBadge.Font = new Font("Microsoft YaHei UI", 7.5F, FontStyle.Bold);
            _errorBadge.BackColor = Color.FromArgb(198, 58, 58);
            _errorBadge.ForeColor = Color.White;
            _errorBadge.Visible = false;
            _errorBadge.Cursor = Cursors.Hand;
            _errorBadge.Click += delegate { ShowErrorDialog(); };
            _errorButton.Controls.Add(_errorBadge);
            _errorBadge.BringToFront();

            // 设置按钮
            Button settings = new Button();
            settings.Text = "设置";
            settings.Size = new Size(66, 28);
            settings.FlatStyle = FlatStyle.Flat;
            settings.FlatAppearance.BorderSize = 0;
            settings.BackColor = Color.FromArgb(58, 74, 96);
            settings.FlatAppearance.BorderSize = 1;
            settings.FlatAppearance.BorderColor = Color.FromArgb(96, 118, 148);
            settings.ForeColor = Color.FromArgb(224, 234, 246);
            settings.Font = new Font("Microsoft YaHei UI", 8.5F);
            settings.Cursor = Cursors.Hand;
            settings.Tag = "settings";
            settings.Click += delegate { ShowSettings(); };
            _topBar.Controls.Add(settings);

            LayoutTopBar();
        }

        private void LayoutTopBar()
        {
            if (_topBar == null) return;

            // 构造阶段 Dock 还没算完，ClientSize 可能是 0，退回用窗体宽度
            int barWidth = _topBar.ClientSize.Width;
            if (barWidth < 100) barWidth = Math.Max(400, ClientSize.Width);
            int right = barWidth - 12;

            foreach (Control control in _topBar.Controls)
            {
                if (control is Button && "settings".Equals(control.Tag))
                {
                    control.Location = new Point(right - control.Width, (_topBar.Height - control.Height) / 2);
                    right -= control.Width + 8;
                }
            }
            if (_errorButton != null) _errorButton.Location = new Point(right - _errorButton.Width, (_topBar.Height - _errorButton.Height) / 2);
            if (_statusLabel != null) _statusLabel.Width = Math.Max(120, right - _errorButton.Width - 24);

            Log(
                "topbar layout: bar=" + barWidth + "x" + _topBar.Height +
                " status=" + (_statusLabel == null ? "-" : _statusLabel.Bounds.ToString()) +
                " error=" + (_errorButton == null ? "-" : _errorButton.Bounds.ToString()));
        }

        // ================================================================ 错误上报

        /// <summary>记录一条错误/告警，重复内容 30 秒内只记一次。</summary>
        private void AddError(string source, string message)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(delegate { AddError(source, message); })); } catch { }
                return;
            }

            for (int i = _errors.Count - 1; i >= 0 && i >= _errors.Count - 8; i -= 1)
            {
                ShellError old = _errors[i];
                if (old.Source == source && old.Message == message && (DateTime.Now - old.At).TotalSeconds < 30) return;
            }

            _errors.Add(new ShellError { At = DateTime.Now, Source = source, Message = message });
            while (_errors.Count > 200) _errors.RemoveAt(0);
            RefreshErrorBadge();
        }

        private void RefreshErrorBadge()
        {
            int count = _errors.Count;
            _errorBadge.Text = count > 99 ? "99+" : count.ToString();
            _errorBadge.Visible = count > 0;
            _errorBadge.Size = new Size(count > 9 ? 24 : 18, 15);

            // 平时灰、有错变红
            _errorButton.BackColor = count > 0 ? Color.FromArgb(150, 40, 40) : Color.FromArgb(46, 54, 66);
            _errorText.ForeColor = count > 0 ? Color.White : Color.FromArgb(150, 158, 170);
            _errorText.Text = count > 0 ? "报错" : "报错";
        }

        private void ShowErrorDialog()
        {
            Form dlg = new Form();
            dlg.Text = "错误与告警 · " + _errors.Count + " 条";
            dlg.Size = new Size(780, 480);
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.BackColor = Color.FromArgb(24, 30, 40);
            dlg.Font = new Font("Microsoft YaHei UI", 9F);

            TextBox box = new TextBox();
            box.Multiline = true;
            box.ReadOnly = true;
            box.ScrollBars = ScrollBars.Both;
            box.WordWrap = false;
            box.Dock = DockStyle.Fill;
            box.BackColor = Color.FromArgb(18, 24, 32);
            box.ForeColor = Color.FromArgb(208, 218, 230);
            box.BorderStyle = BorderStyle.None;
            box.Font = new Font("Consolas", 9F);

            if (_errors.Count == 0)
            {
                box.Text =
                    "暂无错误。\r\n\r\n" +
                    "这里会记录：\r\n" +
                    "  · 服务启动失败 / 健康检查连续失败\r\n" +
                    "  · 页面嵌入失败、Edge 找不到\r\n" +
                    "  · 主界面卡顿（超过 20 秒无响应）\r\n" +
                    "  · 未捕获的异常（来源会注明）\r\n";
            }
            else
            {
                StringBuilder sb = new StringBuilder();
                for (int i = _errors.Count - 1; i >= 0; i -= 1)
                {
                    ShellError item = _errors[i];
                    sb.AppendLine(item.At.ToString("MM-dd HH:mm:ss") + "   [" + item.Source + "]");
                    sb.AppendLine("    " + item.Message);
                    sb.AppendLine();
                }
                box.Text = sb.ToString();
            }
            dlg.Controls.Add(box);

            Panel bar = new Panel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = 46;
            bar.BackColor = Color.FromArgb(28, 36, 48);
            dlg.Controls.Add(bar);

            Button clear = new Button();
            clear.Text = "清空";
            clear.Size = new Size(80, 28);
            clear.Location = new Point(14, 9);
            clear.FlatStyle = FlatStyle.Flat;
            clear.FlatAppearance.BorderSize = 0;
            clear.BackColor = Color.FromArgb(46, 54, 66);
            clear.ForeColor = Color.FromArgb(206, 218, 232);
            clear.Click += delegate
            {
                _errors.Clear();
                RefreshErrorBadge();
                dlg.Close();
            };
            bar.Controls.Add(clear);

            Button copy = new Button();
            copy.Text = "复制全部";
            copy.Size = new Size(92, 28);
            copy.Location = new Point(102, 9);
            copy.FlatStyle = FlatStyle.Flat;
            copy.FlatAppearance.BorderSize = 0;
            copy.BackColor = Color.FromArgb(46, 54, 66);
            copy.ForeColor = Color.FromArgb(206, 218, 232);
            copy.Click += delegate
            {
                try { Clipboard.SetText(box.Text); } catch { }
            };
            bar.Controls.Add(copy);

            Button close = new Button();
            close.Text = "关闭";
            close.Size = new Size(80, 28);
            close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            close.Location = new Point(bar.ClientSize.Width - 94, 9);
            close.FlatStyle = FlatStyle.Flat;
            close.FlatAppearance.BorderSize = 0;
            close.BackColor = Color.FromArgb(46, 54, 66);
            close.ForeColor = Color.FromArgb(206, 218, 232);
            close.Click += delegate { dlg.Close(); };
            bar.Controls.Add(close);

            dlg.ShowDialog(this);
        }

        // ================================================================ 健康自检 / 防卡死

        private void StartHealthWatch()
        {
            _lastHealthTick = DateTime.Now;
            _healthTimer = new System.Windows.Forms.Timer();
            _healthTimer.Interval = 8000;
            _healthTimer.Tick += delegate { CheckHealth(); };
            _healthTimer.Start();

            // 首轮延后一点，避免和服务启动抢时间
            System.Windows.Forms.Timer kick = new System.Windows.Forms.Timer();
            kick.Interval = 4000;
            kick.Tick += delegate
            {
                kick.Stop();
                kick.Dispose();
                CheckHealth();
            };
            kick.Start();
        }

        private void CheckHealth()
        {
            DateTime now = DateTime.Now;

            // 防卡死：定时器本应每 8 秒触发；间隔明显超出说明 UI 线程被阻塞过
            double gap = (now - _lastHealthTick).TotalSeconds;
            _lastHealthTick = now;
            if (gap > 20)
            {
                AddError("界面", "主线程阻塞约 " + Math.Round(gap) + " 秒，现已恢复（可能是页面渲染或大量文件操作）");
            }

            int down = 0;
            if (!ProbeHealth(_port, "/api/health")) down += 1;
            if (!ProbeHealth(_pulse, "/api/health")) down += 1;
            if (!ProbeHealth(_tuner, "/api/health")) down += 1;

            if (down > 0)
            {
                _healthFailStreak += 1;
                if (_healthFailStreak == 3)
                {
                    AddError("服务", down + " 个服务连续 3 次探测无响应，可在「设置」中重启服务");
                }
            }
            else
            {
                if (_healthFailStreak >= 3) AddError("服务", "服务已恢复响应");
                _healthFailStreak = 0;
            }

            UpdateStatusText(down);
        }

        private bool ProbeHealth(int port, string path)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + path);
                request.Timeout = 2000;
                request.ReadWriteTimeout = 2000;
                request.Proxy = null;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    return response.StatusCode == HttpStatusCode.OK;
                }
            }
            catch
            {
                return false;
            }
        }

        private void UpdateStatusText(int down)
        {
            if (_statusLabel == null) return;
            if (down == _lastDownCount) return;
            _lastDownCount = down;

            if (down == 0)
            {
                _statusLabel.Text = string.Format(
                    "索引库 {0} · 看板 {1} · 降温 {2}　全部在线", _port, _pulse, _tuner);
                _statusLabel.ForeColor = Color.FromArgb(132, 176, 148);
            }
            else
            {
                _statusLabel.Text = string.Format(
                    "{0} 个服务无响应（{1} / {2} / {3}）", down, _port, _pulse, _tuner);
                _statusLabel.ForeColor = Color.FromArgb(214, 148, 120);
            }
        }

        // ================================================================ 设置

        private void ShowSettings()
        {
            Form dlg = new Form();
            dlg.Text = "设置 · CodeBuddy 工作套件";
            dlg.Size = new Size(660, 640);
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.MaximizeBox = false;
            dlg.MinimizeBox = false;
            dlg.BackColor = Color.FromArgb(24, 30, 40);
            dlg.ForeColor = Color.FromArgb(210, 222, 234);
            dlg.Font = new Font("Microsoft YaHei UI", 9F);

            Label head = new Label();
            head.Text = "服务端口";
            head.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            head.ForeColor = Color.FromArgb(140, 190, 230);
            head.Bounds = new Rectangle(24, 18, 300, 24);
            dlg.Controls.Add(head);

            Label hint = new Label();
            hint.Text = "改完保存会自动重启三个服务；端口被占用时会提示换一个。";
            hint.ForeColor = Color.FromArgb(130, 144, 162);
            hint.Bounds = new Rectangle(24, 44, 600, 20);
            dlg.Controls.Add(hint);

            NumericUpDown cv = MakePortBox(dlg, 24, 74, "索引库", _port);
            NumericUpDown pu = MakePortBox(dlg, 24, 112, "看板", _pulse);
            NumericUpDown tu = MakePortBox(dlg, 24, 150, "降温", _tuner);

            Label hostLabel = new Label();
            hostLabel.Text = "监听地址";
            hostLabel.Bounds = new Rectangle(24, 192, 80, 22);
            dlg.Controls.Add(hostLabel);

            ComboBox hostBox = new ComboBox();
            hostBox.DropDownStyle = ComboBoxStyle.DropDownList;
            hostBox.Bounds = new Rectangle(112, 190, 300, 24);
            hostBox.Items.Add("127.0.0.1（仅本机）");
            hostBox.Items.Add("0.0.0.0（局域网可访问）");
            hostBox.SelectedIndex = (ReadHostFromConfig() == "0.0.0.0") ? 1 : 0;
            dlg.Controls.Add(hostBox);

            // ---- 可外接内容（只读展示，告诉用户都能接什么）
            Label extHead = new Label();
            extHead.Text = "可外接内容";
            extHead.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            extHead.ForeColor = Color.FromArgb(140, 190, 230);
            extHead.Bounds = new Rectangle(24, 230, 300, 24);
            dlg.Controls.Add(extHead);

            TextBox ext = new TextBox();
            ext.Multiline = true;
            ext.ReadOnly = true;
            ext.ScrollBars = ScrollBars.Vertical;
            ext.BorderStyle = BorderStyle.None;
            ext.BackColor = Color.FromArgb(18, 24, 32);
            ext.ForeColor = Color.FromArgb(196, 208, 222);
            ext.Font = new Font("Consolas", 8.5F);
            ext.Bounds = new Rectangle(24, 258, 596, 190);
            ext.Text = BuildExternalEndpointsText();
            dlg.Controls.Add(ext);

            // ---- 环境检测输出
            Label hcHead = new Label();
            hcHead.Text = "环境检测";
            hcHead.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            hcHead.ForeColor = Color.FromArgb(140, 190, 230);
            hcHead.Bounds = new Rectangle(24, 458, 200, 24);
            dlg.Controls.Add(hcHead);

            TextBox hcOut = new TextBox();
            hcOut.Multiline = true;
            hcOut.ReadOnly = true;
            hcOut.ScrollBars = ScrollBars.Vertical;
            hcOut.BorderStyle = BorderStyle.None;
            hcOut.BackColor = Color.FromArgb(18, 24, 32);
            hcOut.ForeColor = Color.FromArgb(170, 184, 200);
            hcOut.Font = new Font("Consolas", 8.5F);
            hcOut.Bounds = new Rectangle(24, 484, 596, 66);
            hcOut.Text = "点击右侧「运行检测」检查文件与功能完整性。";
            dlg.Controls.Add(hcOut);

            Button runCheck = new Button();
            runCheck.Text = "运行检测";
            runCheck.Size = new Size(96, 26);
            runCheck.Location = new Point(424, 456);
            runCheck.FlatStyle = FlatStyle.Flat;
            runCheck.FlatAppearance.BorderSize = 0;
            runCheck.BackColor = Color.FromArgb(46, 64, 86);
            runCheck.ForeColor = Color.FromArgb(210, 222, 234);
            runCheck.Click += delegate
            {
                hcOut.Text = "正在检测…";
                runCheck.Enabled = false;
                try
                {
                    hcOut.Text = RunHealthCheck();
                }
                catch (Exception ex)
                {
                    hcOut.Text = "检测失败：" + ex.Message;
                    AddError("环境检测", ex.Message);
                }
                runCheck.Enabled = true;
            };
            dlg.Controls.Add(runCheck);

            Button openFolder = new Button();
            openFolder.Text = "打开目录";
            openFolder.Size = new Size(88, 26);
            openFolder.Location = new Point(528, 456);
            openFolder.FlatStyle = FlatStyle.Flat;
            openFolder.FlatAppearance.BorderSize = 0;
            openFolder.BackColor = Color.FromArgb(46, 54, 66);
            openFolder.ForeColor = Color.FromArgb(210, 222, 234);
            openFolder.Click += delegate { try { Process.Start("explorer.exe", _kitRoot); } catch { } };
            dlg.Controls.Add(openFolder);

            // ---- 底部按钮
            Button save = new Button();
            save.Text = "保存并重启";
            save.Size = new Size(112, 30);
            save.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            save.Location = new Point(dlg.ClientSize.Width - 250, dlg.ClientSize.Height - 48);
            save.FlatStyle = FlatStyle.Flat;
            save.FlatAppearance.BorderSize = 0;
            save.BackColor = Color.FromArgb(40, 92, 140);
            save.ForeColor = Color.White;
            save.Click += delegate
            {
                string error;
                if (!SaveConfig((int)cv.Value, (int)pu.Value, (int)tu.Value,
                        hostBox.SelectedIndex == 1 ? "0.0.0.0" : "127.0.0.1", out error))
                {
                    MessageBox.Show(this, "保存失败：" + error, "设置", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    AddError("设置", "保存配置失败：" + error);
                    return;
                }
                dlg.Close();
                RestartServices();
            };
            dlg.Controls.Add(save);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.Size = new Size(88, 30);
            cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            cancel.Location = new Point(dlg.ClientSize.Width - 128, dlg.ClientSize.Height - 48);
            cancel.FlatStyle = FlatStyle.Flat;
            cancel.FlatAppearance.BorderSize = 0;
            cancel.BackColor = Color.FromArgb(46, 54, 66);
            cancel.ForeColor = Color.FromArgb(210, 222, 234);
            cancel.Click += delegate { dlg.Close(); };
            dlg.Controls.Add(cancel);

            dlg.ShowDialog(this);
        }

        private NumericUpDown MakePortBox(Form dlg, int x, int y, string label, int value)
        {
            Label tag = new Label();
            tag.Text = label;
            tag.Bounds = new Rectangle(x, y + 3, 80, 22);
            dlg.Controls.Add(tag);

            NumericUpDown box = new NumericUpDown();
            box.Minimum = 1024;
            box.Maximum = 65535;
            box.Value = value;
            box.Bounds = new Rectangle(x + 88, y, 120, 24);
            dlg.Controls.Add(box);
            return box;
        }

        private string ReadHostFromConfig()
        {
            try
            {
                string path = Path.Combine(_kitRoot, "config", "deploy.json");
                if (!File.Exists(path)) return "127.0.0.1";
                string raw = File.ReadAllText(path, Encoding.UTF8).TrimStart('\uFEFF');
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> map = ser.Deserialize<Dictionary<string, object>>(raw);
                object host;
                if (map != null && map.TryGetValue("host", out host) && host != null)
                {
                    return Convert.ToString(host);
                }
            }
            catch
            {
                /* fall through */
            }
            return "127.0.0.1";
        }

        private string BuildExternalEndpointsText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("浏览器 / 其他设备可访问的地址：");
            sb.AppendLine("  索引库页面    http://127.0.0.1:" + _port + "/");
            sb.AppendLine("  索引库接口    http://127.0.0.1:" + _port + "/api/repos");
            sb.AppendLine("              http://127.0.0.1:" + _port + "/api/scripts");
            sb.AppendLine("  看板页面    http://127.0.0.1:" + _pulse + "/");
            sb.AppendLine("  看板接口    http://127.0.0.1:" + _pulse + "/api/health");
            sb.AppendLine("              http://127.0.0.1:" + _pulse + "/api/guard");
            sb.AppendLine("  降温页面    http://127.0.0.1:" + _tuner + "/");
            sb.AppendLine("  降温接口    http://127.0.0.1:" + _tuner + "/api/health");
            sb.AppendLine("");
            sb.AppendLine("接入方式：把上面的监听地址改成 0.0.0.0 后，同一局域网内用本机 IP 访问。");
            sb.AppendLine("数据接入：把项目目录登记进仓库清单即可出现在索引库：");
            sb.AppendLine("  node apps\\scriptvault\\tools\\adopt-project.mjs \"<项目目录>\" --name <简称>");
            sb.AppendLine("进度接入：用 pulse 包装命令即可上报：");
            sb.AppendLine("  node apps\\pulse\\cli\\pulse.js run -- <命令>");
            sb.AppendLine("");
            sb.AppendLine("规则分发：安装时写入 " + _kitRoot + "\\AGENTS.md 及 CodeBuddy / Cursor /");
            sb.AppendLine("Claude / Codex / Cline / Windsurf 的约定位置，可重复执行 install-rules.ps1。");
            return sb.ToString();
        }

        private string RunHealthCheck()
        {
            string script = Path.Combine(_kitRoot, "tools", "health-check.ps1");
            if (!File.Exists(script)) return "未找到 tools\\health-check.ps1";

            ProcessStartInfo psi = new ProcessStartInfo(
                "powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -Target \"" + _kitRoot + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            try
            {
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(30000);
                    return string.IsNullOrEmpty(output) ? "（无输出）" : output;
                }
            }
            catch (Exception ex)
            {
                return "执行失败：" + ex.Message;
            }
        }

        private bool SaveConfig(int codevault, int pulse, int tuner, string host, out string error)
        {
            error = "";
            try
            {
                string path = Path.Combine(_kitRoot, "config", "deploy.json");
                string raw = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).TrimStart('\uFEFF') : "{}";

                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> map = ser.Deserialize<Dictionary<string, object>>(raw);
                if (map == null) map = new Dictionary<string, object>();

                map["host"] = host;
                map["lan"] = host == "0.0.0.0";

                object existingPorts;
                Dictionary<string, object> ports;
                if (map.TryGetValue("ports", out existingPorts) && existingPorts is Dictionary<string, object>)
                {
                    ports = (Dictionary<string, object>)existingPorts;
                }
                else
                {
                    ports = new Dictionary<string, object>();
                    map["ports"] = ports;
                }
                ports["codevault"] = codevault;
                ports["pulse"] = pulse;
                ports["tuner"] = tuner;

                // 无 BOM 写入：Node 的 JSON.parse 不接受 BOM
                File.WriteAllText(path, ser.Serialize(map), new UTF8Encoding(false));

                _port = codevault;
                _pulse = pulse;
                _tuner = tuner;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private void RestartServices()
        {
            try
            {
                AddError("设置", "正在按新端口重启服务…");
                RunNodeTool("stop.mjs", "all");
                Thread.Sleep(800);
                RunNodeTool("launch.mjs", "all");
                _healthFailStreak = 0;
                _lastDownCount = -1;
                CheckHealth();
            }
            catch (Exception ex)
            {
                AddError("重启", ex.Message);
            }
        }

        private void RunNodeTool(string script, string args)
        {
            string node = Path.Combine(_kitRoot, "runtime", "node.exe");
            if (!File.Exists(node)) node = "node";

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = node;
            psi.Arguments = "\"" + Path.Combine(_kitRoot, "tools", script) + "\" " + args;
            psi.WorkingDirectory = _kitRoot;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            using (Process p = Process.Start(psi))
            {
                p.WaitForExit(25000);
            }
        }

        /// <summary>profile 复用，退出时不删（保留缓存，下次启动更快）。</summary>
        private void CleanupProfile()
        {
            // 固定 profile 刻意保留：删掉会导致每次冷启动都要重建上百 MB 缓存，
            // 既慢又让首页首次渲染出现空白。
        }

        /// <summary>
        /// 结束占用本 profile 的残留 Edge 进程。
        /// 上次异常退出留下的窗口会让 `--app` 请求转发过去，结果嵌错窗口。
        /// </summary>
        private static void KillStaleEdge(string profile)
        {
            try
            {
                string command =
                    "Get-CimInstance Win32_Process -Filter \"Name='msedge.exe'\" | " +
                    "Where-Object { $_.CommandLine -like '*" + profile.Replace("'", "''") + "*' } | " +
                    "ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue } catch {} }";

                ProcessStartInfo psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"" + command + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(6000);
                }
                Thread.Sleep(400);
            }
            catch
            {
                /* best effort: a stale window only costs an extra match, not a crash */
            }
        }

        private void LayoutEmbedded()
        {
            if (!_embedded || _pageHwnd == IntPtr.Zero) return;

            // Leave room for the top bar. The embedded Edge window is another
            // process's HWND, so it always paints above our own controls — a button
            // placed inside its rectangle would be invisible. Keeping the top
            // strip free is what makes the buttons usable at all.
            int top = TOP_BAR_HEIGHT;
            int w = Math.Max(1, ClientSize.Width);
            int h = Math.Max(1, ClientSize.Height - top);

            // MoveWindow alone leaves the child with its old frame metrics, so the
            // page never quite fills the client area. SWP_FRAMECHANGED forces the
            // child to recompute its non-client area after the style change.
            SetWindowPos(
                _pageHwnd,
                IntPtr.Zero,
                0,
                top,
                w,
                h,
                SWP_FRAMECHANGED | SWP_SHOWWINDOW | SWP_NOACTIVATE);

            // Belt and braces: the client area of the child must equal what we asked.
            MoveWindow(_pageHwnd, 0, top, w, h, true);

            // 移动跨进程窗口后它不会自己重画，强制一次，否则页面停在黑屏
            try
            {
                InvalidateRect(_pageHwnd, IntPtr.Zero, true);
                UpdateWindow(_pageHwnd);
            }
            catch
            {
                /* cosmetic */
            }

            if (_topBar != null) _topBar.BringToFront();
        }

        /// <summary>Diagnostics: a silent restore failure is impossible to debug otherwise.</summary>
        internal static void Log(string message)
        {
            try
            {
                string file = Path.Combine(Path.GetTempPath(), "kit-shell.log");
                File.AppendAllText(
                    file,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch
            {
                /* logging must never break the window */
            }
        }

        private void HideToTray(bool notify)
        {
            // Order matters: clear the minimized state BEFORE hiding, otherwise the
            // window keeps the Win32 minimized flag and Show() only makes it
            // "visible but still minimized".
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            ShowInTaskbar = false;
            Hide();
            // The embedded page is another process's window: hiding the parent form
            // does NOT hide it, so a full-screen page would stay on screen after
            // "minimize to tray". Hide it explicitly.
            if (_embedded && _pageHwnd != IntPtr.Zero) ShowWindow(_pageHwnd, SW_HIDE);
            Log("hide to tray, visible=" + Visible);
            if (notify && _tray != null)
            {
                _tray.BalloonTipText = "已最小化到托盘，双击图标恢复，右键可关闭。";
                _tray.ShowBalloonTip(2500);
            }
        }

        private void RestoreWindow()
        {
            Log("restore requested, visible=" + Visible + " state=" + WindowState);
            ShowInTaskbar = true;
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show();
            WindowState = FormWindowState.Normal;
            BringToFront();
            Activate();
            if (_embedded && _pageHwnd != IntPtr.Zero)
            {
                ShowWindow(_pageHwnd, SW_SHOW);
                ShowWindow(_pageHwnd, SW_RESTORE);
                SetForegroundWindow(_pageHwnd);
            }
            LayoutEmbedded();
            Log("restore done, visible=" + Visible + " state=" + WindowState);
        }

        private void OpenExternal(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                MessageBox.Show(this, "无法打开浏览器：" + url, "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        // ---------------------------------------------------------------- services
        private static bool PortOpen(int port)
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult ar = client.BeginConnect("127.0.0.1", port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(600)) return false;
                    client.EndConnect(ar);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private void StartLauncher(string target)
        {
            try
            {
                string node = Path.Combine(_kitRoot, "runtime", "node.exe");
                if (!File.Exists(node)) node = "node";
                string script = Path.Combine(_kitRoot, "tools", "launch.mjs");
                if (!File.Exists(script)) return;

                ProcessStartInfo psi = new ProcessStartInfo(node);
                psi.Arguments = "\"" + script + "\" " + target + " --no-open";
                psi.WorkingDirectory = _kitRoot;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process.Start(psi);
            }
            catch
            {
                // the page will show its own error; nothing else to do here
            }
        }

        private void WaitHealth(string url, int seconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    System.Net.HttpWebRequest request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                    request.Timeout = 1200;
                    using (System.Net.HttpWebResponse response = (System.Net.HttpWebResponse)request.GetResponse())
                    {
                        if ((int)response.StatusCode == 200) return;
                    }
                }
                catch
                {
                    // not ready yet
                }
                Thread.Sleep(400);
            }
        }

        private void StopServices()
        {
            try
            {
                string node = Path.Combine(_kitRoot, "runtime", "node.exe");
                if (!File.Exists(node)) node = "node";
                string script = Path.Combine(_kitRoot, "tools", "stop.mjs");
                if (!File.Exists(script)) return;

                ProcessStartInfo psi = new ProcessStartInfo(node);
                psi.Arguments = "\"" + script + "\" all";
                psi.WorkingDirectory = _kitRoot;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process proc = Process.Start(psi))
                {
                    if (proc != null) proc.WaitForExit(8000);
                }
            }
            catch
            {
                // best effort: the uninstall script can still stop things by port
            }
        }

        private void KillEdge()
        {
            try
            {
                if (_edge != null && !_edge.HasExited)
                {
                    _edge.Kill();
                    _edge.WaitForExit(3000);
                }
            }
            catch
            {
                // already gone
            }
        }
    }
}
