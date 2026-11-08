using ShakeToFindCursor.Localization;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfSlider = System.Windows.Controls.Slider;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using WpfMessageBox = System.Windows.MessageBox;
using WpfRadioButton = System.Windows.Controls.RadioButton;

namespace ShakeToFindCursor;

public partial class SettingsWindow : Window
{
    private readonly ObservableCollection<ExcludedAppItem> _excludedApps = new();
    private readonly Action<AppSettings> _save;
    private readonly Action<AppSettings>? _preview;
    private readonly Action? _cancelPreview;

    private AppSettings _saved;
    private bool _loading;
    private readonly Func<string?> _target;
    private string? _displayedTarget;
    private readonly HashSet<WpfTextBox> _invalid = new();
    private Action? _refreshValidationErrors;
    public SettingsWindow() : this(App.CurrentSettings, App.SaveSettings,
        preview: App.PreviewSettings, cancelPreview: App.CancelSettingsPreview) { }

    internal SettingsWindow(AppSettings settings, Action<AppSettings> save, Func<string?>? target = null,
        Action<AppSettings>? preview = null, Action? cancelPreview = null)
    {
        _save = save;
        _preview = preview;
        _cancelPreview = cancelPreview;
        _saved = settings.Clone();
        _target = target ?? (() => null);
        InitializeComponent();
        ListExcludedApps.ItemsSource = _excludedApps;
        WireNumber(InputDistance, SliderMinimumShakeDistance, ErrorDistance);
        WireNumber(InputInterval, SliderShakeWindowMs, ErrorInterval);
        WireNumber(InputFactor, SliderShakeSensitivityPercent, ErrorFactor);
        WireNumber(InputMagnification, SliderMagnification, ErrorMagnification, decimals: true);
        WireNumber(InputHold, SliderMaxSizeHold, ErrorHold, decimals: true);
        WireNumber(InputShrinkSpeed, SliderShrinkSpeed, ErrorShrinkSpeed, decimals: true);
        WireNumber(InputCommandLineDuration, SliderCommandLineDuration, ErrorCommandLineDuration, decimals: true);
        Closed += (_, _) => _cancelPreview?.Invoke();
        Deactivated += (_, _) => _cancelPreview?.Invoke();
        SourceInitialized += (_, _) => FitHeightToWorkArea();
        foreach (var toggle in new System.Windows.Controls.Primitives.ToggleButton[]
            { ToggleStartup, ToggleFullscreen, ModeProportional, ModeMaximum })
        {
            toggle.Checked += (_, _) => UpdateActions();
            toggle.Unchecked += (_, _) => UpdateActions();
        }
        LanguagePicker.SelectionChanged += (_, _) => SaveLanguageSelection();
        SliderMagnification.ValueChanged += (_, _) => UpdateActions();
        SliderMaxSizeHold.ValueChanged += (_, _) => UpdateActions();
        SliderShrinkSpeed.ValueChanged += (_, _) => UpdateActions();
        SliderCommandLineDuration.ValueChanged += (_, _) => UpdateActions();
        _excludedApps.CollectionChanged += (_, _) => UpdateActions();
        ListExcludedApps.SelectionChanged += (_, _) => UpdateActions();
        LoadSettings(settings);
    }

    private void FitHeightToWorkArea()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var work = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        double available = Math.Max(240, work.Height / System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleY - 16);
        MinHeight = Math.Min(MinHeight, available);
        Height = Math.Min(Height, available);
    }

    private void LoadSettings(AppSettings s)
    {
        _loading = true;
        LanguagePicker.SelectedValue = UiText.Normalize(s.Language);
        ToggleStartup.IsChecked = s.RunOnStartup;
        SliderMinimumShakeDistance.Value = s.MinimumShakeDistance;
        SliderShakeWindowMs.Value = s.ShakeWindowMs;
        SliderShakeSensitivityPercent.Value = s.ShakeSensitivityPercent;
        SliderMagnification.Value = s.MagnificationFactor;
        ModeProportional.IsChecked = s.ScaleWithShakeStrength;
        ModeMaximum.IsChecked = !s.ScaleWithShakeStrength;
        SliderMaxSizeHold.Value = s.MaxSizeHoldSeconds;
        SliderShrinkSpeed.Value = s.ShrinkSpeed;
        SliderCommandLineDuration.Value = s.CommandLineDurationSeconds;
        ToggleFullscreen.IsChecked = s.DisableInFullscreen;

        // Reset must replace invalid text even when the slider already has its default value.
        InputDistance.Text = s.MinimumShakeDistance.ToString();
        InputInterval.Text = s.ShakeWindowMs.ToString();
        InputFactor.Text = s.ShakeSensitivityPercent.ToString();
        InputMagnification.Text = s.MagnificationFactor.ToString("0.##", CultureInfo.CurrentCulture);
        InputHold.Text = s.MaxSizeHoldSeconds.ToString("0.##", CultureInfo.CurrentCulture);
        InputShrinkSpeed.Text = s.ShrinkSpeed.ToString("0.##", CultureInfo.CurrentCulture);
        InputCommandLineDuration.Text = s.CommandLineDurationSeconds.ToString("0.##", CultureInfo.CurrentCulture);
        _excludedApps.Clear();
        foreach (var app in s.ExcludedProcesses)
            _excludedApps.Add(new ExcludedAppItem(app,
                s.ProcessNameOnlyExclusions.Contains(app, StringComparer.OrdinalIgnoreCase), UpdateActions));
        _loading = false;
        UpdateActions();

    }

    private AppSettings ReadDraft() => new AppSettings
        {
            Language = UiText.Normalize(LanguagePicker.SelectedValue as string),
            RunOnStartup = ToggleStartup.IsChecked == true,
            MinimumShakeDistance = (int)SliderMinimumShakeDistance.Value,
            ShakeWindowMs = (int)SliderShakeWindowMs.Value,
            ShakeSensitivityPercent = (int)SliderShakeSensitivityPercent.Value,
            MagnificationFactor = SliderMagnification.Value,
            ScaleWithShakeStrength = ModeProportional.IsChecked == true,
            MaxSizeHoldSeconds = SliderMaxSizeHold.Value,
            ShrinkSpeed = SliderShrinkSpeed.Value,
            CommandLineDurationSeconds = SliderCommandLineDuration.Value,
            DisableInFullscreen = ToggleFullscreen.IsChecked == true,
            ExcludedProcesses = _excludedApps.Select(app => app.Rule).ToList(),
            ProcessNameOnlyExclusions = _excludedApps.Where(app => app.HasPath && app.MatchProcessNameOnly)
                .Select(app => app.Rule).ToList()
        };

    private void SaveLanguageSelection()
    {
        if (_loading) return;
        string language = UiText.Normalize(LanguagePicker.SelectedValue as string);
        if (language == _saved.Language) return;
        // Save only the language; numeric drafts and exclusions may still be invalid or unsaved.
        var next = _saved.Clone();
        next.Language = language;
        try { _save(next); }
        catch (Exception ex)
        {
            _loading = true;
            try { LanguagePicker.SelectedValue = _saved.Language; }
            finally { _loading = false; }
            WpfMessageBox.Show(this, ex.Message, UiText.Current["ErrorSave"], MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _saved = next.Clone();
        UiText.Current.Apply(language);
        _refreshValidationErrors?.Invoke();
        UpdateActions();
    }
    private bool HasChanges(AppSettings s) =>
        s.Language != _saved.Language || s.RunOnStartup != _saved.RunOnStartup ||
        s.MinimumShakeDistance != _saved.MinimumShakeDistance || s.ShakeWindowMs != _saved.ShakeWindowMs ||
        s.ShakeSensitivityPercent != _saved.ShakeSensitivityPercent || s.MagnificationFactor != _saved.MagnificationFactor ||
        s.MaxSizeHoldSeconds != _saved.MaxSizeHoldSeconds || s.ScaleWithShakeStrength != _saved.ScaleWithShakeStrength ||
        s.ShrinkSpeed != _saved.ShrinkSpeed || s.CommandLineDurationSeconds != _saved.CommandLineDurationSeconds ||
        s.DisableInFullscreen != _saved.DisableInFullscreen ||
        !_saved.ExcludedProcesses.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(s.ExcludedProcesses) ||
        !_saved.ProcessNameOnlyExclusions.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(s.ProcessNameOnlyExclusions);

    private void UpdateActions()
    {
        if (_loading) return;
        BtnApply.IsEnabled = _invalid.Count == 0 && HasChanges(ReadDraft());
        BtnConfirm.IsEnabled = _invalid.Count == 0;
        BtnPreview.IsEnabled = _invalid.Count == 0 && _preview != null;
        if (BtnApply.IsEnabled || _invalid.Count > 0)
        {
            SaveIndicator.BeginAnimation(OpacityProperty, null);
            SaveIndicator.Opacity = 0;
        }
        ListExcludedApps.Visibility = _excludedApps.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyExclusions.Visibility = _excludedApps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnRemoveApp.IsEnabled = ListExcludedApps.SelectedItem != null;
        RefreshExternalTarget(force: true);
    }

    internal void RefreshExternalTarget(bool force = false)
    {
        var target = ProcessPaths.Normalize(_target());
        if (!force && target == _displayedTarget) return;
        _displayedTarget = target;
        CurrentAppLabel.Text = target == null ? UiText.Current["AddCurrent"] : UiText.Current.Format("AddTarget", System.IO.Path.GetFileName(target));
        BtnAddCurrentApp.ToolTip = target ?? CurrentAppLabel.Text;
        System.Windows.Automation.AutomationProperties.SetName(BtnAddCurrentApp,
            target == null ? CurrentAppLabel.Text : $"{CurrentAppLabel.Text} ({target})");
        BtnAddCurrentApp.IsEnabled = target != null && !_excludedApps.Any(app =>
            string.Equals(app.Rule, target, StringComparison.OrdinalIgnoreCase));
    }

    private void WireNumber(WpfTextBox input, WpfSlider slider, TextBlock error, bool decimals = false)
    {
        bool syncing = false;
        void RefreshError() => error.Text = UiText.Current.Format(
            decimals ? "DecimalInputRange" : "InputRange", slider.Minimum, slider.Maximum);
        _refreshValidationErrors += () => { if (_invalid.Contains(input)) RefreshError(); };
        slider.ValueChanged += (_, _) =>
        {
            if (!syncing) input.Text = slider.Value.ToString(decimals ? "0.##" : "0", CultureInfo.CurrentCulture);
        };
        input.TextChanged += (_, _) =>
        {
            bool parsed = double.TryParse(input.Text,
                decimals ? NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign : NumberStyles.None,
                CultureInfo.CurrentCulture, out double v);
            if (parsed && double.IsFinite(v) && v >= slider.Minimum && v <= slider.Maximum)
            {
                syncing = true;
                try { slider.Value = v; } finally { syncing = false; }
                _invalid.Remove(input);
                error.Visibility = Visibility.Collapsed;
            }
            else
            {
                _invalid.Add(input);
                RefreshError();
                error.Visibility = Visibility.Visible;
            }
            UpdateActions();
        };
    }

    private void SaveDraft(bool close)
    {
        if (_invalid.Count != 0) return;
        var draft = ReadDraft();
        if (!HasChanges(draft)) { if (close) Close(); return; }
        try { _save(draft); }
        catch (Exception ex)
        {
            WpfMessageBox.Show(this, ex.Message, UiText.Current["ErrorSave"], MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _saved = draft.Clone();
        UpdateActions();
        if (close) Close();
        else ShowSaveIndicator();
    }

    #region Navigation

    private void NavCategory_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfRadioButton rb) return;

        if (PanelShake != null) PanelShake.Visibility = Visibility.Collapsed;
        if (PanelGeneral != null) PanelGeneral.Visibility = Visibility.Collapsed;

        ScrollViewer? targetPanel = rb.Name == "NavShake" ? PanelShake : PanelGeneral;

        if (targetPanel != null)
        {
            targetPanel.Visibility = Visibility.Visible;
            targetPanel.Opacity = 0;

            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            targetPanel.BeginAnimation(OpacityProperty, fadeIn);
        }
    }

    #endregion

    #region Settings events

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        _cancelPreview?.Invoke();
        // Reset mouse parameters without changing the current startup choice, including unsaved edits.
        LoadSettings(new AppSettings { Language = _saved.Language, RunOnStartup = ToggleStartup.IsChecked == true });
    }

    private void BtnPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_invalid.Count == 0) _preview?.Invoke(ReadDraft());
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e) => SaveDraft(close: false);
    private void BtnConfirm_Click(object sender, RoutedEventArgs e) => SaveDraft(close: true);

    private void BtnAddCurrentApp_Click(object sender, RoutedEventArgs e)
    {
        string? shown = _displayedTarget;
        RefreshExternalTarget();
        if (shown != null && shown == _displayedTarget && BtnAddCurrentApp.IsEnabled)
            _excludedApps.Add(new ExcludedAppItem(shown, false, UpdateActions));
    }

    private void BtnAddApp_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ProcessPickerWindow { Owner = this };
        if (picker.ShowDialog() == true && picker.SelectedExecutablePath is { } path)
        {
            if (!_excludedApps.Any(app => string.Equals(app.Rule, path, StringComparison.OrdinalIgnoreCase)))
            {
                _excludedApps.Add(new ExcludedAppItem(path, false, UpdateActions));
            }
        }
    }

    private void BtnRemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (ListExcludedApps.SelectedItem is ExcludedAppItem selected)
        {
            _excludedApps.Remove(selected);
        }
    }

    #endregion

    #region Window chrome

    private void ShowSaveIndicator()
    {

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { BeginTime = TimeSpan.Zero };
        var hold = new DoubleAnimation(1, 1, TimeSpan.FromSeconds(1.5)) { BeginTime = TimeSpan.FromMilliseconds(150) };
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400)) { BeginTime = TimeSpan.FromMilliseconds(1650) };

        var storyboard = new Storyboard();
        foreach (var anim in new[] { fadeIn, hold, fadeOut })
        {
            Storyboard.SetTarget(anim, SaveIndicator);
            Storyboard.SetTargetProperty(anim, new PropertyPath(OpacityProperty));
            storyboard.Children.Add(anim);
        }
        storyboard.Begin();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            DragMove();
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnCloseWindow_Click(object sender, RoutedEventArgs e) => Close();


    #endregion
}
