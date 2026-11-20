using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Wpf = System.Windows.Media;
using WpfImage = System.Windows.Media.Imaging;

namespace WinPulseOptimizer;

internal sealed class TrayMenu : ContextMenuStrip
{
    private static Wpf.SolidColorBrush ThemeBrush(string key) =>
        (Wpf.SolidColorBrush)System.Windows.Application.Current.FindResource(key);

    private static Color ThemeColor(string key)
    {
        Wpf.Color color = ThemeBrush(key).Color;
        return Color.FromArgb(color.A, color.R, color.G, color.B);
    }

    private Color _surface;
    private Color _text;
    private Color _muted;
    private Color _danger;
    private readonly List<IDisposable> _assets = new();
    private readonly List<IDisposable> _iconAssets = new();
    private readonly List<(ToolStripMenuItem Item, string Glyph, string ColorKey)> _commandIcons = new();
    private readonly ToolStripLabel _brand;
    private readonly ToolStripLabel _status;
    private readonly ToolStripMenuItem _gaming;
    private readonly ToolStripMenuItem _study;
    private readonly ToolStripMenuItem _chill;
    private readonly ToolStripMenuItem _memory;
    private readonly ToolStripMenuItem _exit;
    private ToolStripRenderer _themedRenderer = null!;
    private readonly ToolStripRenderer _highContrast = new ToolStripSystemRenderer();

    internal TrayMenu(Icon icon, Action open, Action gaming, Action study, Action chill, Action memory, Action exit)
    {
        Padding = new Padding(10, 10, 10, 10);
        MinimumSize = new Size(310, 0);
        ImageScalingSize = new Size(18, 18);
        ShowImageMargin = true;
        Font = Own(new Font("Segoe UI", 9.5f));
        var emphasis = Own(new Font(Font, FontStyle.Bold));
        var small = Own(new Font("Segoe UI", 8.5f));

        _brand = new ToolStripLabel("WinPulse Pro", Own(icon.ToBitmap()))
        {
            Font = emphasis,
            Padding = new Padding(12, 8, 16, 2),
            Margin = new Padding(4, 2, 4, 0),
            TextAlign = ContentAlignment.MiddleLeft
        };
        _status = new ToolStripLabel
        {
            Font = small,
            Padding = new Padding(12, 2, 16, 8),
            Margin = new Padding(4, 0, 4, 4),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var openItem = Command("Abrir WinPulse Pro", "OpenGlyph", open);
        openItem.Font = emphasis;
        _gaming = Command("Jugar (Máximo rendimiento)", "GameGlyph", gaming);
        _study = Command("Estudiar / Trabajar", "StudyGlyph", study);
        _chill = Command("Estar Chill", "QuietGlyph", chill);
        _memory = Command("Liberar RAM ahora", "MemoryGlyph", memory);
        _exit = Command("Salir por completo", "PowerGlyph", exit, "DangerBrush");

        Items.AddRange([_brand, _status, Separator(), openItem, Separator(),
            _gaming, _study, _chill, Separator(), _memory, _exit]);
        ApplyThemeColors();
        ThemeManager.ThemeApplied += OnThemeApplied;
        UpdateState("Plan de Windows", "", busy: false, canExit: true);
    }

    private void OnThemeApplied() => ApplyThemeColors();

    private void ApplyThemeColors()
    {
        _surface = ThemeColor("PanelBrush");
        _text = ThemeColor("TextBrush");
        _muted = ThemeColor("MutedBrush");
        _danger = ThemeColor("DangerBrush");
        _themedRenderer = new DarkRenderer();
        foreach (var asset in _iconAssets) asset.Dispose();
        _iconAssets.Clear();
        foreach (var (item, glyph, colorKey) in _commandIcons)
        {
            var bmp = DrawIcon(glyph, colorKey);
            _iconAssets.Add(bmp);
            item.Image = bmp;
        }
    }

    internal void UpdateState(string title, string tag, bool busy, bool canExit, bool pendingReapply = false)
    {
        bool highContrast = SystemInformation.HighContrast;
        Renderer = highContrast ? _highContrast : _themedRenderer;
        ShowCheckMargin = highContrast;
        ShowImageMargin = !highContrast;
        BackColor = highContrast ? SystemColors.Menu : _surface;
        ForeColor = highContrast ? SystemColors.MenuText : _text;
        _brand.ForeColor = ForeColor;
        _status.ForeColor = highContrast ? SystemColors.MenuText : _muted;
        _exit.ForeColor = highContrast ? SystemColors.MenuText : _danger;
        string prefix = pendingReapply ? "Requiere reaplicar: " : "Activo: ";
        _status.Text = $"{prefix}{title}" + (busy ? "  ·  Operación en curso" : "");
        _gaming.Checked = !pendingReapply && tag == "JUGAR";
        _study.Checked = !pendingReapply && tag == "ESTUDIAR";
        _chill.Checked = !pendingReapply && tag == "CHILL";
        _gaming.Enabled = _study.Enabled = _chill.Enabled = _memory.Enabled = !busy;
        _exit.Enabled = canExit;
    }

    private static ToolStripSeparator Separator() => new()
    {
        Margin = new Padding(0, 4, 0, 4)
    };

    private ToolStripMenuItem Command(string text, string glyph, Action click, string color = "AccentBrush")
    {
        var item = new ToolStripMenuItem(text, null, (_, _) => click())
        {
            Padding = new Padding(10, 8, 34, 8),
            Margin = new Padding(4, 2, 4, 2),
            ImageTransparentColor = Color.Transparent
        };
        _commandIcons.Add((item, glyph, color));
        return item;
    }

    private static Bitmap DrawIcon(string glyph, string color)
    {
        const int pixels = 40;
        var geometry = (Wpf.Geometry)System.Windows.Application.Current.FindResource(glyph);
        var visual = new Wpf.DrawingVisual();
        var pen = new Wpf.Pen(ThemeBrush(color), 1.4)
        {
            StartLineCap = Wpf.PenLineCap.Round, EndLineCap = Wpf.PenLineCap.Round, LineJoin = Wpf.PenLineJoin.Round
        };
        using (var drawing = visual.RenderOpen())
        {
            drawing.PushTransform(new Wpf.TranslateTransform(2, 2));
            drawing.DrawGeometry(null, pen, geometry);
        }
        var source = new WpfImage.RenderTargetBitmap(pixels, pixels, 192, 192, Wpf.PixelFormats.Pbgra32);
        source.Render(visual);
        var bitmap = new Bitmap(pixels, pixels, PixelFormat.Format32bppPArgb);
        var bits = bitmap.LockBits(new Rectangle(0, 0, pixels, pixels), ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try { source.CopyPixels(System.Windows.Int32Rect.Empty, bits.Scan0, bits.Stride * pixels, bits.Stride); }
        finally { bitmap.UnlockBits(bits); }
        return bitmap;
    }

    private T Own<T>(T asset) where T : IDisposable
    {
        _assets.Add(asset);
        return asset;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        ThemeManager.ThemeApplied -= OnThemeApplied;
        foreach (var asset in _iconAssets) asset.Dispose();
        _iconAssets.Clear();
        foreach (var asset in _assets) asset.Dispose();
        _assets.Clear();
    }

    private sealed class DarkRenderer : ToolStripRenderer
    {
        private readonly Color _panel = ThemeColor("PanelBrush");
        private readonly Color _raised = ThemeColor("RaisedBrush");
        private readonly Color _stroke = ThemeColor("StrokeBrush");
        private readonly Color _accent = ThemeColor("AccentBrush");
        private readonly Color _active = ThemeColor("AccentSoftBrush");
        private readonly Color _hover = ThemeColor("HoverBrush");
        private readonly Color _quiet = ThemeColor("QuietBrush");

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(_panel);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
            if (e.ToolStrip is TrayMenu menu)
            {
                int top = Math.Max(8, menu._brand.Bounds.Top - 2);
                int bottom = menu._status.Bounds.Bottom + 2;
                var header = new Rectangle(8, top, menu.ClientSize.Width - 16, Math.Max(0, bottom - top));
                if (header.Width >= 14 && header.Height >= 14)
                    FillRounded(e.Graphics, header, _raised);
            }
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(_stroke);
            var bounds = e.ToolStrip.ClientRectangle;
            e.Graphics.DrawRectangle(pen, 0, 0, bounds.Width - 1, bounds.Height - 1);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(_stroke);
            int y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 14, y, VisibleWidth(e.Item) - 14, y);
        }

        private static int VisibleWidth(ToolStripItem item) =>
            Math.Min(item.Width, (item.Owner?.ClientSize.Width ?? item.Width) - item.Bounds.Left);

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item is not ToolStripMenuItem item) return;
            bool hovered = item.Selected && item.Enabled;
            if (!hovered && !item.Checked) return;

            var bounds = new Rectangle(8, 1, VisibleWidth(item) - 16, item.Height - 2);
            if (bounds.Width < 14 || bounds.Height < 14) return;
            FillRounded(e.Graphics, bounds, hovered ? _hover : _active);
            if (!item.Checked) return;

            float y = bounds.Top + bounds.Height * 0.5f;
            float x = bounds.Right - 18;
            using var pen = new Pen(_accent, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var previous = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawLines(pen, [new PointF(x - 4, y), new PointF(x - 1, y + 3), new PointF(x + 5, y - 4)]);
            e.Graphics.SmoothingMode = previous;
        }

        private static void FillRounded(Graphics graphics, Rectangle bounds, Color color)
        {
            using var shape = new GraphicsPath();
            shape.AddArc(bounds.Left, bounds.Top, 14, 14, 180, 90);
            shape.AddArc(bounds.Right - 14, bounds.Top, 14, 14, 270, 90);
            shape.AddArc(bounds.Right - 14, bounds.Bottom - 14, 14, 14, 0, 90);
            shape.AddArc(bounds.Left, bounds.Bottom - 14, 14, 14, 90, 90);
            shape.CloseFigure();
            using var brush = new SolidBrush(color);
            var previous = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.FillPath(brush, shape);
            graphics.SmoothingMode = previous;
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }

        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
        {
            if (e.Item is ToolStripMenuItem && e.Image != null)
            {
                var rect = e.ImageRectangle;
                rect.X += 6;
                rect.Y = (e.Item.Height - rect.Height) / 2;
                e = new ToolStripItemImageRenderEventArgs(e.Graphics, e.Item, e.Image, rect);
            }
            base.OnRenderItemImage(e);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? e.Item.ForeColor : _quiet;
            if (e.Item is ToolStripMenuItem)
            {
                var rect = e.TextRectangle;
                rect.X += 6;
                rect.Y = (e.Item.Height - rect.Height) / 2;
                e.TextRectangle = rect;
            }
            base.OnRenderItemText(e);
        }
    }
}
