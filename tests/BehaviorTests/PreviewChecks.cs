using System.Globalization;
using System.Windows;
using ShakeToFindCursor;
using ShakeToFindCursor.Localization;
using WpfButton = System.Windows.Controls.Button;

internal static partial class Program
{
    private static void VerifyDraftPreview()
    {
        var saved = new AppSettings();
        AppSettings? preview = null;
        int writes = 0, cancellations = 0;
        var window = new SettingsWindow(saved, _ => writes++, preview: draft => preview = draft,
            cancelPreview: () => cancellations++);
        void Click(WpfButton button) => button.RaiseEvent(new RoutedEventArgs(
            System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        string Number(double value) => value.ToString(CultureInfo.CurrentCulture);
        window.InputMagnification.Text = Number(4.5);
        window.InputHold.Text = Number(1.25);
        window.InputShrinkSpeed.Text = Number(.75);
        window.InputCommandLineDuration.Text = Number(3.5);
        Near(window.SliderMagnification.Value, 4.5);
        Near(window.SliderMaxSizeHold.Value, 1.25);
        Near(window.SliderShrinkSpeed.Value, .75);
        Near(window.SliderCommandLineDuration.Value, 3.5);
        Click(window.BtnPreview);
        if (writes != 0 || preview == null) throw new Exception("Preview saved a draft or failed to receive it.");
        Near(preview.MagnificationFactor, 4.5);
        Near(preview.MaxSizeHoldSeconds, 1.25);
        Near(preview.ShrinkSpeed, .75);
        Near(preview.CommandLineDurationSeconds, 3.5);
        Near(saved.MagnificationFactor, 10);
        Near(saved.CommandLineDurationSeconds, 1);
        foreach (var input in new[] { window.InputMagnification, window.InputHold,
            window.InputShrinkSpeed, window.InputCommandLineDuration })
        {
            input.Text = "NaN";
            if (window.BtnApply.IsEnabled || window.BtnConfirm.IsEnabled || window.BtnPreview.IsEnabled)
                throw new Exception("Invalid decimal input could be saved or previewed.");
            Click(window.BtnReset);
        }
        if (!window.BtnPreview.IsEnabled || window.BtnApply.IsEnabled)
            throw new Exception("Reset did not restore valid, clean inputs.");
        window.SliderShrinkSpeed.Value = 1.5;
        if (window.InputShrinkSpeed.Text != Number(1.5)) throw new Exception("Decimal slider did not sync its editor.");
        window.Close();
        if (writes != 0 || cancellations == 0) throw new Exception("Closing saved the draft or failed to cancel preview.");
    }

    private static void VerifyPreviewAnimation()
    {
        var saved = new AppSettings { MagnificationFactor = 2, ScaleWithShakeStrength = false,
            MaxSizeHoldSeconds = .5 };
        var detector = new ShakeDetector(saved);
        var renderer = new FakeRenderer();
        using var animator = new CursorAnimator(detector, saved, renderer);
        var draft = new AppSettings { MagnificationFactor = 5, CommandLineDurationSeconds = .5, ShrinkSpeed = 3 };
        var preview = animator.PreviewAsync(draft);
        WaitUntil(() => renderer.LastScale == 5);
        if (!preview.Wait(5000)) throw new Exception("Preview did not finish.");
        Near(renderer.LastScale, 1);
        Near(detector.Energy, 0);
        Shake(detector, Environment.TickCount64 - 160);
        WaitUntil(() => renderer.LastScale == 2);
        detector.SetSuppressed(true);
        WaitUntil(() => renderer.LastScale == 1);
        var cancelled = animator.PreviewAsync(new AppSettings { MagnificationFactor = 3, CommandLineDurationSeconds = 30 });
        WaitUntil(() => renderer.LastScale == 3);
        animator.CancelPreview();
        if (!cancelled.Wait(500)) throw new Exception("Preview cancellation left a pending task.");
        WaitUntil(() => renderer.LastScale == 1);
        var exiting = animator.PreviewAsync(draft);
        animator.Dispose();
        if (!exiting.IsCompleted || renderer.LastScale != 1)
            throw new Exception("Disposal did not finish the preview and restore the cursor.");
    }

    private static void VerifySettingsLayout()
    {
        var ui = UiText.Current;
        string original = ui.Culture.Name;
        try
        {
            foreach (string language in new[] { "en", "zh-Hans" })
            {
                ui.Apply(language);
                var window = new SettingsWindow(new AppSettings(), _ => { });
                var content = (FrameworkElement)window.Content;
                if (window.NavGeneral.IsChecked != true || window.PanelGeneral.Visibility != Visibility.Visible)
                    throw new Exception("Settings did not open on General.");
                content.Measure(new Size(window.Width, window.Height));
                content.Arrange(new Rect(0, 0, window.Width, window.Height));
                content.UpdateLayout();
                content.UpdateLayout();
                if (window.PanelGeneral.ScrollableHeight > 1)
                    throw new Exception($"Default {language} window hides general controls: {window.PanelGeneral.ScrollableHeight} px.");
                window.NavShake.IsChecked = true;
                content.Measure(new Size(window.Width, window.Height));
                content.Arrange(new Rect(0, 0, window.Width, window.Height));
                content.UpdateLayout();
                content.UpdateLayout();
                if (window.PanelShake.ScrollableHeight > 1)
                    throw new Exception($"Default {language} window still hides shake controls: {window.PanelShake.ScrollableHeight} px.");
                window.Close();
            }
        }
        finally { ui.Apply(original); }
    }
}
