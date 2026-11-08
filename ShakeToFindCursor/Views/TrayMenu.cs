using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ShakeToFindCursor.Localization;

namespace ShakeToFindCursor;

internal sealed class TrayMenu : ContextMenuStrip
{
    internal ToolStripMenuItem Toggle { get; } = new();
    internal ToolStripMenuItem Settings { get; } = new();
    internal ToolStripMenuItem ExitApp { get; } = new();
    private readonly ToolStripSeparator _separator = new() { AutoSize = false };
    private Font? _textFont;
    private int _dpi = 96;
    private bool _ready;
    private readonly record struct MenuLayout(int Dpi, float Em, int Row, int Separator, Size Size);
    private MenuLayout _layout;
    private int Px(int dip) => (int)Math.Round(dip * _dpi / 96d);
    protected override Padding DefaultPadding => new(0, Px(3), 0, Px(3));

    internal TrayMenu()
    {
        ShowImageMargin = ShowCheckMargin = false;
        AutoSize = true; // NotifyIcon asks for the preferred size BEFORE Opening.
        Renderer = new MenuRenderer(this);
        Items.AddRange(new ToolStripItem[] { Toggle, Settings, _separator, ExitApp });
        RefreshText(true);
        _ready = true;
    }

    internal void RefreshText(bool enabled)
    {
        var text = UiText.Current;
        Toggle.Text = text[enabled ? "Disable" : "Enable"];
        Settings.Text = text["TraySettings"];
        ExitApp.Text = text["Exit"];
        _layout = default;
        if (Visible) SizeForDpi(_dpi);
    }

    public override Size GetPreferredSize(Size proposedSize) => _ready
        ? MeasureForDpi((int)MonitorDpi.AtPointer()).Size
        : base.GetPreferredSize(proposedSize);

    private MenuLayout MeasureForDpi(int dpi)
    {
        // This can run before the first HWND is created. Do not lay out controls
        // here: ShowInTaskbar needs the final footprint to compute its corner.
        var system = SystemFonts.MenuFont ?? SystemFonts.DefaultFont;
        float em = Math.Max(12.5f, system.SizeInPoints * 96 / 72) * dpi / 96;
        if (_layout.Dpi == dpi && _layout.Em == em) return _layout;
        int Scale(int dip) => (int)Math.Round(dip * dpi / 96d);
        using var font = new Font(system.FontFamily, em, FontStyle.Regular, GraphicsUnit.Pixel);
        int width = Scale(128), row = Scale(28), separator = Scale(7);
        foreach (var item in Items.OfType<ToolStripMenuItem>())
        {
            var text = TextRenderer.MeasureText(item.Text, font, Size.Empty, MenuRenderer.TextFlags);
            width = Math.Max(width, Scale(40) + text.Width);
            row = Math.Max(row, text.Height + Scale(4));
        }
        return _layout = new(dpi, em, row, separator, new Size(width, 3 * row + separator + 2 * Scale(3)));
    }

    protected override void OnOpening(CancelEventArgs e)
    {
        SizeForDpi((int)MonitorDpi.AtPointer());
        base.OnOpening(e);
    }

    internal void SizeForDpi(int dpi)
    {
        _dpi = dpi;
        var layout = MeasureForDpi(dpi);
        // Use physical pixels once, including for text, so monitor scaling and
        // WinForms' default menu font don't independently scale the same value.
        var system = SystemFonts.MenuFont ?? SystemFonts.DefaultFont;
        float em = layout.Em;
        if (_textFont == null || Math.Abs(_textFont.Size - em) > .01)
        {
            var oldText = _textFont;
            _textFont = new Font(system.FontFamily, em, FontStyle.Regular, GraphicsUnit.Pixel);
            Font = _textFont;
            oldText?.Dispose();
        }
        SuspendLayout();
        try
        {
            Padding = DefaultPadding;
            foreach (ToolStripItem item in Items)
            {
                item.AutoSize = false;
                item.Margin = Padding.Empty;
                item.Size = new Size(layout.Size.Width, item == _separator ? layout.Separator : layout.Row);
            }
            Size = layout.Size;
        }
        finally { ResumeLayout(true); }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            int roundSmall = 3;
            _ = DwmSetWindowAttribute(Handle, 33, ref roundSmall, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _textFont?.Dispose();
    }

    private sealed class MenuRenderer(TrayMenu menu) : ToolStripRenderer
    {
        internal const TextFormatFlags TextFlags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        private static bool Contrast => SystemInformation.HighContrast;
        private static Color Surface => Contrast ? SystemColors.Menu : Color.White;
        private static Color Border => Contrast ? SystemColors.WindowFrame : Color.FromArgb(215, 222, 232);

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(Surface);
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Border);
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected && !e.Item.Pressed) return;
            using var brush = new SolidBrush(Contrast ? SystemColors.Highlight : Color.FromArgb(239, 243, 248));
            e.Graphics.FillRectangle(brush, 1, 0, e.Item.Width - 2, e.Item.Height);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            Color ink = Contrast ? (e.Item.Selected ? SystemColors.HighlightText : SystemColors.MenuText)
                : Color.FromArgb(26, 27, 29);
            int x = menu.Px(20);
            TextRenderer.DrawText(e.Graphics, e.Text, menu._textFont, new Rectangle(x, 0, e.Item.Width-x, e.Item.Height),
                ink, TextFlags);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Border);
            e.Graphics.DrawLine(pen, 1, e.Item.Height / 2, e.Item.Width - 2, e.Item.Height / 2);
        }
    }
}
