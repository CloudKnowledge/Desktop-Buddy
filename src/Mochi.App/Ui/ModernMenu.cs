using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;
using Mochi.App.Platform;

namespace Mochi.App.Ui;

/// <summary>Windows 11-style context menus: rounded corners, soft highlight, light/dark aware.</summary>
internal static class ModernMenu
{
    public static bool IsDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }

    public static void Style(ToolStripDropDown menu)
    {
        bool dark = IsDarkTheme();
        menu.Renderer = new Renderer(dark);
        menu.Font = new Font("Segoe UI", 9.5f);
        menu.ForeColor = dark ? Color.FromArgb(0xF2, 0xF2, 0xF2) : Color.FromArgb(0x1B, 0x1B, 0x1F);
        menu.BackColor = dark ? Color.FromArgb(0x2B, 0x2B, 0x2F) : Color.FromArgb(0xFB, 0xFB, 0xFD);
        menu.Padding = new Padding(2, 4, 2, 4);
        if (menu is ToolStripDropDownMenu dd) dd.ShowImageMargin = true;
        RoundCorners(menu);
    }

    public static void StyleItems(ToolStripItemCollection items, ToolStripDropDown owner)
    {
        foreach (ToolStripItem item in items)
        {
            item.ForeColor = owner.ForeColor;
            if (item is ToolStripMenuItem mi)
            {
                mi.Padding = new Padding(2, 4, 2, 4);
                if (mi.HasDropDownItems)
                {
                    Style(mi.DropDown);
                    StyleItems(mi.DropDownItems, mi.DropDown);
                }
            }
        }
    }

    static void RoundCorners(ToolStripDropDown menu)
    {
        void Apply()
        {
            int pref = Native.DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(menu.Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        if (menu.IsHandleCreated) Apply();
        else menu.HandleCreated += (_, _) => Apply();
    }

    sealed class Renderer(bool dark) : ToolStripProfessionalRenderer(new Palette(dark))
    {
        readonly Color _hover = dark ? Color.FromArgb(0x3A, 0x3A, 0x40) : Color.FromArgb(0xEC, 0xEE, 0xF4);
        readonly Color _accent = Color.FromArgb(0xE0, 0x9A, 0x3E);
        readonly Color _muted = dark ? Color.FromArgb(0x9A, 0x9A, 0xA2) : Color.FromArgb(0x7A, 0x7A, 0x84);

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundRect(new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2), 5);
            using var brush = new SolidBrush(_hover);
            g.FillPath(brush, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (!e.Item.Enabled) e.TextColor = _muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var pen = new Pen(dark ? Color.FromArgb(0x45, 0x45, 0x4C) : Color.FromArgb(0xE3, 0xE4, 0xEA));
            e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            using var pen = new Pen(_accent, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f;
            g.DrawLines(pen, [new PointF(cx - 5, cy), new PointF(cx - 1.5f, cy + 3.5f), new PointF(cx + 5, cy - 4)]);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = _muted;
            base.OnRenderArrow(e);
        }

        static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    sealed class Palette(bool dark) : ProfessionalColorTable
    {
        readonly Color _bg = dark ? Color.FromArgb(0x2B, 0x2B, 0x2F) : Color.FromArgb(0xFB, 0xFB, 0xFD);
        public override Color ToolStripDropDownBackground => _bg;
        public override Color ImageMarginGradientBegin => _bg;
        public override Color ImageMarginGradientMiddle => _bg;
        public override Color ImageMarginGradientEnd => _bg;
        public override Color MenuBorder => _bg;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => _bg;
    }
}
