// ============================================================================
// CodeBuddy 工作套件 - 安装程序
//
// 交互流程：说明功能 → 检测环境 → 用户确认 → 部署 → 自检 → 显示结果
// 自检不通过时不会显示"部署完成"，而是列出失败项。
//
// 静默模式（供自动化验证）：
//   installer.exe --silent --target "D:\Kit" --port 47910 --pulse 47821 --tuner 47822 --lan
//   退出码：0 成功 / 1 检测或自检失败 / 2 参数或环境错误
//
// 编译（系统自带，无需安装任何东西）：
//   csc.exe /target:winexe /out:installer.exe /r:System.Windows.Forms.dll
//           /r:System.Drawing.dll /r:System.Web.Extensions.dll Installer.cs
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace CodeBuddyWorkKit
{
    internal static class Program
    {
        internal static string PayloadDir;
        internal static string ToolsDir;
        internal static string PackageVersion = "0.0.0";
        internal static bool Silent;
        internal static string TargetDir;
        internal static int PortCodevault = 47910;
        internal static int PortPulse = 47821;
        internal static int PortTuner = 47822;
        internal static bool AllowLan;
        internal static bool NoDesktop;

        [STAThread]
        internal static int Main(string[] args)
        {
            string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            PayloadDir = Path.Combine(exeDir, "payload");
            ToolsDir = Path.Combine(PayloadDir, "tools");
            TargetDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodeBuddyWorkKit");

            if (!ParseArgs(args)) return 2;

            if (!Directory.Exists(PayloadDir))
            {
                Console.Error.WriteLine("安装包不完整：找不到 payload 目录（应与 installer.exe 同级）");
                return 2;
            }
            if (!File.Exists(Path.Combine(ToolsDir, "deploy.ps1")))
            {
                Console.Error.WriteLine("安装包不完整：找不到 payload\\tools\\deploy.ps1");
                return 2;
            }

            // The version shown to the user and compared against an existing
            // install comes from payload\VERSION, never from a hard-coded string.
            try
            {
                string versionFile = Path.Combine(PayloadDir, "VERSION");
                if (File.Exists(versionFile))
                {
                    string text = File.ReadAllText(versionFile, Encoding.UTF8).Trim();
                    if (text.Length > 0) PackageVersion = text;
                }
            }
            catch
            {
                /* keep 0.0.0 */
            }

            if (Silent) return RunSilent();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new InstallerForm());
            return 0;
        }

        private static bool ParseArgs(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--silent") Silent = true;
                else if (a == "--lan") AllowLan = true;
                else if (a == "--no-desktop") NoDesktop = true;
                else if (a == "--target" && i + 1 < args.Length) TargetDir = args[++i];
                else if (a == "--port" && i + 1 < args.Length) PortCodevault = ToInt(args[++i], 47910);
                else if (a == "--pulse" && i + 1 < args.Length) PortPulse = ToInt(args[++i], 47821);
                else if (a == "--tuner" && i + 1 < args.Length) PortTuner = ToInt(args[++i], 47822);
                else if (a == "--help" || a == "-h")
                {
                    Console.WriteLine("installer.exe [--silent] [--target DIR] [--port N] [--pulse N] [--tuner N] [--lan] [--no-desktop]");
                    return false;
                }
            }
            return true;
        }

        private static int ToInt(string text, int fallback)
        {
            int value;
            return int.TryParse(text, out value) && value > 0 && value < 65536 ? value : fallback;
        }

        private static int RunSilent()
        {
            Console.WriteLine("== 环境检测 ==");
            PsResult env = Ps.Run("check-env.ps1", BuildEnvArgs(), Console.WriteLine, true);
            Console.WriteLine(env.StdOut);
            if (env.ExitCode != 0)
            {
                Console.Error.WriteLine("环境检测未通过，安装中止。");
                return 1;
            }

            Console.WriteLine("== 部署 ==");
            PsResult dep = Ps.Run("deploy.ps1", BuildDeployArgs(), Console.WriteLine, false);
            Console.WriteLine(dep.StdOut);
            if (dep.ExitCode != 0)
            {
                Console.Error.WriteLine("部署失败。");
                return 1;
            }

            Console.WriteLine("== 自检 ==");
            PsResult test = Ps.Run("test-deploy.ps1", "-Target \"" + TargetDir + "\"", Console.WriteLine, false);
            Console.WriteLine(test.StdOut);
            if (test.ExitCode != 0)
            {
                Console.Error.WriteLine("自检失败，部署未完成（可运行一键清理与回退.cmd 撤销）。");
                return 1;
            }

            Console.WriteLine("部署完成：" + TargetDir);
            return 0;
        }

        internal static string BuildEnvArgs()
        {
            return string.Format(
                "-Target \"{0}\" -Port {1} -PulsePort {2} -TunerPort {3}",
                TargetDir, PortCodevault, PortPulse, PortTuner);
        }

        internal static string BuildDeployArgs()
        {
            string args = string.Format(
                "-Target \"{0}\" -Port {1} -PulsePort {2} -TunerPort {3}",
                TargetDir, PortCodevault, PortPulse, PortTuner);
            if (AllowLan) args += " -Lan";
            if (NoDesktop) args += " -NoDesktop";
            return args;
        }
    }

    /// <summary>Result of one powershell.exe invocation.</summary>
    internal class PsResult
    {
        internal int ExitCode;
        internal string StdOut = "";
        internal string StdErr = "";
    }

    internal static class Ps
    {
        /// <summary>
        /// Run a helper script. Output encoding is forced to UTF-8 first, otherwise
        /// PowerShell 5.1 emits ANSI bytes and every Chinese line arrives garbled.
        /// </summary>
        internal static PsResult Run(string script, string arguments, Action<string> onLine, bool captureOnly)
        {
            PsResult result = new PsResult();
            string scriptPath = Path.Combine(Program.ToolsDir, script);
            if (!File.Exists(scriptPath))
            {
                result.ExitCode = 127;
                result.StdErr = "缺少脚本：" + scriptPath;
                return result;
            }

            string command = string.Format(
                "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; & '{0}' {1}",
                scriptPath.Replace("'", "''"), arguments);

            ProcessStartInfo psi = new ProcessStartInfo("powershell.exe");
            psi.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + command + "\"";
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.WorkingDirectory = Program.PayloadDir;

            StringBuilder sb = new StringBuilder();
            StringBuilder eb = new StringBuilder();

            try
            {
                using (Process proc = new Process())
                {
                    proc.StartInfo = psi;
                    proc.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) return;
                        sb.AppendLine(e.Data);
                        if (!captureOnly && onLine != null) onLine(e.Data);
                    };
                    proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) return;
                        eb.AppendLine(e.Data);
                        if (!captureOnly && onLine != null) onLine("[stderr] " + e.Data);
                    };
                    proc.Start();
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    proc.WaitForExit(600000);
                    result.ExitCode = proc.HasExited ? proc.ExitCode : 124;
                }
            }
            catch (Exception ex)
            {
                result.ExitCode = 1;
                result.StdErr = ex.Message;
            }

            result.StdOut = sb.ToString();
            result.StdErr = eb.ToString();
            return result;
        }

        /// <summary>Pull the last JSON object out of mixed console output.</summary>
        internal static Dictionary<string, object> LastJson(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string[] lines = text.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i].Trim();
                if (line.Length < 2 || line[0] != '{') continue;
                try
                {
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    ser.MaxJsonLength = 4 * 1024 * 1024;
                    return ser.Deserialize<Dictionary<string, object>>(line);
                }
                catch
                {
                    // not the JSON line, keep looking
                }
            }
            return null;
        }
    }

    internal class InstallerForm : Form
    {
        private readonly TextBox _log;
        private readonly TextBox _target;
        private readonly NumericUpDown _port;
        private readonly NumericUpDown _pulse;
        private readonly NumericUpDown _tuner;
        private readonly CheckBox _lan;
        private readonly CheckBox _desktop;
        private readonly Button _btnCheck;
        private readonly Button _btnInstall;
        private readonly Button _btnClose;
        private readonly Label _status;
        private Label _installBadge;
        private bool _envPassed;
        private bool _busy;

        // Filled by check-install.ps1: "not-installed" | "same" | "upgrade" | "downgrade"
        private string _installState = "not-installed";
        private string _installedVersion = "";
        private string _installedAt = "";
        private string _installedPath = "";

        /** "2026-11-13T13:45:56+08:00" -> "2026-11-13 13:45"; never throws. */
        private static string FormatDeployedAt(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "未知";
            DateTime parsed;
            return DateTime.TryParse(raw, out parsed) ? parsed.ToString("yyyy-MM-dd HH:mm") : raw;
        }

        /** One-line summary of what this run will do, shown next to the buttons. */
        private string InstallStateText()
        {
            string installedPart = "v" + _installedVersion +
                (_installedAt.Length > 0 ? "（" + FormatDeployedAt(_installedAt) + " 部署）" : "");

            switch (_installState)
            {
                case "same":
                    return "已装 " + installedPart + "，与本包同版本（将重装）";
                case "upgrade":
                    return "升级 " + installedPart + " → v" + Program.PackageVersion;
                case "downgrade":
                    return "降级 " + installedPart + " → v" + Program.PackageVersion;
                default:
                    return "首次安装 v" + Program.PackageVersion;
            }
        }

        /** Multi-line block for the confirmation dialog: version, date, location. */
        private string InstallSummaryBlock()
        {
            if (_installState == "not-installed")
            {
                return "首次安装，未检测到已部署副本";
            }
            return string.Format(
                "已装版本：v{0}\n更新时间：{1}\n安装位置：{2}\n本次方式：{3}",
                _installedVersion,
                FormatDeployedAt(_installedAt),
                _installedPath,
                InstallStateText());
        }

        internal InstallerForm()
        {
            Text = "CodeBuddy 工作套件 - 安装程序";
            ClientSize = new Size(820, 700);
            MinimumSize = new Size(780, 660);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.FromArgb(246, 248, 251);

            Label title = new Label();
            title.Text = "CodeBuddy 工作套件";
            title.Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(27, 58, 92);
            title.AutoSize = true;
            title.Location = new Point(20, 16);
            Controls.Add(title);

            Label subtitle = new Label();
            subtitle.Text = "索引库 + AI 进度看板 + IDE 降温面板 + 脚本库 + 全局规则（一次性部署）";
            subtitle.ForeColor = Color.FromArgb(95, 105, 120);
            subtitle.AutoSize = true;
            subtitle.Location = new Point(22, 52);
            Controls.Add(subtitle);

            TextBox intro = new TextBox();
            intro.Multiline = true;
            intro.ReadOnly = true;
            intro.ScrollBars = ScrollBars.Vertical;
            intro.BackColor = Color.White;
            intro.BorderStyle = BorderStyle.FixedSingle;
            intro.Font = new Font("Microsoft YaHei UI", 9F);
            intro.Text = IntroText();
            intro.Location = new Point(22, 80);
            intro.Size = new Size(776, 210);
            Controls.Add(intro);

            // --- 安装选项
            Label optTitle = new Label();
            optTitle.Text = "安装选项";
            optTitle.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            optTitle.ForeColor = Color.FromArgb(27, 58, 92);
            optTitle.AutoSize = true;
            optTitle.Location = new Point(22, 304);
            Controls.Add(optTitle);

            // 常驻一行：已装版本 + 更新日期 + 本次动作。
            // 放在日志里会被后续输出滚走，所以单独占一条固定位置。
            _installBadge = new Label();
            _installBadge.AutoSize = false;
            _installBadge.Bounds = new Rectangle(112, 302, 686, 22);
            _installBadge.Font = new Font("Microsoft YaHei UI", 9F);
            _installBadge.ForeColor = Color.FromArgb(120, 130, 145);
            _installBadge.Text = "尚未检测";
            Controls.Add(_installBadge);

            Label tLabel = new Label();
            tLabel.Text = "安装目录";
            tLabel.AutoSize = true;
            tLabel.Location = new Point(22, 336);
            Controls.Add(tLabel);

            _target = new TextBox();
            _target.Text = Program.TargetDir;
            _target.Location = new Point(110, 333);
            _target.Size = new Size(600, 24);
            Controls.Add(_target);

            Button browse = new Button();
            browse.Text = "浏览…";
            browse.Location = new Point(718, 332);
            browse.Size = new Size(80, 26);
            browse.Click += delegate
            {
                FolderBrowserDialog dlg = new FolderBrowserDialog();
                dlg.Description = "选择安装目录（建议放在非系统盘或用户目录下）";
                if (Directory.Exists(_target.Text)) dlg.SelectedPath = _target.Text;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _target.Text = Path.Combine(dlg.SelectedPath, "CodeBuddyWorkKit");
                }
            };
            Controls.Add(browse);

            Label pLabel = new Label();
            pLabel.Text = "端口";
            pLabel.AutoSize = true;
            pLabel.Location = new Point(22, 372);
            Controls.Add(pLabel);

            _port = MakePort(62, 369);
            _pulse = MakePort(170, 369);
            _tuner = MakePort(278, 369);

            Label hint = new Label();
            hint.Text = "索引库 / 进度看板 / 降温面板（被占用可改端口）";
            hint.ForeColor = Color.FromArgb(120, 130, 145);
            hint.AutoSize = true;
            hint.Location = new Point(390, 372);
            Controls.Add(hint);

            _lan = new CheckBox();
            _lan.Text = "允许局域网访问（监听 0.0.0.0，需自备访问控制）";
            _lan.Location = new Point(22, 402);
            _lan.AutoSize = true;
            Controls.Add(_lan);

            _desktop = new CheckBox();
            _desktop.Text = "在桌面创建启动快捷方式";
            _desktop.Location = new Point(430, 402);
            _desktop.AutoSize = true;
            _desktop.Checked = true;
            Controls.Add(_desktop);

            // --- 日志
            Label logTitle = new Label();
            logTitle.Text = "执行日志";
            logTitle.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            logTitle.ForeColor = Color.FromArgb(27, 58, 92);
            logTitle.AutoSize = true;
            logTitle.Location = new Point(22, 434);
            Controls.Add(logTitle);

            _log = new TextBox();
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.BackColor = Color.FromArgb(24, 32, 44);
            _log.ForeColor = Color.FromArgb(210, 225, 240);
            _log.Font = new Font("Consolas", 9F);
            _log.BorderStyle = BorderStyle.FixedSingle;
            _log.Location = new Point(22, 458);
            _log.Size = new Size(776, 170);
            Controls.Add(_log);

            _status = new Label();
            _status.Text = "未开始";
            _status.AutoSize = true;
            _status.ForeColor = Color.FromArgb(95, 105, 120);
            _status.Location = new Point(22, 642);
            Controls.Add(_status);

            _btnCheck = new Button();
            _btnCheck.Text = "① 检测环境";
            _btnCheck.Location = new Point(470, 636);
            _btnCheck.Size = new Size(110, 32);
            _btnCheck.Click += delegate { RunCheck(); };
            Controls.Add(_btnCheck);

            _btnInstall = new Button();
            _btnInstall.Text = "② 开始安装";
            _btnInstall.Location = new Point(590, 636);
            _btnInstall.Size = new Size(110, 32);
            _btnInstall.Enabled = false;
            _btnInstall.Click += delegate { RunInstall(); };
            Controls.Add(_btnInstall);

            _btnClose = new Button();
            _btnClose.Text = "取消";
            _btnClose.Location = new Point(710, 636);
            _btnClose.Size = new Size(88, 32);
            _btnClose.Click += delegate { Close(); };
            Controls.Add(_btnClose);

            AppendLog("准备就绪。先点「检测环境」，通过后才能安装。");
        }

        private NumericUpDown MakePort(int x, int y)
        {
            NumericUpDown box = new NumericUpDown();
            box.Minimum = 1024;
            box.Maximum = 65535;
            box.Location = new Point(x, y);
            box.Size = new Size(96, 24);
            Controls.Add(box);
            return box;
        }

        private void InitPorts()
        {
            _port.Value = Program.PortCodevault;
            _pulse.Value = Program.PortPulse;
            _tuner.Value = Program.PortTuner;
        }

        private static string IntroText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("本次安装会在你选定的目录中部署一整套本地工作环境，并在 CodeBuddy 中登记使用规则：");
            sb.AppendLine();
            sb.AppendLine("  1. CodeVault 索引库      项目索引 / 源码查看 / 仓库状态 / 脚本库（默认 47910）");
            sb.AppendLine("  2. CodeBuddy Pulse       AI 编程进度看板，含重复测试进程守护（默认 47821）");
            sb.AppendLine("  3. Idle Tuner            IDE 空闲降温面板：回收内存、降优先级、挂起进程（默认 47822）");
            sb.AppendLine("  4. ScriptVault 脚本库    公共模块 + 可复用工具 + 回归测试，含项目接入工具 adopt-project");
            sb.AppendLine("  5. 全局规则              ~\\.codebuddy\\rules\\deploy-target.mdc");
            sb.AppendLine("                           让 CodeBuddy 在本机优先复用上述工具，并在任务收尾自动做版本管理");
            sb.AppendLine();
            sb.AppendLine("安装过程：检测环境 → 复制应用 → 写配置 → 生成启动脚本 → 建桌面快捷方式 → 写规则 → 自检");
            sb.AppendLine();
            sb.AppendLine("说明：");
            sb.AppendLine("  · 自检全部通过才会提示「部署完成」；任一项失败会列出原因并可用一键清理回退");
            sb.AppendLine("  · 重复安装只覆盖应用代码，保留 config 配置与已有数据");
            sb.AppendLine("  · 安装目录内提供「一键清理与回退.cmd」，停止服务并移除快捷方式、规则与文件");
            return sb.ToString();
        }

        private void AppendLog(string line)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(AppendLog), line);
                return;
            }
            _log.AppendText(line + Environment.NewLine);
        }

        private void SetBusy(bool busy, string status)
        {
            _busy = busy;
            _btnCheck.Enabled = !busy;
            _btnInstall.Enabled = !busy && _envPassed;
            _btnClose.Enabled = !busy;
            _target.Enabled = !busy;
            _port.Enabled = !busy;
            _pulse.Enabled = !busy;
            _tuner.Enabled = !busy;
            _lan.Enabled = !busy;
            _desktop.Enabled = !busy;
            if (status != null) _status.Text = status;
        }

        private void SyncOptions()
        {
            Program.TargetDir = _target.Text.Trim();
            Program.PortCodevault = (int)_port.Value;
            Program.PortPulse = (int)_pulse.Value;
            Program.PortTuner = (int)_tuner.Value;
            Program.AllowLan = _lan.Checked;
            Program.NoDesktop = !_desktop.Checked;
        }

        private void RunCheck()
        {
            if (_busy) return;
            SyncOptions();
            if (Program.TargetDir.Length == 0)
            {
                MessageBox.Show(this, "请先选择安装目录。", "缺少参数", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SetBusy(true, "正在检测环境…");
            _envPassed = false;
            AppendLog("");
            AppendLog("===== 安装状态检测 =====");

            ThreadPool.QueueUserWorkItem(delegate
            {
                // (1) Already installed? which version? upgrade or reinstall?
                // -Json is required: without it the script prints prose only and
                // LastJson() finds nothing, silently falling back to "not installed".
                PsResult install = Ps.Run(
                    "check-install.ps1",
                    "-Target \"" + Program.TargetDir + "\" -Json",
                    AppendLog,
                    false);
                Dictionary<string, object> info = Ps.LastJson(install.StdOut);
                string state = "not-installed";
                string installedVersion = "";
                string installedAt = "";
                string installedPath = "";
                if (info != null)
                {
                    if (info.ContainsKey("state")) state = Convert.ToString(info["state"]);
                    object rawInstalled;
                    if (info.TryGetValue("installed", out rawInstalled))
                    {
                        Dictionary<string, object> map = rawInstalled as Dictionary<string, object>;
                        if (map != null)
                        {
                            if (map.ContainsKey("version")) installedVersion = Convert.ToString(map["version"]);
                            if (map.ContainsKey("deployedAt")) installedAt = Convert.ToString(map["deployedAt"]);
                            if (map.ContainsKey("path")) installedPath = Convert.ToString(map["path"]);
                        }
                    }
                }

                AppendLog("");
                AppendLog("===== 环境检测 =====");
                PsResult r = Ps.Run("check-env.ps1", Program.BuildEnvArgs(), AppendLog, false);
                Dictionary<string, object> json = Ps.LastJson(r.StdOut);

                BeginInvoke(new Action(delegate
                {
                    _installState = state;
                    _installedVersion = installedVersion;
                    _installedAt = installedAt;
                    _installedPath = installedPath;

                    bool ok = r.ExitCode == 0;
                    int blocks = 0;
                    int warns = 0;
                    if (json != null)
                    {
                        blocks = ToInt(json, "blocks");
                        warns = ToInt(json, "warns");
                    }
                    _envPassed = ok;

                    // 状态条：已装版本 + 部署日期 + 本次动作
                    _installBadge.Text = InstallStateText();
                    _installBadge.ForeColor =
                        _installState == "downgrade" ? Color.FromArgb(200, 60, 60) :
                        _installState == "same" ? Color.FromArgb(176, 132, 24) :
                        _installState == "upgrade" ? Color.FromArgb(24, 120, 72) :
                        Color.FromArgb(24, 110, 90);

                    SetBusy(false, ok
                        ? string.Format("{0}｜环境检测通过（{1} 项提示）", InstallStateText(), warns)
                        : string.Format("环境检测未通过：{0} 项阻塞", blocks));

                    if (ok)
                    {
                        AppendLog("结论：环境检测通过。点击「开始安装」继续。");
                    }
                    else
                    {
                        AppendLog("结论：存在阻塞项，请按提示处理后重新检测。");
                        MessageBox.Show(this,
                            "环境检测未通过，无法安装。\n\n请查看日志中标记为 [阻塞] 的项目。",
                            "环境检测失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }));
            });
        }

        private static int ToInt(Dictionary<string, object> map, string key)
        {
            if (map == null || !map.ContainsKey(key) || map[key] == null) return 0;
            int value;
            return int.TryParse(Convert.ToString(map[key]), out value) ? value : 0;
        }

        private void RunInstall()
        {
            if (_busy) return;
            if (!_envPassed)
            {
                MessageBox.Show(this, "请先通过环境检测。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SyncOptions();

            string message = string.Format(
                "即将安装到：\n{0}\n\n{1}\n\n索引库端口 {2}　看板 {3}　降温 {4}\n{5}\n\n确认开始安装？",
                Program.TargetDir, InstallSummaryBlock(),
                Program.PortCodevault, Program.PortPulse, Program.PortTuner,
                Program.AllowLan ? "监听 0.0.0.0（局域网可访问）" : "仅本机访问");
            if (MessageBox.Show(this, message, "确认安装", MessageBoxButtons.OKCancel, MessageBoxIcon.Question)
                != DialogResult.OK)
            {
                return;
            }

            SetBusy(true, "正在部署…");
            AppendLog("");
            AppendLog("===== 开始部署 =====");

            ThreadPool.QueueUserWorkItem(delegate
            {
                PsResult dep = Ps.Run("deploy.ps1", Program.BuildDeployArgs(), AppendLog, false);
                if (dep.ExitCode != 0)
                {
                    BeginInvoke(new Action(delegate
                    {
                        SetBusy(false, "部署失败");
                        AppendLog("部署失败（退出码 " + dep.ExitCode + "）。");
                        MessageBox.Show(this,
                            "部署过程中出错，请查看日志。\n可运行目标目录中的「一键清理与回退.cmd」撤销。",
                            "部署失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }));
                    return;
                }

                BeginInvoke(new Action(delegate { AppendLog(""); AppendLog("===== 部署自检 ====="); }));

                PsResult test = Ps.Run(
                    "test-deploy.ps1",
                    "-Target \"" + Program.TargetDir + "\"",
                    AppendLog,
                    false);

                BeginInvoke(new Action(delegate
                {
                    if (test.ExitCode == 0)
                    {
                        SetBusy(false, "部署完成");
                        AppendLog("");
                        AppendLog("===== 部署完成 =====");
                        AppendLog("目录：" + Program.TargetDir);
                        AppendLog("访问：http://127.0.0.1:" + Program.PortCodevault + "/");
                        MessageBox.Show(this,
                            string.Format(
                                "部署完成，自检全部通过。\n\n目录：{0}\n访问：http://127.0.0.1:{1}/\n\n桌面已创建「CodeBuddy 工作套件」快捷方式。\n如需撤销，运行目录中的「一键清理与回退.cmd」。",
                                Program.TargetDir, Program.PortCodevault),
                            "部署完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        SetBusy(false, "自检未通过");
                        AppendLog("自检未通过（退出码 " + test.ExitCode + "）。");
                        MessageBox.Show(this,
                            "服务已复制但自检未通过，未标记为部署完成。\n\n请查看日志中的 [FAIL] 项；可运行「一键清理与回退.cmd」撤销。",
                            "自检失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }));
            });
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            InitPorts();
        }
    }
}
