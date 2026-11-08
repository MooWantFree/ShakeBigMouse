using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ShakeToFindCursor;
using ShakeToFindCursor.Localization;

internal static partial class Program
{
    private static void VerifyControlStyles()
    {
        var ui = UiText.Current;
        string original = ui.Culture.Name;
        var saved = new AppSettings { Language = "en", ExcludedProcesses = [@"C:\Example\Example.exe"] };
        int writes = 0;
        SettingsWindow? window = null;
        try
        {
            ui.Apply("en");
            window = new SettingsWindow(saved, draft => { saved = draft; writes++; });
            window.Show();
            window.UpdateLayout();
            var combo = window.LanguagePicker;
            var button = (ToggleButton)combo.Template.FindName("DropDownButton", combo)!;
            var popup = (Popup)combo.Template.FindName("PART_Popup", combo)!;
            button.IsChecked = true;
            PumpStyles();
            if (!combo.IsDropDownOpen || !popup.IsOpen || !popup.Child.IsVisible || popup.Child.RenderSize.Width < combo.ActualWidth - 1)
                throw new Exception("Styled language dropdown did not open or align with the selection box.");
            var image = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(popup.Child.RenderSize.Width),
                (int)Math.Ceiling(popup.Child.RenderSize.Height), 96, 96, PixelFormats.Pbgra32);
            image.Render(popup.Child);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using (var output = System.IO.File.Create("tests/BehaviorTests/obj/language-menu-preview.png")) encoder.Save(output);
            button.IsChecked = false;
            if (combo.IsDropDownOpen || popup.IsOpen) throw new Exception("Styled language dropdown did not close.");
            combo.SelectedValue = "zh-Hans";
            if (saved.Language != "zh-Hans" || writes != 1) throw new Exception("Language selection no longer saves immediately.");

            var row = (ListBoxItem)window.ListExcludedApps.ItemContainerGenerator.ContainerFromIndex(0);
            CheckBox FindSwitch(DependencyObject parent)
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                {
                    var child = VisualTreeHelper.GetChild(parent, i);
                    if (child is CheckBox check) return check;
                    try { return FindSwitch(child); } catch (InvalidOperationException) { }
                }
                throw new InvalidOperationException("Matching-mode switch not found.");
            }
            var mode = FindSwitch(row);
            mode.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            PumpStyles();
            var item = (ExcludedAppItem)window.ListExcludedApps.Items[0];
            var thumb = (TranslateTransform)mode.Template.FindName("thumbTranslate", mode)!;
            if (!item.MatchProcessNameOnly || thumb.X != 16 || !mode.IsTabStop || !combo.IsTabStop)
                throw new Exception("Styled switch did not preserve its binding, checked appearance or keyboard access.");
            mode.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            PumpStyles();
            if (item.MatchProcessNameOnly || thumb.X != 0 || writes != 1 || window.BtnApply.IsEnabled)
                throw new Exception("Styled switch did not restore its original draft state.");
        }
        finally { window?.Close(); ui.Apply(original); }
    }

    private static void PumpStyles()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
