using System.Windows;
using ShakeToFindCursor;

internal static partial class Program
{
    private static void VerifyStartupPreference()
    {
        var saved = new AppSettings();
        int writes = 0;
        var window = new SettingsWindow(saved, draft => { saved = draft; writes++; });
        if (writes != 0) throw new Exception("Opening Settings changed startup registration.");
        window.ToggleStartup.IsChecked = true;
        if (writes != 0 || saved.RunOnStartup || !window.BtnApply.IsEnabled)
            throw new Exception("Startup saved without Apply or Confirm.");
        window.BtnReset.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        if (saved.RunOnStartup || window.ToggleStartup.IsChecked != true || writes != 0 || !window.BtnApply.IsEnabled)
            throw new Exception("Reset discarded or committed an unsaved startup choice.");
        window.BtnApply.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        if (!saved.RunOnStartup || writes != 1) throw new Exception("Apply did not save startup.");
        window.SliderMinimumShakeDistance.Value = 800;
        window.BtnReset.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        if (window.ToggleStartup.IsChecked != true || window.BtnApply.IsEnabled || writes != 1)
            throw new Exception("Reset changed saved startup or left reverted parameters dirty.");
        window.Close();
        if (!saved.RunOnStartup || writes != 1) throw new Exception("Closing changed saved startup.");
        var reopened = new SettingsWindow(saved, draft => { saved = draft; writes++; });
        if (reopened.ToggleStartup.IsChecked != true || reopened.BtnApply.IsEnabled || writes != 1)
            throw new Exception("Reopening Settings lost startup or saved it again.");
        reopened.ToggleStartup.IsChecked = false;
        reopened.BtnReset.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        if (!saved.RunOnStartup || writes != 1 || reopened.ToggleStartup.IsChecked != false || !reopened.BtnApply.IsEnabled)
            throw new Exception("Reset discarded an unsaved startup disable.");
        reopened.Close();
        if (!saved.RunOnStartup || writes != 1) throw new Exception("Closing saved a pending startup disable.");
    }
}
