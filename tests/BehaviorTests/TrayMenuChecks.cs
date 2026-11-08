using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using ShakeToFindCursor;
using ShakeToFindCursor.Localization;
using F = System.Windows.Forms;

internal static partial class Program
{
    // Explicit interactive check. Uses the production menu; no tray command
    // reaches the real app and the pointer is restored on exit.
    private static void VerifyTrayMenu()
    {
        if (!GetPhysicalCursorPos(out var saved)) throw new Exception("Could not save pointer.");
        var key = typeof(F.ToolStripDropDown).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var folder = Directory.CreateDirectory("tests/BehaviorTests/obj/tray-menu").FullName;
        using var menu = new TrayMenu();
        int settingsClicks = 0;
        menu.Settings.Click += (_, _) => settingsClicks++;
        try
        {
            // NotifyIcon uses ShowInTaskbar, which resolves the corner before
            // Opening. Don't prime SizeForDpi or create the HWND before this.
            var taskbarShow = typeof(F.ContextMenuStrip).GetMethod("ShowInTaskbar", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var screen in F.Screen.AllScreens)
            {
                using var cold = new TrayMenu();
                var anchor = new Point(screen.Bounds.Right - 150, screen.Bounds.Bottom - 15);
                SetPhysicalCursorPos(anchor.X, anchor.Y);
                var gaps = new List<Point>();
                for (int open = 0; open < 2; open++)
                {
                    taskbarShow.Invoke(cold, new object[] { anchor.X, anchor.Y });
                    F.Application.DoEvents();
                    var gap = new Point(cold.Right - anchor.X, cold.Bottom - anchor.Y);
                    Console.WriteLine($"{screen.DeviceName} open {open+1}: pointer={anchor}, bounds={cold.Bounds}, corner offset={gap}");
                    gaps.Add(gap);
                    cold.Close();
                }
                if (gaps.Any(g => Math.Abs(g.X) > 1 || Math.Abs(g.Y) > 1))
                    throw new Exception("Tray menu did not anchor on the first or repeated open.");
            }
            // Also exercise scales not physically present on the host.
            foreach (int dpi in new[] { 96, 144, 192 })
            {
                menu.SizeForDpi(dpi);
                if (menu.Toggle.Height < 28 * dpi / 96 || menu.Font.Size < 12.5f * dpi / 96)
                    throw new Exception($"Menu did not scale at {dpi} DPI.");
            }
            foreach (var screen in F.Screen.AllScreens)
            foreach (string lang in new[] { "en", "zh-Hans" })
            {
                var area = screen.WorkingArea;
                var anchor = new Point(area.Right - 3, area.Bottom - 3);
                SetPhysicalCursorPos(anchor.X, anchor.Y);
                int dpi = (int)MonitorDpi.AtPointer();
                UiText.Current.Apply(lang);
                menu.RefreshText(true);
                menu.Show(anchor, F.ToolStripDropDownDirection.AboveLeft);
                F.Application.DoEvents();
                if (!menu.Visible || !area.Contains(menu.Bounds) || menu.Width < 128 * dpi / 96 ||
                    menu.Toggle.Height < 28 * dpi / 96 ||
                    menu.Toggle.Text != UiText.Current["Disable"] || menu.Settings.Text != UiText.Current["TraySettings"] ||
                    menu.ExitApp.Text != UiText.Current["Exit"]) throw new Exception($"Popup placement, sizing or labels failed: {menu.Bounds} / {area}.");
                menu.Toggle.Select();
                key.Invoke(menu, new object[] { F.Keys.Down });
                if (!menu.Settings.Selected) throw new Exception("Down did not select Settings.");
                using (var image = new Bitmap(menu.Width, menu.Height))
                {
                    menu.DrawToBitmap(image, new Rectangle(Point.Empty, menu.Size));
                    image.Save(Path.Combine(folder, $"tray-{lang}-{dpi}.png"), ImageFormat.Png);
                }
                int before = settingsClicks;
                key.Invoke(menu, new object[] { F.Keys.Enter });
                if (settingsClicks != before + 1 || menu.Visible) throw new Exception("Enter did not invoke and dismiss.");
                menu.RefreshText(false);
                menu.Show(anchor, F.ToolStripDropDownDirection.AboveLeft);
                F.Application.DoEvents();
                if (menu.Toggle.Text != UiText.Current["Enable"]) throw new Exception("Toggle label did not update.");
                key.Invoke(menu, new object[] { F.Keys.Escape });
                if (menu.Visible) throw new Exception("Escape did not dismiss.");
                Console.WriteLine($"{screen.DeviceName}: {dpi} DPI, {lang}, {menu.Size}, first row={menu.Toggle.Bounds}, padding={menu.Padding}");
            }
        }
        finally { menu.Close(); UiText.Current.Apply("system"); SetPhysicalCursorPos(saved.X, saved.Y); }
    }
}
