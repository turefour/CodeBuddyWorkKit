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
        private bool _closingForReal;
        private bool _stopServicesOnExit;
        private System.Windows.Forms.Timer _attachTimer;
        private DateTime _attachDeadline;
        private HashSet<uint> _preExistingEdge = new HashSet<uint>();

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

        private void LayoutEmbedded()
        {
            if (!_embedded || _pageHwnd == IntPtr.Zero) return;
            // MoveWindow alone leaves the child with its old frame metrics, so the
            // page never quite fills the client area. SWP_FRAMECHANGED forces the
            // child to recompute its non-client area after the style change.
            SetWindowPos(
                _pageHwnd,
                IntPtr.Zero,
                0,
                0,
                ClientSize.Width,
                ClientSize.Height,
                SWP_FRAMECHANGED | SWP_SHOWWINDOW | SWP_NOACTIVATE);

            // Belt and braces: the client area of the child must equal what we asked.
            MoveWindow(_pageHwnd, 0, 0, ClientSize.Width, ClientSize.Height, true);
        }

        /// <summary>Diagnostics: a silent restore failure is impossible to debug otherwise.</summary>
        private static void Log(string message)
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
