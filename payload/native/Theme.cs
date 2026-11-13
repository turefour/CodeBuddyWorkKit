using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WorkKit.Native
{
    /// <summary>
    /// 深色主题。配色对齐浏览器端（GitHub Dark 系）。
    ///
    /// 内存约定：字体和画刷都是**进程级单例**，全部 static readonly。
    /// 早期版本写成 `public static Font Body { get { return new Font(...); } }`，
    /// 每次访问都新建一个 Font —— 在 DrawSubItem 这种每行每列都会走的路径上，
    /// 一遍刷新就能创建上百个 GDI 对象，句柄数只增不减。
    /// </summary>
    internal static class Theme
    {
        // ---------------------------------------------------------------- 配色
        internal static readonly Color Bg = Color.FromArgb(0x0D, 0x11, 0x17);
        internal static readonly Color Panel = Color.FromArgb(0x16, 0x1B, 0x22);
        internal static readonly Color Card = Color.FromArgb(0x1C, 0x23, 0x2C);
        internal static readonly Color CardAlt = Color.FromArgb(0x21, 0x29, 0x33);
        internal static readonly Color Border = Color.FromArgb(0x30, 0x36, 0x3D);
        internal static readonly Color Text = Color.FromArgb(0xC9, 0xD1, 0xD9);
        internal static readonly Color Dim = Color.FromArgb(0x8B, 0x94, 0x9E);
        internal static readonly Color Faint = Color.FromArgb(0x6E, 0x76, 0x81);
        internal static readonly Color Accent = Color.FromArgb(0x1F, 0x6F, 0xEB);
        internal static readonly Color AccentSoft = Color.FromArgb(0x1A, 0x2F, 0x4D);
        internal static readonly Color Ok = Color.FromArgb(0x3F, 0xB9, 0x50);
        internal static readonly Color Warn = Color.FromArgb(0xD2, 0x99, 0x22);
        internal static readonly Color Err = Color.FromArgb(0xF8, 0x51, 0x49);
        internal static readonly Color ErrSoft = Color.FromArgb(0x3D, 0x1E, 0x1E);
        internal static readonly Color ChipBg = Color.FromArgb(0x21, 0x29, 0x33);

        internal const string FontName = "Microsoft YaHei UI";

        // ---------------------------------------------------------------- 字体（单例）
        internal static readonly Font Body = new Font(FontName, 9F, FontStyle.Regular);
        internal static readonly Font Small = new Font(FontName, 8.5F, FontStyle.Regular);
        internal static readonly Font SmallBold = new Font(FontName, 8.5F, FontStyle.Bold);
        internal static readonly Font Tiny = new Font(FontName, 8F, FontStyle.Regular);
        internal static readonly Font Section = new Font(FontName, 9.5F, FontStyle.Bold);
        internal static readonly Font Title = new Font(FontName, 15F, FontStyle.Bold);
        internal static readonly Font Brand = new Font(FontName, 11F, FontStyle.Bold);
        internal static readonly Font Kpi = new Font(FontName, 19F, FontStyle.Bold);
        internal static readonly Font Mono = new Font("Consolas", 8.5F, FontStyle.Regular);

        /// <summary>
        /// 非常用字号才走这里；高频绘制路径请直接用上面的静态字段。
        /// 调用方负责在窗口关闭时 Dispose（本程序只有对话框会用到）。
        /// </summary>
        internal static Font Font(float size, FontStyle style)
        {
            return new Font(FontName, size, style);
        }

        // ---------------------------------------------------------------- 画刷 / 画笔缓存
        //
        // 按颜色缓存，全进程共享。自绘列表每行要填背景、画分隔线，
        // 每次 new SolidBrush 会在几秒内堆积上千个 GDI 句柄。
        private static readonly Dictionary<int, SolidBrush> BrushCache = new Dictionary<int, SolidBrush>();
        private static readonly Dictionary<int, Pen> PenCache = new Dictionary<int, Pen>();

        internal static SolidBrush Brush(Color color)
        {
            int key = color.ToArgb();
            SolidBrush brush;
            if (BrushCache.TryGetValue(key, out brush)) return brush;
            brush = new SolidBrush(color);
            BrushCache[key] = brush;
            return brush;
        }

        internal static Pen Pen(Color color, float width = 1F)
        {
            int key = color.ToArgb() ^ (int)(width * 100) << 24;
            Pen pen;
            if (PenCache.TryGetValue(key, out pen)) return pen;
            pen = new Pen(color, width);
            PenCache[key] = pen;
            return pen;
        }

        /// <summary>Tiny 字号用于卡片副标题，避免另开一个 Font 字段。</summary>
        internal static Font CardMeta { get { return Tiny; } }
    }

    /// <summary>
    /// 双缓冲 ListView。
    /// OwnerDraw 的列表在频繁刷新时，默认实现会「擦背景 → 逐行重画」，
    /// 中间那一帧就是肉眼看到的闪白。开双缓冲后整块先画到离屏位图再一次性提交。
    /// </summary>
    internal sealed class BufferedListView : ListView
    {
        internal BufferedListView()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            SetStyle(ControlStyles.UserPaint, false);
        }
    }

    /// <summary>
    /// 批量更新 ListView：冻结重绘 → 改数据 → 恢复并刷新一次。
    ///
    /// BeginUpdate 只是延迟绘制，Clear() 仍会擦除背景，中间态依然可见。
    /// WM_SETREDRAW 把绘制整个关掉，改完一次性呈现，这是消除闪烁的关键。
    /// </summary>
    internal static class ListSync
    {
        private const int WM_SETREDRAW = 0x000B;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        internal static void Freeze(ListView list)
        {
            try
            {
                if (list != null && list.IsHandleCreated) SendMessage(list.Handle, WM_SETREDRAW, 0, 0);
            }
            catch
            {
                /* 冻结失败只影响观感 */
            }
        }

        internal static void Thaw(ListView list)
        {
            try
            {
                if (list == null || !list.IsHandleCreated) return;
                SendMessage(list.Handle, WM_SETREDRAW, 1, 0);
                list.Invalidate(true);
                list.Update();
            }
            catch
            {
                /* ignore */
            }
        }

        /// <summary>选中行的首列文本，用于重建后恢复选中。</summary>
        internal static string CaptureSelection(ListView list)
        {
            if (list == null || list.SelectedItems.Count == 0) return null;
            return list.SelectedItems[0].Text;
        }

        /// <summary>按首列文本恢复选中；找不到就退回首行。</summary>
        internal static void RestoreSelection(ListView list, string key)
        {
            if (list == null || list.Items.Count == 0) return;

            if (!string.IsNullOrEmpty(key))
            {
                foreach (ListViewItem item in list.Items)
                {
                    if (item.Text == key)
                    {
                        item.Selected = true;
                        item.Focused = true;
                        return;
                    }
                }
            }
            if (list.SelectedItems.Count == 0) list.Items[0].Selected = true;
        }
    }

    /// <summary>控件工厂：把重复的「深色外观设置」收敛到一处。</summary>
    internal static class Ui
    {
        internal static Label Text(string caption, Font font, Color color)
        {
            Label label = new Label();
            label.Text = caption;
            label.Font = font;
            label.ForeColor = color;
            label.BackColor = Color.Transparent;
            label.AutoSize = false;
            return label;
        }

        internal static Label Title(string caption)
        {
            return Text(caption, Theme.Title, Theme.Text);
        }

        internal static Button Flat(string caption, int width, int height)
        {
            Button button = new Button();
            button.Text = caption;
            button.Size = new Size(width, height);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = Theme.Border;
            button.BackColor = Theme.Card;
            button.ForeColor = Theme.Text;
            button.Font = Theme.Small;
            button.Cursor = Cursors.Hand;
            button.UseVisualStyleBackColor = false;
            return button;
        }

        internal static Button Primary(string caption, int width, int height)
        {
            Button button = Flat(caption, width, height);
            button.BackColor = Theme.Accent;
            button.ForeColor = Color.White;
            button.FlatAppearance.BorderSize = 0;
            return button;
        }

        /// <summary>深色 ListView：整行选中、无网格线、双缓冲。</summary>
        internal static ListView List(params string[] columns)
        {
            ListView list = new BufferedListView();
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.MultiSelect = false;
            list.GridLines = false;
            list.BorderStyle = BorderStyle.None;
            list.BackColor = Theme.Panel;
            list.ForeColor = Theme.Text;
            list.Font = Theme.Small;
            list.OwnerDraw = true;
            list.HeaderStyle = ColumnHeaderStyle.Nonclickable;

            foreach (string column in columns)
            {
                list.Columns.Add(column, 140, HorizontalAlignment.Left);
            }
            return list;
        }
    }

    /// <summary>自绘 ListView：深色表头 + 交替行 + 选中高亮。</summary>
    internal static class Draw
    {
        internal static void ListView(ListView control)
        {
            control.DrawColumnHeader += delegate(object s, DrawListViewColumnHeaderEventArgs e)
            {
                e.Graphics.FillRectangle(Theme.Brush(Theme.CardAlt), e.Bounds);
                e.Graphics.DrawLine(
                    Theme.Pen(Theme.Border),
                    e.Bounds.Left,
                    e.Bounds.Bottom - 1,
                    e.Bounds.Right,
                    e.Bounds.Bottom - 1);

                TextRenderer.DrawText(
                    e.Graphics, e.Header.Text, Theme.SmallBold, e.Bounds, Theme.Dim,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };

            control.DrawItem += delegate(object s, DrawListViewItemEventArgs e)
            {
                // 必须显式关掉默认绘制：只置 OwnerDraw=true 时，系统仍会在
                // 「获得焦点 / 首次点击」这类时机用默认高亮色画一遍选中行，
                // 表现就是「点进去变系统蓝，鼠标划过才恢复自定义色」。
                e.DrawDefault = false;

                bool selected = e.Item.Selected || (e.State & ListViewItemStates.Selected) != 0;
                Color back = selected
                    ? Theme.AccentSoft
                    : (e.ItemIndex % 2 == 0 ? Theme.Panel : Theme.Card);

                // 铺满整行宽度：列宽之和通常小于客户区，右侧空白不填就会露出系统底色
                int rowWidth = Math.Max(control.ClientSize.Width, e.Bounds.Right);
                e.Graphics.FillRectangle(
                    Theme.Brush(back),
                    new Rectangle(0, e.Bounds.Top, rowWidth, e.Bounds.Height));

                if (selected)
                {
                    e.Graphics.FillRectangle(
                        Theme.Brush(Theme.Accent),
                        new Rectangle(0, e.Bounds.Top, 2, e.Bounds.Height));
                }
            };

            control.DrawSubItem += delegate(object s, DrawListViewSubItemEventArgs e)
            {
                e.DrawDefault = false;

                // 用 Item.Selected 而不是 ItemState：后者在部分重绘路径上不准，
                // 会导致选中行某些列仍按「未选中」上色，看起来花屏。
                bool selected = e.Item.Selected;
                Color back = selected
                    ? Theme.AccentSoft
                    : (e.ItemIndex % 2 == 0 ? Theme.Panel : Theme.Card);
                e.Graphics.FillRectangle(Theme.Brush(back), e.Bounds);

                Color fore = (e.Item.ForeColor == Color.Empty || e.Item.ForeColor == Theme.Text)
                    ? Theme.Text
                    : e.Item.ForeColor;
                TextRenderer.DrawText(
                    e.Graphics, e.SubItem.Text, Theme.Small,
                    new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height),
                    fore,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };

            // 选中项变化后全量重绘一次，避免局部重绘留下旧颜色
            control.SelectedIndexChanged += delegate { control.Invalidate(); };

            control.Resize += delegate
            {
                if (control.Columns.Count == 0) return;
                int fixedWidth = 0;
                for (int i = 0; i < control.Columns.Count - 1; i += 1) fixedWidth += control.Columns[i].Width;
                // 最后一列吃掉剩余宽度：留白会在表头右侧露出系统浅色背景
                int last = control.ClientSize.Width - fixedWidth - 2;
                if (last > 60) control.Columns[control.Columns.Count - 1].Width = last;
            };
        }

        /// <summary>
        /// 带左侧色条的卡片。用自绘代替「Panel + 多个 Label」，
        /// 每张卡片只占一个控件句柄；100 个项目就是 100 个句柄 vs 400 个。
        /// </summary>
        internal static void Card(Graphics g, Rectangle bounds, string title, string value, Color accent)
        {
            g.FillRectangle(Theme.Brush(Theme.Card), bounds);
            g.DrawRectangle(Theme.Pen(Theme.Border), bounds.Left, bounds.Top, bounds.Width - 1, bounds.Height - 1);
            g.FillRectangle(Theme.Brush(accent), new Rectangle(bounds.Left, bounds.Top, 3, bounds.Height));

            TextRenderer.DrawText(
                g, title, Theme.Tiny,
                new Rectangle(bounds.Left + 14, bounds.Top + 10, bounds.Width - 20, 18),
                Theme.Dim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            TextRenderer.DrawText(
                g, value, Theme.Kpi,
                new Rectangle(bounds.Left + 14, bounds.Top + 30, bounds.Width - 20, 34),
                Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        /// <summary>小圆角标签（用矩形近似，避免 Region 带来的额外 GDI 对象）。</summary>
        internal static void Chip(Graphics g, int x, int y, string text, Color fore, Color back)
        {
            Size size = TextRenderer.MeasureText(text, Theme.Tiny);
            Rectangle box = new Rectangle(x, y, size.Width + 14, 20);
            g.FillRectangle(Theme.Brush(back), box);
            g.DrawRectangle(Theme.Pen(Theme.Border), box.Left, box.Top, box.Width - 1, box.Height - 1);
            TextRenderer.DrawText(
                g, text, Theme.Tiny,
                new Rectangle(box.Left + 7, box.Top, box.Width - 10, box.Height),
                fore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        /// <summary>
        /// 迷你趋势线。只画折线 + 填充，不建控件、不建 Bitmap ——
        /// 直接在当前 Graphics 上画，每帧零额外 GDI 对象。
        /// </summary>
        internal static void Sparkline(
            Graphics g, Rectangle bounds, IList<double> values, Color color, string caption, string current)
        {
            g.FillRectangle(Theme.Brush(Theme.Card), bounds);
            g.DrawRectangle(Theme.Pen(Theme.Border), bounds.Left, bounds.Top, bounds.Width - 1, bounds.Height - 1);

            TextRenderer.DrawText(
                g, caption, Theme.Tiny,
                new Rectangle(bounds.Left + 9, bounds.Top + 6, bounds.Width - 16, 16),
                Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            TextRenderer.DrawText(
                g, current, Theme.SmallBold,
                new Rectangle(bounds.Left + 9, bounds.Top + 22, bounds.Width - 16, 18),
                color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            Rectangle plot = new Rectangle(
                bounds.Left + 9, bounds.Top + 44, bounds.Width - 18, bounds.Height - 52);
            if (plot.Width < 10 || plot.Height < 8) return;

            if (values == null || values.Count < 2)
            {
                TextRenderer.DrawText(
                    g, "采样中…", Theme.Tiny, plot, Theme.Faint,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                return;
            }

            double max = 0.0001;
            foreach (double value in values) if (value > max) max = value;
            // 留 15% 顶部余量，曲线不会贴着上边框
            max *= 1.15;

            Point[] points = new Point[values.Count];
            double stepX = plot.Width / (double)(values.Count - 1);
            for (int i = 0; i < values.Count; i += 1)
            {
                double ratio = Math.Max(0, Math.Min(1, values[i] / max));
                points[i] = new Point(
                    plot.Left + (int)Math.Round(i * stepX),
                    plot.Bottom - (int)Math.Round(ratio * (plot.Height - 2)));
            }

            // 面积填充（浅色）+ 折线（实色）
            using (SolidBrush area = new SolidBrush(Color.FromArgb(38, color)))
            {
                System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath();
                path.AddLines(points);
                path.AddLine(points[points.Length - 1].X, plot.Bottom, points[0].X, plot.Bottom);
                path.CloseFigure();
                g.FillPath(area, path);
                path.Dispose();
            }
            g.DrawLines(Theme.Pen(color, 1.4F), points);

            // 末端点，强调「当前值」
            Point last = points[points.Length - 1];
            g.FillEllipse(Theme.Brush(color), last.X - 2, last.Y - 2, 5, 5);
        }

        /// <summary>分区标题：左侧 3px 竖条 + 粗体标题（对齐网页「构建逻辑」卡片）。</summary>
        internal static void SectionHeader(Graphics g, int x, int y, int width, string title)
        {
            g.FillRectangle(Theme.Brush(Theme.Accent), new Rectangle(x, y + 3, 3, 16));
            TextRenderer.DrawText(
                g, title, Theme.Section,
                new Rectangle(x + 10, y, width - 12, 22),
                Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }
    }
}
