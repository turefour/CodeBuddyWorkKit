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
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
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

        private const int GWL_STYLE = -16;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_CHILD = 0x40000000;
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const int SW_MAXIMIZE = 3;
        private const int SW_RESTORE = 9;

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

        internal ShellForm(string[] args)
        {
            _exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            _kitRoot = ResolveKitRoot(_exeDir);
            ReadConfig();

            Text = "CodeBuddy 工作套件";
            Width = 1360;
            Height = 900;
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
            _tray = new NotifyIcon();
            _tray.Icon = SystemIcons.Application;
            _tray.Text = "CodeBuddy 工作套件";
            _tray.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = new Font("Microsoft YaHei UI", 9F);

            ToolStripMenuItem show = new ToolStripMenuItem("显示主窗口");
            show.Font = new Font(menu.Font, FontStyle.Bold);
            show.Click += delegate { RestoreWindow(); };
            menu.Items.Add(show);

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
            _attachDeadline = DateTime.UtcNow.AddSeconds(12);
            _attachTimer = new System.Windows.Forms.Timer();
            _attachTimer.Interval = 300;
            _attachTimer.Tick += delegate
            {
                if (_pageHwnd == IntPtr.Zero && _edge != null && !_edge.HasExited)
                {
                    _edge.Refresh();
                    if (_edge.MainWindowHandle != IntPtr.Zero) _pageHwnd = _edge.MainWindowHandle;
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

        private void Embed()
        {
            try
            {
                IntPtr result = SetParent(_pageHwnd, Handle);
                if (result == IntPtr.Zero && _pageHwnd != IntPtr.Zero)
                {
                    // SetParent 失败：保持外部窗口
                    return;
                }

                int style = GetWindowLong(_pageHwnd, GWL_STYLE);
                style = (style & ~WS_CAPTION) & ~WS_THICKFRAME;
                style |= WS_CHILD;
                SetWindowLong(_pageHwnd, GWL_STYLE, style);

                ShowWindow(_pageHwnd, SW_SHOW);
                _embedded = true;
                LayoutEmbedded();
                Activate();
            }
            catch
            {
                _embedded = false;
            }
        }

        private void LayoutEmbedded()
        {
            if (!_embedded || _pageHwnd == IntPtr.Zero) return;
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
