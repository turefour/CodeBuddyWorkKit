using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace WorkKit.Native
{
    internal static class NativeProgram
    {
        private static Mutex _single;
        private static NotifyIcon _tray;
        private static EventWaitHandle _showSignal;
        private static bool _allowClose;

        [STAThread]
        internal static int Main(string[] args)
        {
            bool created;
            _single = new Mutex(true, "CodeBuddyWorkKit.Native", out created);

            if (!created)
            {
                // 已经在跑：把窗口叫出来，然后安静退出
                try
                {
                    EventWaitHandle signal = EventWaitHandle.OpenExisting("CodeBuddyWorkKit.Native.Show");
                    signal.Set();
                    signal.Close();
                }
                catch
                {
                    MessageBox.Show(
                        "工作套件已经在运行，请查看右下角托盘图标。",
                        "CodeBuddy 工作套件",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                return 0;
            }

            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "CodeBuddyWorkKit.Native.Show");

            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                Log("UI exception: " + e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Log("unhandled: " + e.ExceptionObject);
            };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            NativeForm form = new NativeForm(args);
            BuildTray(form);
            WatchSignal(form);

            Application.Run(form);

            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            return 0;
        }

        private static void BuildTray(NativeForm form)
        {
            _tray = new NotifyIcon();
            _tray.Icon = SystemIcons.Application;
            _tray.Text = "CodeBuddy 工作套件（原生版）";
            _tray.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = Theme.Body;

            ToolStripMenuItem show = new ToolStripMenuItem("显示主窗口");
            show.Font = Theme.SmallBold;
            show.Click += delegate { Restore(form); };
            menu.Items.Add(show);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem web = new ToolStripMenuItem("在浏览器打开索引库");
            web.Click += delegate
            {
                try { Process.Start(form.IndexUrl); }
                catch { }
            };
            menu.Items.Add(web);

            ToolStripMenuItem folder = new ToolStripMenuItem("打开部署目录");
            folder.Click += delegate
            {
                try { Process.Start("explorer.exe", form.KitRoot); }
                catch { }
            };
            menu.Items.Add(folder);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem hide = new ToolStripMenuItem("隐藏到托盘");
            hide.Click += delegate { HideToTray(form); };
            menu.Items.Add(hide);

            ToolStripMenuItem exit = new ToolStripMenuItem("退出");
            exit.Click += delegate
            {
                _allowClose = true;
                form.Close();
            };
            menu.Items.Add(exit);

            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { Restore(form); };

            // 最小化 / 点关闭都收进托盘，避免误关
            form.Resize += delegate
            {
                if (form.WindowState == FormWindowState.Minimized) HideToTray(form);
            };
            form.FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (_allowClose) return;
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    HideToTray(form);
                }
            };
        }

        private static void WatchSignal(NativeForm form)
        {
            Thread watcher = new Thread(delegate()
            {
                while (true)
                {
                    try
                    {
                        if (_showSignal == null) break;
                        _showSignal.WaitOne();
                    }
                    catch
                    {
                        break;
                    }
                    if (form.IsDisposed) break;
                    try { form.BeginInvoke(new Action(delegate { Restore(form); })); }
                    catch { break; }
                }
            });
            watcher.IsBackground = true;
            watcher.Start();
        }

        private static void HideToTray(NativeForm form)
        {
            form.ShowInTaskbar = false;
            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
            form.Hide();
            if (_tray != null)
            {
                _tray.BalloonTipText = "已收进托盘，双击图标恢复。";
                _tray.ShowBalloonTip(2000);
            }
        }

        private static void Restore(NativeForm form)
        {
            form.ShowInTaskbar = true;
            form.Show();
            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
            form.Activate();
            form.BringToFront();
        }

        private static void Log(string message)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "kit-native.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch
            {
                /* 日志失败不能影响主流程 */
            }
        }
    }
}
