using System;
using System.Drawing;
using System.Windows.Forms;

namespace WorkKit.Native
{
    /// <summary>
    /// 深色主题配色。和网页版同一套色值，切过来不会有割裂感。
    /// 纯 WinForms，不依赖任何浏览器或第三方控件。
    /// </summary>
    internal static class Theme
    {
        internal static readonly Color Bg = Color.FromArgb(0x12, 0x18, 0x20);
        internal static readonly Color Panel = Color.FromArgb(0x1B, 0x24, 0x30);
        internal static readonly Color Card = Color.FromArgb(0x22, 0x2D, 0x3B);
        internal static readonly Color Border = Color.FromArgb(0x2C, 0x38, 0x48);
        internal static readonly Color Text = Color.FromArgb(0xD2, 0xDC, 0xE8);
        internal static readonly Color Dim = Color.FromArgb(0x8A, 0x98, 0xAA);
        internal static readonly Color Faint = Color.FromArgb(0x5E, 0x6B, 0x7C);
        internal static readonly Color Accent = Color.FromArgb(0x2E, 0x75, 0xB6);
        internal static readonly Color AccentSoft = Color.FromArgb(0x1C, 0x3A, 0x5A);
        internal static readonly Color Ok = Color.FromArgb(0x4C, 0xA6, 0x72);
        internal static readonly Color Warn = Color.FromArgb(0xE0, 0xA8, 0x3E);
        internal static readonly Color Err = Color.FromArgb(0xD0, 0x54, 0x54);
        internal static readonly Color ErrSoft = Color.FromArgb(0x3A, 0x1C, 0x1C);

        internal const string FontName = "Microsoft YaHei UI";

        internal static Font Font(float size, FontStyle style)
        {
            return new Font(FontName, size, style);
        }

        internal static Font Body { get { return Font(9F, FontStyle.Regular); } }
        internal static Font Small { get { return Font(8.5F, FontStyle.Regular); } }
        internal static Font SmallBold { get { return Font(8.5F, FontStyle.Bold); } }
        internal static Font Title { get { return Font(12F, FontStyle.Bold); } }
        internal static Font Section { get { return Font(10F, FontStyle.Bold); } }
        internal static Font Mono { get { return new Font("Consolas", 8.5F); } }
        internal static Font MonoBig { get { return new Font("Consolas", 11F, FontStyle.Bold); } }
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

        /// <summary>深色 ListView：整行选中、无网格线、双列可排序外观。</summary>
        internal static ListView List(params string[] columns)
        {
            ListView list = new ListView();
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

        /// <summary>自绘 ListView：表头 + 交替行 + 选中高亮。</summary>
        internal static void DrawListView(ListView list, DrawListViewItemEventArgs e, bool isSelected)
        {
            Color back = isSelected ? Theme.AccentSoft : (e.ItemIndex % 2 == 0 ? Theme.Panel : Theme.Card);
            using (SolidBrush brush = new SolidBrush(back))
            {
                e.Graphics.FillRectangle(brush, e.Bounds);
            }
        }
    }
}
