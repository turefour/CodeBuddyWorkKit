using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace WorkKit.Native
{
    /// <summary>
    /// 原生主窗口：完全不依赖浏览器，纯 WinForms 绘制。
    ///
    /// 为什么要有这一版：
    ///   浏览器嵌入版（Shell.exe）靠 Edge 的 --app 窗口 + SetParent 实现，
    ///   跨进程窗口总有渲染/层级/黑屏的边界问题。本版把数据直接从 HTTP 接口
    ///   取回来自己画，视觉可控、启动更快、没有外部依赖。
    /// </summary>
    internal sealed class NativeForm : Form
    {
        // ---------------------------------------------------------- 状态

        private readonly string _kitRoot;
        private int _port = 47910;
        private int _pulse = 47821;
        private int _tuner = 47822;
        private string _bindHost = "127.0.0.1";

        private sealed class ShellError
        {
            internal DateTime At;
            internal string Source = "";
            internal string Message = "";
        }

        private readonly List<ShellError> _errors = new List<ShellError>();

        // ---------------------------------------------------------- 控件

        private Panel _topBar;
        private Label _statusLabel;
        private Label _statsLabel;
        private Panel _errorButton;
        private Label _errorText;
        private Label _errorBadge;

        private Panel _nav;
        private Label _navTitle;
        private readonly List<Button> _navButtons = new List<Button>();

        private Panel _host;
        private Panel _viewOverview;
        private Panel _viewProjects;
        private Panel _viewScripts;
        private Panel _viewRepos;
        private Panel _viewPulse;

        private Panel _footBar;
        private Label _footLeft;
        private Label _footRight;

        private string _view = "overview";
        private System.Windows.Forms.Timer _timer;
        private DateTime _lastTick = DateTime.MinValue;
        private int _failStreak;
        private int _upCount = 3;
        private int _estTokens;
        private int _usageEvents;
        private string _lastStatusText = "";

        // ---------------------------------------------------------- 概览卡片

        private Label _kpiProjects;
        private Label _kpiRepos;
        private Label _kpiScripts;
        private Label _kpiServices;
        private ListView _recentProjects;

        // ---------------------------------------------------------- 项目

        private ListView _projectList;
        private Panel _projectDetail;
        private string _detailText = "";

        // ---------------------------------------------------------- 脚本库

        private ComboBox _scriptCategory;
        private ListView _scriptList;
        private Label _scriptSummary;

        // ---------------------------------------------------------- 仓库

        private ListView _repoList;
        private Label _repoSummary;

        // ---------------------------------------------------------- 看板

        private Label _kpiJobs;
        private Label _kpiProcs;
        private Label _kpiCpu;
        private Label _kpiMem;
        private Panel _cpuChart;
        private Panel _memChart;
        private readonly List<double> _cpuHistory = new List<double>();
        private readonly List<double> _memHistory = new List<double>();
        private const int HISTORY_LIMIT = 40;
        private ListView _procList;
        private Label _guardLabel;

        // ---------------------------------------------------------- 构造

        internal string KitRoot { get { return _kitRoot; } }
        internal string IndexUrl { get { return "http://127.0.0.1:" + _port + "/"; } }

        internal NativeForm(string[] args)
        {
            foreach (string arg in args)
            {
                if (arg.StartsWith("--kit=")) _kitRoot = arg.Substring(6).Trim('"');
            }
            if (string.IsNullOrEmpty(_kitRoot))
            {
                _kitRoot = Path.GetDirectoryName(Application.ExecutablePath);
            }
            _kitRoot = Path.GetFullPath(_kitRoot);
            ReadConfig();

            Text = "CodeBuddy 工作套件 · 原生版";
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            Width = Math.Min(1280, work.Width - 60);
            Height = Math.Min(860, work.Height - 60);
            MinimumSize = new Size(1000, 620);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            try { Icon = SystemIcons.Application; } catch { }

            // 顺序很关键：先加 Top/Left/Bottom，最后加 Dock=Fill 的容器。
            // WinForms 按 z-order 逆序处理 Dock，最后添加的 Fill 才能拿到
            // 「扣掉边栏之后」的剩余空间；顺序反了会被边栏遮挡。
            BuildTopBar();
            BuildNav();
            BuildFooter();
            BuildHost();

            Load += delegate
            {
                LayoutShell();
                ShowView("overview");
                RefreshAll();
            };
            Resize += delegate { LayoutShell(); };

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 4000;
            _timer.Tick += delegate { Tick(); };
            _timer.Start();
            _lastTick = DateTime.Now;
        }

        private void ReadConfig()
        {
            try
            {
                string path = Path.Combine(_kitRoot, "config", "deploy.json");
                if (!File.Exists(path)) return;
                string raw = File.ReadAllText(path, Encoding.UTF8).TrimStart('\uFEFF');
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> map = ser.Deserialize<Dictionary<string, object>>(raw);
                if (map == null) return;

                if (map.ContainsKey("host") && map["host"] != null) _bindHost = Convert.ToString(map["host"]);
                Dictionary<string, object> ports = Api.Map(map, "ports");
                if (ports != null)
                {
                    _port = Api.Int(ports, "codevault", _port);
                    _pulse = Api.Int(ports, "pulse", _pulse);
                    _tuner = Api.Int(ports, "tuner", _tuner);
                }
            }
            catch
            {
                /* 配置损坏时用默认端口，界面上会体现为「服务无响应」 */
            }
        }

        // ---------------------------------------------------------- 顶部工具条

        private void BuildTopBar()
        {
            _topBar = new Panel();
            
            _topBar.Height = 46;
            _topBar.BackColor = Theme.Panel;
            _topBar.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen pen = new Pen(Theme.Border))
                {
                    e.Graphics.DrawLine(pen, 0, _topBar.Height - 1, _topBar.Width, _topBar.Height - 1);
                }
            };
            Controls.Add(_topBar);

            Label brand = Ui.Text("CodeBuddy", Theme.Font(11F, FontStyle.Bold), Theme.Text);
            brand.Bounds = new Rectangle(16, 10, 110, 26);
            _topBar.Controls.Add(brand);

            _statsLabel = Ui.Text("", Theme.Small, Theme.Dim);
            _statsLabel.Bounds = new Rectangle(660, 14, 300, 20);
            _topBar.Controls.Add(_statsLabel);

            _statusLabel = Ui.Text("正在连接服务…", Theme.Small, Theme.Dim);
            _statusLabel.Bounds = new Rectangle(132, 12, 520, 22);
            _topBar.Controls.Add(_statusLabel);

            // 报错按钮 + 徽章
            _errorButton = new Panel();
            _errorButton.Size = new Size(88, 30);
            _errorButton.BackColor = Theme.Card;
            _errorButton.BorderStyle = BorderStyle.FixedSingle;
            _errorButton.Cursor = Cursors.Hand;
            _errorButton.Click += delegate { ShowErrors(); };
            _topBar.Controls.Add(_errorButton);

            _errorText = Ui.Text("报错", Theme.Small, Theme.Dim);
            _errorText.Dock = DockStyle.Fill;
            _errorText.TextAlign = ContentAlignment.MiddleCenter;
            _errorText.Click += delegate { ShowErrors(); };
            _errorButton.Controls.Add(_errorText);

            _errorBadge = Ui.Text("0", Theme.Font(7.5F, FontStyle.Bold), Color.White);
            _errorBadge.Size = new Size(20, 15);
            _errorBadge.Location = new Point(68, -6);
            _errorBadge.TextAlign = ContentAlignment.MiddleCenter;
            _errorBadge.BackColor = Theme.Err;
            _errorBadge.Visible = false;
            _errorBadge.Click += delegate { ShowErrors(); };
            _errorButton.Controls.Add(_errorBadge);
            _errorBadge.BringToFront();

            Button refresh = Ui.Flat("刷新", 64, 30);
            refresh.Tag = "refresh";
            refresh.Click += delegate { RefreshAll(); };
            _topBar.Controls.Add(refresh);

            Button settings = Ui.Flat("设置", 64, 30);
            settings.Tag = "settings";
            settings.Click += delegate { ShowSettings(); };
            _topBar.Controls.Add(settings);

            Button web = Ui.Flat("浏览器版", 82, 30);
            web.Tag = "web";
            web.Click += delegate { OpenExternal("http://127.0.0.1:" + _port + "/"); };
            _topBar.Controls.Add(web);

            _topBar.Resize += delegate { LayoutTopBar(); };
            LayoutTopBar();
        }

        private void LayoutTopBar()
        {
            if (_topBar == null) return;
            int right = _topBar.ClientSize.Width - 14;
            Button[] rightButtons = new Button[3];
            int index = 0;
            foreach (Control control in _topBar.Controls)
            {
                Button button = control as Button;
                if (button != null && button.Tag != null) rightButtons[index++] = button;
            }
            for (int i = rightButtons.Length - 1; i >= 0; i -= 1)
            {
                if (rightButtons[i] == null) continue;
                rightButtons[i].Location = new Point(right - rightButtons[i].Width, 8);
                right -= rightButtons[i].Width + 8;
            }
            if (_errorButton != null) _errorButton.Location = new Point(right - _errorButton.Width, 8);
            if (_statusLabel != null) _statusLabel.Width = Math.Max(160, right - _errorButton.Width - 160);
            if (_statsLabel != null && _statusLabel != null)
            {
                _statsLabel.Location = new Point(_statusLabel.Right + 16, 14);
                _statsLabel.Width = Math.Max(120, Math.Min(320, right - _errorButton.Width - _statsLabel.Left - 8));
            }
        }

        // ---------------------------------------------------------- 侧栏导航

        private void BuildNav()
        {
            _nav = new Panel();
            
            _nav.Width = 176;
            _nav.BackColor = Theme.Panel;
            _nav.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen pen = new Pen(Theme.Border))
                {
                    e.Graphics.DrawLine(pen, _nav.Width - 1, 0, _nav.Width - 1, _nav.Height);
                }
            };
            Controls.Add(_nav);

            _navTitle = Ui.Text("视图", Theme.SmallBold, Theme.Faint);
            _navTitle.Bounds = new Rectangle(18, 16, 140, 20);
            _nav.Controls.Add(_navTitle);

            string[][] items = new string[][]
            {
                new string[] { "overview", "概览" },
                new string[] { "projects", "项目索引" },
                new string[] { "scripts", "脚本库" },
                new string[] { "repos", "仓库与备份" },
                new string[] { "pulse", "运行看板" }
            };

            int y = 44;
            foreach (string[] item in items)
            {
                Button button = new Button();
                button.Text = "  " + item[1];
                button.Tag = item[0];
                button.TextAlign = ContentAlignment.MiddleLeft;
                button.Bounds = new Rectangle(10, y, 156, 36);
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderSize = 0;
                button.BackColor = Theme.Panel;
                button.ForeColor = Theme.Dim;
                button.Font = Theme.Body;
                button.Cursor = Cursors.Hand;
                button.Click += delegate(object sender, EventArgs e)
                {
                    Button clicked = (Button)sender;
                    ShowView(Convert.ToString(clicked.Tag));
                };
                _nav.Controls.Add(button);
                _navButtons.Add(button);
                y += 40;
            }

            Button tuner = new Button();
            tuner.Text = "  降温面板";
            tuner.TextAlign = ContentAlignment.MiddleLeft;
            tuner.Bounds = new Rectangle(10, y, 156, 36);
            tuner.FlatStyle = FlatStyle.Flat;
            tuner.FlatAppearance.BorderSize = 0;
            tuner.BackColor = Theme.Panel;
            tuner.ForeColor = Theme.Faint;
            tuner.Font = Theme.Body;
            tuner.Cursor = Cursors.Hand;
            tuner.Click += delegate { OpenExternal("http://127.0.0.1:" + _tuner + "/"); };
            _nav.Controls.Add(tuner);

            Button settingsButton = Ui.Flat("设置", 156, 34);
            settingsButton.Bounds = new Rectangle(10, y + 8, 156, 34);
            settingsButton.TextAlign = ContentAlignment.MiddleLeft;
            settingsButton.Text = "  设置";
            settingsButton.Click += delegate { ShowSettings(); };
            _nav.Controls.Add(settingsButton);

            Button webButton = Ui.Flat("浏览器版", 156, 34);
            webButton.Bounds = new Rectangle(10, y + 48, 156, 34);
            webButton.TextAlign = ContentAlignment.MiddleLeft;
            webButton.Text = "  浏览器版";
            webButton.Click += delegate { OpenExternal("http://127.0.0.1:" + _port + "/"); };
            _nav.Controls.Add(webButton);

            Label hint = Ui.Text("托盘常驻\r\n最小化或关闭都会\r\n收进右下角", Theme.Small, Theme.Faint);
            hint.Bounds = new Rectangle(18, y + 96, 146, 60);
            _nav.Controls.Add(hint);
        }

        private void BuildHost()
        {
            _host = new Panel();
            
            _host.BackColor = Theme.Bg;
            _host.Padding = new Padding(16, 14, 16, 10);
            Controls.Add(_host);

            _viewOverview = BuildOverview();
            _viewProjects = BuildProjects();
            _viewScripts = BuildScripts();
            _viewRepos = BuildRepos();
            _viewPulse = BuildPulse();

            Panel[] all = new Panel[] { _viewOverview, _viewProjects, _viewScripts, _viewRepos, _viewPulse };
            foreach (Panel view in all)
            {
                view.Dock = DockStyle.Fill;
                view.BackColor = Theme.Bg;
                view.Visible = false;
                _host.Controls.Add(view);
            }
        }

        private void BuildFooter()
        {
            Panel footer = new Panel();
            _footBar = footer;
            
            footer.Height = 28;
            footer.BackColor = Theme.Panel;
            footer.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen pen = new Pen(Theme.Border))
                {
                    e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
                }
            };
            Controls.Add(footer);
            footer.BringToFront();

            _footLeft = Ui.Text("", Theme.Small, Theme.Dim);
            _footLeft.Bounds = new Rectangle(14, 5, 620, 18);
            footer.Controls.Add(_footLeft);

            _footRight = Ui.Text("", Theme.Small, Theme.Dim);
            _footRight.TextAlign = ContentAlignment.MiddleRight;
            _footRight.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _footRight.Bounds = new Rectangle(footer.Width - 300, 5, 286, 18);
            footer.Controls.Add(_footRight);
        }

        /// <summary>
        /// 手动布局四个区块。不用 Dock：WinForms 的 Dock 依赖 z-order，
        /// Fill 容器很容易把边栏或状态栏挤掉，改成绝对坐标反而更可控。
        /// </summary>
        private void LayoutShell()
        {
            int width = ClientSize.Width;
            int height = ClientSize.Height;
            const int topH = 46;
            const int navW = 176;
            const int footH = 28;
            int bodyH = Math.Max(80, height - topH - footH);

            if (_topBar != null) _topBar.SetBounds(0, 0, width, topH);
            if (_nav != null) _nav.SetBounds(0, topH, navW, bodyH);
            if (_host != null) _host.SetBounds(navW, topH, Math.Max(200, width - navW), bodyH);
            if (_footBar != null) _footBar.SetBounds(0, Math.Max(0, height - footH), width, footH);

            LayoutTopBar();
        }

        private void ShowView(string name)
        {
            _view = name;
            Panel[] all = new Panel[] { _viewOverview, _viewProjects, _viewScripts, _viewRepos, _viewPulse };
            foreach (Panel view in all)
            {
                if (view != null) view.Visible = false;
            }

            Panel target = _viewOverview;
            if (name == "projects") target = _viewProjects;
            else if (name == "scripts") target = _viewScripts;
            else if (name == "repos") target = _viewRepos;
            else if (name == "pulse") target = _viewPulse;
            if (target != null)
            {
                target.Visible = true;
                target.BringToFront();
            }

            foreach (Button button in _navButtons)
            {
                bool active = Convert.ToString(button.Tag) == name;
                button.BackColor = active ? Theme.AccentSoft : Theme.Panel;
                button.ForeColor = active ? Theme.Text : Theme.Dim;
            }
        }

        // ---------------------------------------------------------- 概览

        private Panel BuildOverview()
        {
            Panel panel = new Panel();

            Label title = Ui.Title("概览");
            title.Bounds = new Rectangle(0, 0, 320, 30);
            panel.Controls.Add(title);

            Label sub = Ui.Text("数据来自本机三个服务的 HTTP 接口，每 4 秒刷新一次。", Theme.Small, Theme.Faint);
            sub.Bounds = new Rectangle(0, 32, 620, 20);
            panel.Controls.Add(sub);

            _kpiProjects = MakeCard(panel, 0, 64, "项目", "—");
            _kpiRepos = MakeCard(panel, 190, 64, "仓库", "—");
            _kpiScripts = MakeCard(panel, 380, 64, "脚本", "—");
            _kpiServices = MakeCard(panel, 570, 64, "服务", "—");

            Label recentTitle = Ui.Text("最近项目", Theme.Section, Theme.Text);
            recentTitle.Bounds = new Rectangle(0, 168, 240, 24);
            panel.Controls.Add(recentTitle);

            _recentProjects = Ui.List("项目", "类型", "状态", "概要");
            _recentProjects.Bounds = new Rectangle(0, 196, 900, 300);
            _recentProjects.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _recentProjects.Columns[0].Width = 200;
            _recentProjects.Columns[1].Width = 110;
            _recentProjects.Columns[2].Width = 100;
            _recentProjects.Columns[3].Width = 420;
            Draw.ListView(_recentProjects);
            panel.Controls.Add(_recentProjects);

            return panel;
        }

        /// <summary>迷你趋势图容器：自绘 Panel，一个控件画完整张图。</summary>
        private Panel MakeChart(Panel parent, int x, int y, string caption)
        {
            Panel chart = new Panel();
            chart.Bounds = new Rectangle(x, y, 150, 84);
            chart.BackColor = Theme.Card;
            chart.Tag = caption;
            chart.Paint += delegate(object s, PaintEventArgs e)
            {
                bool isCpu = ((Panel)s).Tag as string == "CPU 趋势";
                List<double> data = isCpu ? _cpuHistory : _memHistory;
                string current = isCpu
                    ? (data.Count > 0 ? Math.Round(data[data.Count - 1], 1) + "%" : "-")
                    : (data.Count > 0 ? Math.Round(data[data.Count - 1], 0) + " MB" : "-");
                Draw.Sparkline(
                    e.Graphics, ((Panel)s).ClientRectangle, data,
                    isCpu ? Theme.Ok : Theme.Accent, (string)((Panel)s).Tag, current);
            };
            parent.Controls.Add(chart);
            return chart;
        }

        private Label MakeCard(Panel parent, int x, int y, string caption, string value)
        {
            Panel card = new Panel();
            card.Bounds = new Rectangle(x, y, 176, 84);
            card.BackColor = Theme.Card;
            card.Paint += delegate(object s, PaintEventArgs e)
            {
                e.Graphics.DrawRectangle(Theme.Pen(Theme.Border), 0, 0, card.Width - 1, card.Height - 1);
                e.Graphics.FillRectangle(Theme.Brush(Theme.Accent), new Rectangle(0, 0, 3, card.Height));
            };
            parent.Controls.Add(card);

            Label cap = Ui.Text(caption, Theme.Small, Theme.Faint);
            cap.Bounds = new Rectangle(14, 12, 140, 18);
            card.Controls.Add(cap);

            Label val = Ui.Text(value, Theme.Font(20F, FontStyle.Bold), Theme.Text);
            val.Bounds = new Rectangle(14, 34, 150, 34);
            card.Controls.Add(val);
            return val;
        }

        // ---------------------------------------------------------- 项目

        private Panel BuildProjects()
        {
            Panel panel = new Panel();

            Label title = Ui.Title("项目索引");
            title.Bounds = new Rectangle(0, 0, 320, 30);
            panel.Controls.Add(title);

            Label sub = Ui.Text("点一行看详情；详情来自 CodeVault 的 projects.json。", Theme.Small, Theme.Faint);
            sub.Bounds = new Rectangle(0, 32, 700, 20);
            panel.Controls.Add(sub);

            _projectList = Ui.List("项目", "类型", "状态", "问题");
            _projectList.Bounds = new Rectangle(0, 60, 560, 460);

            // 详情直接画在容器上：右侧区域没有子控件，Pain t 一定可见，
            // 也省掉一个控件句柄。
            panel.Paint += delegate(object s, PaintEventArgs e)
            {
                if (_detailText.Length == 0) return;
                Rectangle box = new Rectangle(576, 60, Math.Max(200, panel.Width - 592), Math.Max(120, panel.Height - 76));
                e.Graphics.FillRectangle(Theme.Brush(Theme.Card), box);
                e.Graphics.DrawRectangle(Theme.Pen(Theme.Border), box.Left, box.Top, box.Width - 1, box.Height - 1);
                e.Graphics.FillRectangle(Theme.Brush(Theme.Accent), new Rectangle(box.Left, box.Top, 3, box.Height));
                TextRenderer.DrawText(
                    e.Graphics, _detailText, Theme.Mono,
                    new Rectangle(box.Left + 14, box.Top + 12, box.Width - 28, box.Height - 24),
                    Theme.Text,
                    TextFormatFlags.Top | TextFormatFlags.Left | TextFormatFlags.WordBreak);
            };
            _projectList.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom;
            _projectList.Columns[0].Width = 180;
            _projectList.Columns[1].Width = 110;
            _projectList.Columns[2].Width = 90;
            _projectList.Columns[3].Width = 90;
            _projectList.SelectedIndexChanged += delegate { ShowProjectDetail(); };
            Draw.ListView(_projectList);
            panel.Controls.Add(_projectList);

            return panel;
        }

        private void ShowProjectDetail()
        {
            if (_projectList == null) return;
            if (_projectList.SelectedItems.Count == 0)
            {
                _detailText = "选择一个项目查看详情。";
                if (_viewProjects != null) _viewProjects.Invalidate();
                return;
            }
            object tag = _projectList.SelectedItems[0].Tag;
            Dictionary<string, object> map = tag as Dictionary<string, object>;
            if (map == null)
            {
                _detailText = "无详情数据。";
                if (_viewProjects != null) _viewProjects.Invalidate();
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Api.Str(map, "name", Api.Str(map, "id", "-")));
            sb.AppendLine(new string('─', 40));
            sb.AppendLine("ID      " + Api.Str(map, "id", "-"));
            sb.AppendLine("类型    " + Api.Str(map, "kind", "-"));
            sb.AppendLine("状态    " + Api.Str(map, "status", "-"));
            sb.AppendLine("路径    " + Api.Str(map, "path", "-"));
            sb.AppendLine("");
            sb.AppendLine("概要");
            sb.AppendLine("  " + Api.Str(map, "summary", "（未填写）"));

            List<object> build = Api.AsList(map.ContainsKey("build") ? map["build"] : null);
            if (build.Count > 0)
            {
                sb.AppendLine("");
                sb.AppendLine("构建逻辑");
                foreach (object item in build) sb.AppendLine("  · " + item);
            }

            List<object> issues = Api.AsList(map.ContainsKey("issues") ? map["issues"] : null);
            sb.AppendLine("");
            sb.AppendLine("问题（" + issues.Count + "）");
            foreach (object item in issues)
            {
                Dictionary<string, object> issue = item as Dictionary<string, object>;
                if (issue == null) continue;
                string status = Api.Str(issue, "status", "");
                sb.AppendLine(
                    "  [" + Api.Str(issue, "level", "-") + "/" + status + "] " +
                    Api.Str(issue, "title", "-"));
            }

            _detailText = sb.ToString();
            if (_viewProjects != null) _viewProjects.Invalidate();
        }

        // ---------------------------------------------------------- 脚本库

        private Panel BuildScripts()
        {
            Panel panel = new Panel();

            Label title = Ui.Title("脚本库");
            title.Bounds = new Rectangle(0, 0, 320, 30);
            panel.Controls.Add(title);

            _scriptSummary = Ui.Text("", Theme.Small, Theme.Faint);
            _scriptSummary.Bounds = new Rectangle(0, 32, 700, 20);
            panel.Controls.Add(_scriptSummary);

            _scriptCategory = new ComboBox();
            _scriptCategory.DropDownStyle = ComboBoxStyle.DropDownList;
            _scriptCategory.Bounds = new Rectangle(0, 58, 180, 24);
            _scriptCategory.FlatStyle = FlatStyle.Flat;
            _scriptCategory.BackColor = Theme.Card;
            _scriptCategory.ForeColor = Theme.Text;
            _scriptCategory.Font = Theme.Small;
            _scriptCategory.SelectedIndexChanged += delegate { RefreshScripts(); };
            panel.Controls.Add(_scriptCategory);

            _scriptList = Ui.List("名称", "分类", "说明");
            _scriptList.Bounds = new Rectangle(0, 90, 900, 430);
            _scriptList.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _scriptList.Columns[0].Width = 180;
            _scriptList.Columns[1].Width = 130;
            _scriptList.Columns[2].Width = 560;
            _scriptList.DoubleClick += delegate { OpenSelectedScript(); };
            Draw.ListView(_scriptList);
            panel.Controls.Add(_scriptList);

            return panel;
        }

        private void OpenSelectedScript()
        {
            if (_scriptList.SelectedItems.Count == 0) return;
            Dictionary<string, object> map = _scriptList.SelectedItems[0].Tag as Dictionary<string, object>;
            if (map == null) return;
            string rel = Api.Str(map, "path", "");
            string full = Path.Combine(_kitRoot, "apps", "scriptvault", rel.Replace('/', '\\'));
            try
            {
                if (File.Exists(full))
                {
                    Process.Start("notepad.exe", "\"" + full + "\"");
                }
            }
            catch (Exception ex)
            {
                AddError("脚本库", "打开失败：" + ex.Message);
            }
        }

        // ---------------------------------------------------------- 仓库

        private Panel BuildRepos()
        {
            Panel panel = new Panel();

            Label title = Ui.Title("仓库与备份");
            title.Bounds = new Rectangle(0, 0, 320, 30);
            panel.Controls.Add(title);

            _repoSummary = Ui.Text("", Theme.Small, Theme.Faint);
            _repoSummary.Bounds = new Rectangle(0, 32, 760, 20);
            panel.Controls.Add(_repoSummary);

            _repoList = Ui.List("仓库", "分支", "提交", "未提交", "文件数", "最近提交");
            _repoList.Bounds = new Rectangle(0, 60, 900, 460);
            _repoList.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _repoList.Columns[0].Width = 140;
            _repoList.Columns[1].Width = 90;
            _repoList.Columns[2].Width = 90;
            _repoList.Columns[3].Width = 80;
            _repoList.Columns[4].Width = 80;
            _repoList.Columns[5].Width = 400;
            Draw.ListView(_repoList);
            panel.Controls.Add(_repoList);

            return panel;
        }

        // ---------------------------------------------------------- 看板

        private Panel BuildPulse()
        {
            Panel panel = new Panel();

            Label title = Ui.Title("运行看板");
            title.Bounds = new Rectangle(0, 0, 320, 30);
            panel.Controls.Add(title);

            Label sub = Ui.Text("来自 Pulse 的实时进程与任务视图；重复测试进程会自动告警。", Theme.Small, Theme.Faint);
            sub.Bounds = new Rectangle(0, 32, 700, 20);
            panel.Controls.Add(sub);

            _kpiJobs = MakeCard(panel, 0, 60, "运行中任务", "—");
            _kpiProcs = MakeCard(panel, 190, 60, "脚本进程", "—");
            _kpiCpu = MakeCard(panel, 380, 60, "CPU", "—");
            _kpiMem = MakeCard(panel, 570, 60, "内存", "—");

            _cpuChart = MakeChart(panel, 760, 60, "CPU 趋势");
            _memChart = MakeChart(panel, 922, 60, "内存趋势");

            _guardLabel = Ui.Text("", Theme.Small, Theme.Ok);
            _guardLabel.Bounds = new Rectangle(0, 158, 760, 22);
            panel.Controls.Add(_guardLabel);

            _procList = Ui.List("PID", "角色", "名称", "CPU", "内存", "命令行");
            _procList.Bounds = new Rectangle(0, 186, 900, 320);
            _procList.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _procList.Columns[0].Width = 70;
            _procList.Columns[1].Width = 90;
            _procList.Columns[2].Width = 160;
            _procList.Columns[3].Width = 70;
            _procList.Columns[4].Width = 90;
            _procList.Columns[5].Width = 400;
            Draw.ListView(_procList);
            panel.Controls.Add(_procList);

            return panel;
        }

        // ---------------------------------------------------------- 刷新

        private void Tick()
        {
            DateTime now = DateTime.Now;
            double gap = (now - _lastTick).TotalSeconds;
            _lastTick = now;
            if (gap > 20)
            {
                AddError("界面", "主线程阻塞约 " + Math.Round(gap) + " 秒，现已恢复");
            }
            RefreshAll();
        }

        private void RefreshAll()
        {
            bool okCv = Api.GetOk("http://127.0.0.1:" + _port + "/api/health", 1600);
            bool okPulse = Api.GetOk("http://127.0.0.1:" + _pulse + "/api/health", 1600);
            bool okTuner = Api.GetOk("http://127.0.0.1:" + _tuner + "/api/health", 1600);

            int up = (okCv ? 1 : 0) + (okPulse ? 1 : 0) + (okTuner ? 1 : 0);
            if (up < 3)
            {
                _failStreak += 1;
                if (_failStreak >= 3)
                {
                    AddError("服务", (3 - up) + " 个服务无响应（" + _port + "/" + _pulse + "/" + _tuner + "）");
                }
            }
            else
            {
                _failStreak = 0;
            }

            _upCount = up;
            SetStatus(up);

            // 仓库先刷：概览的「仓库」指标直接复用它的结果，避免再打一次慢接口
            RefreshRepos();
            RefreshPulse();
            RefreshOverview(okCv);
            RefreshProjects();
            RefreshScripts();

            if (_footRight != null)
            {
                _footRight.Text = string.Format(
                    "端口 {0} / {1} / {2} · 事件 {3} · ~{4} token · {5}",
                    _port, _pulse, _tuner, _usageEvents, _estTokens, DateTime.Now.ToString("HH:mm:ss"));
            }
        }

        private void SetStatus(int up)
        {
            if (_statusLabel == null) return;
            string text;
            Color color;
            if (up == 3)
            {
                text = "三个服务全部在线 · 索引库 " + _port;
                color = Theme.Ok;
            }
            else if (up > 0)
            {
                text = up + "/3 服务在线（" + _port + " / " + _pulse + " / " + _tuner + ")";
                color = Theme.Warn;
            }
            else
            {
                text = "服务全部无响应，可在「设置」重启";
                color = Theme.Err;
            }
            if (text != _lastStatusText)
            {
                _lastStatusText = text;
                _statusLabel.Text = text;
                _statusLabel.ForeColor = color;
            }
        }

        private void RefreshOverview(bool online)
        {
            if (!online) return;

            Dictionary<string, object> projects = Api.GetObject("http://127.0.0.1:" + _port + "/api/projects", 2500);
            Dictionary<string, object> data = Api.Map(projects, "data");
            List<object> list = Api.AsList(data != null && data.ContainsKey("projects") ? data["projects"] : null);

            if (_kpiProjects != null) _kpiProjects.Text = list.Count.ToString();

            if (_recentProjects != null)
            {
                ListSync.Freeze(_recentProjects); _recentProjects.BeginUpdate();
                _recentProjects.Items.Clear();
                int shown = 0;
                foreach (object item in list)
                {
                    Dictionary<string, object> map = item as Dictionary<string, object>;
                    if (map == null) continue;
                    ListViewItem row = new ListViewItem(Api.Str(map, "name", Api.Str(map, "id", "-")));
                    row.SubItems.Add(Api.Str(map, "kind", "-"));
                    row.SubItems.Add(Api.Str(map, "status", "-"));
                    row.SubItems.Add(Api.Str(map, "summary", ""));
                    row.Tag = map;
                    _recentProjects.Items.Add(row);
                    shown += 1;
                    if (shown >= 40) break;
                }
                _recentProjects.EndUpdate();
                ListSync.Thaw(_recentProjects);
            }

            Dictionary<string, object> scripts = Api.GetObject("http://127.0.0.1:" + _port + "/api/scripts", 3000);
            Dictionary<string, object> vault = Api.Map(scripts, "vault");
            Dictionary<string, object> summary = Api.Map(vault, "summary");
            int total = Api.Int(summary, "total", 0);
            if (_kpiScripts != null) _kpiScripts.Text = total.ToString();

            if (_kpiRepos != null)
            {
                _kpiRepos.Text = _repoList == null ? "—" : _repoList.Items.Count.ToString();
            }
            if (_kpiServices != null) _kpiServices.Text = _upCount + "/3";

            if (_statsLabel != null)
            {
                _statsLabel.Text = "项目 " + list.Count + "  ·  仓库 " + (_repoList == null ? 0 : _repoList.Items.Count) + "  ·  脚本 " + total;
            }
        }

        private void RefreshProjects()
        {
            if (_projectList == null) return;
            Dictionary<string, object> projects = Api.GetObject("http://127.0.0.1:" + _port + "/api/projects", 2500);
            Dictionary<string, object> data = Api.Map(projects, "data");
            List<object> list = Api.AsList(data != null && data.ContainsKey("projects") ? data["projects"] : null);

            if (_projectList.Items.Count == list.Count && _projectList.Items.Count > 0) return;

            _projectList.BeginUpdate();
            _projectList.Items.Clear();
            foreach (object item in list)
            {
                Dictionary<string, object> map = item as Dictionary<string, object>;
                if (map == null) continue;
                ListViewItem row = new ListViewItem(Api.Str(map, "name", Api.Str(map, "id", "-")));
                row.SubItems.Add(Api.Str(map, "kind", "-"));
                row.SubItems.Add(Api.Str(map, "status", "-"));
                row.SubItems.Add(Api.AsList(map.ContainsKey("issues") ? map["issues"] : null).Count.ToString());
                row.Tag = map;
                _projectList.Items.Add(row);
            }
            _projectList.EndUpdate();

            // 默认选中第一行，右侧详情不至于空着
            if (_projectList.Items.Count > 0 && _projectList.SelectedItems.Count == 0)
            {
                _projectList.Items[0].Selected = true;
                ShowProjectDetail();
            }
        }

        private void RefreshScripts()
        {
            if (_scriptList == null) return;

            Dictionary<string, object> scripts = Api.GetObject("http://127.0.0.1:" + _port + "/api/scripts", 3000);
            Dictionary<string, object> vault = Api.Map(scripts, "vault");
            if (vault == null) return;

            Dictionary<string, object> summary = Api.Map(vault, "summary");
            List<object> categories = Api.AsList(vault.ContainsKey("categories") ? vault["categories"] : null);
            List<object> entries = Api.AsList(vault.ContainsKey("entries") ? vault["entries"] : null);

            if (_scriptSummary != null)
            {
                _scriptSummary.Text = string.Format(
                    "共 {0} 个（模块 {1} · 工具 {2} · Python {3} · 测试 {4}）",
                    Api.Int(summary, "total", entries.Count),
                    Api.Int(summary, "modules", 0),
                    Api.Int(summary, "tools", 0),
                    Api.Int(summary, "python", 0),
                    Api.Int(summary, "tests", 0));
            }

            // 分类下拉只重建一次，之后保留用户选择
            if (_scriptCategory != null && _scriptCategory.Items.Count == 0)
            {
                _scriptCategory.Items.Add("全部");
                foreach (object item in categories)
                {
                    Dictionary<string, object> map = item as Dictionary<string, object>;
                    if (map != null) _scriptCategory.Items.Add(Api.Str(map, "label", Api.Str(map, "id", "-")));
                }
                _scriptCategory.SelectedIndex = 0;
            }

            string filter = _scriptCategory != null && _scriptCategory.SelectedIndex > 0
                ? Convert.ToString(_scriptCategory.SelectedItem)
                : "";

            string keepScript = ListSync.CaptureSelection(_scriptList); ListSync.Freeze(_scriptList); _scriptList.BeginUpdate();
            _scriptList.Items.Clear();
            foreach (object item in entries)
            {
                Dictionary<string, object> map = item as Dictionary<string, object>;
                if (map == null) continue;
                string categoryId = Api.Str(map, "category", "");
                string categoryLabel = CategoryLabel(categories, categoryId);
                if (filter.Length > 0 && categoryLabel != filter) continue;

                ListViewItem row = new ListViewItem(Api.Str(map, "name", "-"));
                row.SubItems.Add(categoryLabel);
                row.SubItems.Add(Api.Str(map, "purpose", ""));
                row.Tag = map;
                _scriptList.Items.Add(row);
            }
            _scriptList.EndUpdate(); ListSync.Thaw(_scriptList); ListSync.RestoreSelection(_scriptList, keepScript);
        }

        private static string CategoryLabel(List<object> categories, string id)
        {
            foreach (object item in categories)
            {
                Dictionary<string, object> map = item as Dictionary<string, object>;
                if (map == null) continue;
                if (Api.Str(map, "id", "") == id) return Api.Str(map, "label", id);
            }
            return id;
        }

        private void RefreshRepos()
        {
            if (_repoList == null) return;
            Dictionary<string, object> data = Api.GetObject("http://127.0.0.1:" + _port + "/api/repos", 6000);
            List<object> repos = Api.AsList(data != null && data.ContainsKey("repos") ? data["repos"] : null);

            if (_repoSummary != null)
            {
                int dirty = 0;
                foreach (object item in repos)
                {
                    Dictionary<string, object> map = item as Dictionary<string, object>;
                    if (map != null && Api.Int(map, "dirty", 0) > 0) dirty += 1;
                }
                _repoSummary.Text = string.Format("共 {0} 个仓库，{1} 个有未提交改动", repos.Count, dirty);
                _repoSummary.ForeColor = dirty > 0 ? Theme.Warn : Theme.Faint;
            }

            if (_repoList.Items.Count == repos.Count && repos.Count > 0) return;

            _repoList.BeginUpdate();
            _repoList.Items.Clear();
            foreach (object item in repos)
            {
                Dictionary<string, object> map = item as Dictionary<string, object>;
                if (map == null) continue;
                ListViewItem row = new ListViewItem(Api.Str(map, "name", "-"));
                row.SubItems.Add(Api.Str(map, "branch", "-"));
                row.SubItems.Add(Api.Str(map, "hash", "-"));
                int dirtyCount = Api.Int(map, "dirty", 0);
                row.SubItems.Add(dirtyCount.ToString());
                row.SubItems.Add(Api.Int(map, "tracked", 0).ToString());
                row.SubItems.Add(Api.Str(map, "subject", ""));
                if (dirtyCount > 0) row.ForeColor = Theme.Warn;
                row.Tag = map;
                _repoList.Items.Add(row);
            }
            _repoList.EndUpdate();

            if (_repoList.Items.Count > 0 && _repoList.SelectedItems.Count == 0)
            {
                _repoList.Items[0].Selected = true;
            }
        }

        private void RefreshPulse()
        {
            Dictionary<string, object> state = Api.GetObject("http://127.0.0.1:" + _pulse + "/api/state", 3000);
            if (state == null) return;

            Dictionary<string, object> summary = Api.Map(state, "summary");
            List<object> processes = Api.AsList(state.ContainsKey("processes") ? state["processes"] : null);
            Dictionary<string, object> guard = Api.Map(state, "guard");
            Dictionary<string, object> usage = Api.Map(state, "usage");
            if (usage != null)
            {
                _usageEvents = Api.Int(usage, "events", 0);
                _estTokens = Api.Int(usage, "estTokens", 0);
            }

            if (_kpiJobs != null) _kpiJobs.Text = Api.Int(summary, "running", 0).ToString();
            if (_kpiProcs != null) _kpiProcs.Text = processes.Count.ToString();

            double cpu = 0;
            double memMb = 0;
            foreach (object item in processes)
            {
                Dictionary<string, object> map = item as Dictionary<string, object>;
                if (map == null) continue;
                cpu += Api.Dbl(map, "cpuPercent", 0);
                memMb += Api.Dbl(map, "memMB", 0);
            }
            if (_kpiCpu != null) _kpiCpu.Text = Math.Round(cpu, 1) + "%";

            // 趋势线：只保留最近 HISTORY_LIMIT 次采样，内存占用恒定
            _cpuHistory.Add(cpu);
            while (_cpuHistory.Count > HISTORY_LIMIT) _cpuHistory.RemoveAt(0);
            if (_cpuChart != null) _cpuChart.Invalidate();
            if (_kpiMem != null) _kpiMem.Text = Math.Round(memMb, 0) + " MB";
            _memHistory.Add(memMb);
            while (_memHistory.Count > HISTORY_LIMIT) _memHistory.RemoveAt(0);
            if (_memChart != null) _memChart.Invalidate();

            if (_guardLabel != null && guard != null)
            {
                List<object> groups = Api.AsList(guard.ContainsKey("groups") ? guard["groups"] : null);
                int locked = 0;
                int warn = 0;
                foreach (object item in groups)
                {
                    Dictionary<string, object> map = item as Dictionary<string, object>;
                    if (map == null) continue;
                    string status = Api.Str(map, "status", "");
                    if (status == "locked") locked += 1;
                    else if (status == "warn") warn += 1;
                }
                if (locked > 0)
                {
                    _guardLabel.Text = "重复进程：" + locked + " 组已锁死并清理（累计清理 " + Api.Int(guard, "killed", 0) + " 个）";
                    _guardLabel.ForeColor = Theme.Err;
                }
                else if (warn > 0)
                {
                    _guardLabel.Text = "重复进程：" + warn + " 组达到告警阈值，继续增加会锁死";
                    _guardLabel.ForeColor = Theme.Warn;
                }
                else
                {
                    _guardLabel.Text = "重复进程：正常（阈值 " + Api.Int(guard, "warnAt", 4) + " / " + Api.Int(guard, "lockAt", 6) + "）";
                    _guardLabel.ForeColor = Theme.Ok;
                }
            }

            if (_procList == null) return;
            string keepProc = ListSync.CaptureSelection(_procList); ListSync.Freeze(_procList); _procList.BeginUpdate();
            _procList.Items.Clear();
            int shown = 0;
            foreach (object item in processes)
            {
                Dictionary<string, object> map = item as Dictionary<string, object>;
                if (map == null) continue;
                ListViewItem row = new ListViewItem(Api.Int(map, "pid", 0).ToString());
                row.SubItems.Add(Api.Str(map, "role", "-"));
                row.SubItems.Add(Api.Str(map, "name", "-"));
                row.SubItems.Add(Math.Round(Api.Dbl(map, "cpuPercent", 0), 1) + "%");
                row.SubItems.Add(Math.Round(Api.Dbl(map, "memMB", 0), 0) + " MB");
                string command = Api.Str(map, "command", "");
                if (command.Length > 90) command = command.Substring(0, 90) + "…";
                row.SubItems.Add(command);
                row.Tag = map;
                _procList.Items.Add(row);
                shown += 1;
                if (shown >= 60) break;
            }
            _procList.EndUpdate(); ListSync.Thaw(_procList); ListSync.RestoreSelection(_procList, keepProc);
        }

        // ---------------------------------------------------------- 报错

        private void AddError(string source, string message)
        {
            for (int i = _errors.Count - 1; i >= 0 && i >= _errors.Count - 8; i -= 1)
            {
                ShellError old = _errors[i];
                if (old.Source == source && old.Message == message && (DateTime.Now - old.At).TotalSeconds < 30) return;
            }
            _errors.Add(new ShellError { At = DateTime.Now, Source = source, Message = message });
            while (_errors.Count > 200) _errors.RemoveAt(0);

            if (_errorBadge != null)
            {
                _errorBadge.Text = _errors.Count > 99 ? "99+" : _errors.Count.ToString();
                _errorBadge.Visible = _errors.Count > 0;
                _errorBadge.Size = new Size(_errors.Count > 9 ? 26 : 20, 15);
            }
            if (_errorButton != null)
            {
                _errorButton.BackColor = _errors.Count > 0 ? Theme.ErrSoft : Theme.Card;
                _errorText.ForeColor = _errors.Count > 0 ? Color.White : Theme.Dim;
            }
        }

        private void ShowErrors()
        {
            Form dlg = new Form();
            dlg.Text = "错误与告警 · " + _errors.Count + " 条";
            dlg.Size = new Size(780, 480);
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.BackColor = Theme.Bg;
            dlg.ForeColor = Theme.Text;
            dlg.Font = Theme.Body;

            TextBox box = new TextBox();
            box.Multiline = true;
            box.ReadOnly = true;
            box.ScrollBars = ScrollBars.Both;
            box.WordWrap = false;
            box.Dock = DockStyle.Fill;
            box.BackColor = Theme.Panel;
            box.ForeColor = Theme.Text;
            box.BorderStyle = BorderStyle.None;
            box.Font = Theme.Mono;

            if (_errors.Count == 0)
            {
                box.Text =
                    "暂无错误。\r\n\r\n记录范围：\r\n" +
                    "  · 服务健康检查连续失败\r\n" +
                    "  · 接口返回异常 / 数据无法解析\r\n" +
                    "  · 主线程阻塞超过 20 秒\r\n" +
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
            bar.BackColor = Theme.Panel;
            dlg.Controls.Add(bar);

            Button clear = Ui.Flat("清空", 80, 28);
            clear.Location = new Point(14, 9);
            clear.Click += delegate
            {
                _errors.Clear();
                AddError("占位", "清除后重新计数");   // 触发徽章刷新
                _errors.Clear();
                if (_errorBadge != null) _errorBadge.Visible = false;
                if (_errorButton != null) _errorButton.BackColor = Theme.Card;
                if (_errorText != null) _errorText.ForeColor = Theme.Dim;
                dlg.Close();
            };
            bar.Controls.Add(clear);

            Button copy = Ui.Flat("复制全部", 92, 28);
            copy.Location = new Point(102, 9);
            copy.Click += delegate { try { Clipboard.SetText(box.Text); } catch { } };
            bar.Controls.Add(copy);

            Button close = Ui.Flat("关闭", 80, 28);
            close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            close.Location = new Point(bar.ClientSize.Width - 94, 9);
            close.Click += delegate { dlg.Close(); };
            bar.Controls.Add(close);

            dlg.ShowDialog(this);
        }

        // ---------------------------------------------------------- 设置

        private void ShowSettings()
        {
            Form dlg = new Form();
            dlg.Text = "设置 · CodeBuddy 工作套件";
            dlg.Size = new Size(680, 660);
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.MaximizeBox = false;
            dlg.MinimizeBox = false;
            dlg.BackColor = Theme.Bg;
            dlg.ForeColor = Theme.Text;
            dlg.Font = Theme.Body;

            Label head = Ui.Text("服务端口", Theme.Section, Theme.Accent);
            head.Bounds = new Rectangle(24, 18, 300, 24);
            dlg.Controls.Add(head);

            Label hint = Ui.Text("改完保存会重启三个服务；端口被占用时会提示更换。", Theme.Small, Theme.Faint);
            hint.Bounds = new Rectangle(24, 44, 600, 20);
            dlg.Controls.Add(hint);

            NumericUpDown cv = MakePort(dlg, 24, 74, "索引库", _port);
            NumericUpDown pu = MakePort(dlg, 24, 112, "看板", _pulse);
            NumericUpDown tu = MakePort(dlg, 24, 150, "降温", _tuner);

            Label hostLabel = Ui.Text("监听地址", Theme.Small, Theme.Text);
            hostLabel.Bounds = new Rectangle(24, 192, 80, 22);
            dlg.Controls.Add(hostLabel);

            ComboBox hostBox = new ComboBox();
            hostBox.DropDownStyle = ComboBoxStyle.DropDownList;
            hostBox.Bounds = new Rectangle(112, 190, 300, 24);
            hostBox.FlatStyle = FlatStyle.Flat;
            hostBox.BackColor = Theme.Card;
            hostBox.ForeColor = Theme.Text;
            hostBox.Items.Add("127.0.0.1（仅本机）");
            hostBox.Items.Add("0.0.0.0（局域网可访问）");
            hostBox.SelectedIndex = _bindHost == "0.0.0.0" ? 1 : 0;
            dlg.Controls.Add(hostBox);

            Label extHead = Ui.Text("可外接内容", Theme.Section, Theme.Accent);
            extHead.Bounds = new Rectangle(24, 230, 300, 24);
            dlg.Controls.Add(extHead);

            TextBox ext = new TextBox();
            ext.Multiline = true;
            ext.ReadOnly = true;
            ext.ScrollBars = ScrollBars.Vertical;
            ext.BorderStyle = BorderStyle.None;
            ext.BackColor = Theme.Panel;
            ext.ForeColor = Theme.Text;
            ext.Font = Theme.Mono;
            ext.Bounds = new Rectangle(24, 258, 612, 190);
            ext.Text = BuildEndpointsText();
            dlg.Controls.Add(ext);

            Label hcHead = Ui.Text("环境检测", Theme.Section, Theme.Accent);
            hcHead.Bounds = new Rectangle(24, 458, 200, 24);
            dlg.Controls.Add(hcHead);

            TextBox hcOut = new TextBox();
            hcOut.Multiline = true;
            hcOut.ReadOnly = true;
            hcOut.ScrollBars = ScrollBars.Vertical;
            hcOut.BorderStyle = BorderStyle.None;
            hcOut.BackColor = Theme.Panel;
            hcOut.ForeColor = Theme.Dim;
            hcOut.Font = Theme.Mono;
            hcOut.Bounds = new Rectangle(24, 484, 612, 66);
            hcOut.Text = "点右侧「运行检测」检查文件与功能完整性。";
            dlg.Controls.Add(hcOut);

            Button run = Ui.Flat("运行检测", 96, 26);
            run.Location = new Point(432, 456);
            run.Click += delegate
            {
                hcOut.Text = "正在检测…";
                run.Enabled = false;
                hcOut.Text = RunHealthCheck();
                run.Enabled = true;
            };
            dlg.Controls.Add(run);

            Button folder = Ui.Flat("打开目录", 88, 26);
            folder.Location = new Point(536, 456);
            folder.Click += delegate { try { Process.Start("explorer.exe", _kitRoot); } catch { } };
            dlg.Controls.Add(folder);

            Button save = Ui.Primary("保存并重启", 112, 30);
            save.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            save.Location = new Point(dlg.ClientSize.Width - 260, dlg.ClientSize.Height - 48);
            save.Click += delegate
            {
                string error;
                if (!SaveConfig((int)cv.Value, (int)pu.Value, (int)tu.Value,
                        hostBox.SelectedIndex == 1 ? "0.0.0.0" : "127.0.0.1", out error))
                {
                    MessageBox.Show(this, "保存失败：" + error, "设置", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    AddError("设置", "保存失败：" + error);
                    return;
                }
                dlg.Close();
                RestartServices();
            };
            dlg.Controls.Add(save);

            Button cancel = Ui.Flat("取消", 88, 30);
            cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            cancel.Location = new Point(dlg.ClientSize.Width - 138, dlg.ClientSize.Height - 48);
            cancel.Click += delegate { dlg.Close(); };
            dlg.Controls.Add(cancel);

            dlg.ShowDialog(this);
        }

        private NumericUpDown MakePort(Form dlg, int x, int y, string label, int value)
        {
            Label tag = Ui.Text(label, Theme.Small, Theme.Text);
            tag.Bounds = new Rectangle(x, y + 3, 80, 22);
            dlg.Controls.Add(tag);

            NumericUpDown box = new NumericUpDown();
            box.Minimum = 1024;
            box.Maximum = 65535;
            box.Value = value;
            box.Bounds = new Rectangle(x + 88, y, 120, 24);
            box.BackColor = Theme.Card;
            box.ForeColor = Theme.Text;
            box.BorderStyle = BorderStyle.FixedSingle;
            dlg.Controls.Add(box);
            return box;
        }

        private string BuildEndpointsText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("浏览器 / 其他设备可访问：");
            sb.AppendLine("  索引库    http://127.0.0.1:" + _port + "/   接口 /api/repos /api/scripts /api/projects");
            sb.AppendLine("  看板      http://127.0.0.1:" + _pulse + "/   接口 /api/state /api/guard");
            sb.AppendLine("  降温      http://127.0.0.1:" + _tuner + "/");
            sb.AppendLine("");
            sb.AppendLine("监听地址改 0.0.0.0 后，同一局域网用本机 IP 即可访问。");
            sb.AppendLine("接项目：node apps\\scriptvault\\tools\\adopt-project.mjs \"<目录>\" --name <简称>");
            sb.AppendLine("推进度：node apps\\pulse\\cli\\pulse.js run -- <命令>");
            sb.AppendLine("");
            sb.AppendLine("本机目录：" + _kitRoot);
            return sb.ToString();
        }

        private string RunHealthCheck()
        {
            string script = Path.Combine(_kitRoot, "tools", "health-check.ps1");
            if (!File.Exists(script)) return "未找到 tools\\health-check.ps1";

            ProcessStartInfo psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -Target \"" + _kitRoot + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.StandardOutputEncoding = Encoding.UTF8;

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

                Dictionary<string, object> ports = Api.Map(map, "ports");
                if (ports == null)
                {
                    ports = new Dictionary<string, object>();
                    map["ports"] = ports;
                }
                ports["codevault"] = codevault;
                ports["pulse"] = pulse;
                ports["tuner"] = tuner;

                File.WriteAllText(path, ser.Serialize(map), new UTF8Encoding(false));

                _port = codevault;
                _pulse = pulse;
                _tuner = tuner;
                _bindHost = host;
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
                _failStreak = 0;
                RefreshAll();
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

            ProcessStartInfo psi = new ProcessStartInfo(node,
                "\"" + Path.Combine(_kitRoot, "tools", script) + "\" " + args);
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

        private void OpenExternal(string url)
        {
            try { Process.Start(url); }
            catch (Exception ex) { AddError("打开链接", ex.Message); }
        }
    }

}
