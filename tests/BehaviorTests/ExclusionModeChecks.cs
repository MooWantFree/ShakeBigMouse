using System.Text.Json;
using System.Windows;
using ShakeToFindCursor;

internal static partial class Program
{
    private static void RenderExclusionModes()
    {
        const string path = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1103.4239.0_x64__publisher\app\ChatGPT.exe";
        var ui = ShakeToFindCursor.Localization.UiText.Current;
        string original = ui.Culture.Name;
        try
        {
            foreach (string language in new[] { "en", "zh-Hans" })
            {
                ui.Apply(language);
                var window = new SettingsWindow(new AppSettings
                    { ExcludedProcesses = [path, @"D:\Apps\Example\Example.exe", "legacy"], ProcessNameOnlyExclusions = [path] }, _ => { });
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(window.Width, window.Height));
                content.Arrange(new Rect(0, 0, window.Width, window.Height));
                content.UpdateLayout();
                content.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.Width, (int)window.Height,
                    96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var output = System.IO.File.Create($"tests/BehaviorTests/obj/exclusion-modes-{language}.png");
                encoder.Save(output);
                window.Close();
            }
        }
        finally { ui.Apply(original); }
    }

    private static void VerifyExclusionModes()
    {
        const string path = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1103.4239.0_x64__publisher\app\ChatGPT.exe";
        const string updated = @"C:\Program Files\WindowsApps\OpenAI.Codex_27.0.0.0_x64__publisher\app\ChatGPT.exe";
        var saved = new AppSettings { ExcludedProcesses = [path] };
        int writes = 0;
        var window = new SettingsWindow(saved, draft => { saved = draft; writes++; });
        void Apply() => window.BtnApply.RaiseEvent(new RoutedEventArgs(
            System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        var item = (ExcludedAppItem)window.ListExcludedApps.Items[0];
        if (item.DisplayName != "ChatGPT.exe" || !item.DisplayPath.Contains('…') || item.Rule != path || item.MatchProcessNameOnly)
            throw new Exception("Long-path display lost the executable name, full rule or default path mode.");
        item.MatchProcessNameOnly = true;
        if (!window.BtnApply.IsEnabled || writes != 0 || saved.ProcessNameOnlyExclusions.Count != 0)
            throw new Exception("Matching mode did not stay a reversible draft.");
        Apply();
        if (writes != 1 || saved.ExcludedProcesses.Single() != path || saved.ProcessNameOnlyExclusions.Single() != path)
            throw new Exception("Process-name mode discarded the original path.");
        var loaded = AppSettings.FromJson(JsonSerializer.Serialize(saved));
        if (!ProcessPaths.Matches(loaded.ExcludedProcesses, "chatgpt", updated, loaded.ProcessNameOnlyExclusions) ||
            !ProcessPaths.Matches(loaded.ExcludedProcesses, "ChatGPT", null, loaded.ProcessNameOnlyExclusions) ||
            ProcessPaths.Matches(loaded.ExcludedProcesses, "other", updated, loaded.ProcessNameOnlyExclusions))
            throw new Exception("Name-only mode did not ignore directory changes or matched a different process name.");
        var copy = loaded.Clone();
        copy.ProcessNameOnlyExclusions.Clear();
        if (loaded.ProcessNameOnlyExclusions.Count != 1) throw new Exception("Cloning shared mutable matching-mode settings.");
        item.MatchProcessNameOnly = false;
        Apply();
        if (writes != 2 || saved.ProcessNameOnlyExclusions.Count != 0 || saved.ExcludedProcesses.Single() != path ||
            ProcessPaths.Matches(saved.ExcludedProcesses, "ChatGPT", updated, saved.ProcessNameOnlyExclusions))
            throw new Exception("Unchecking did not restore exact path matching.");
        item.MatchProcessNameOnly = true;
        window.Close();
        if (saved.ProcessNameOnlyExclusions.Count != 0 || writes != 2)
            throw new Exception("Closing committed an unsaved matching-mode change.");
        var oldSettings = AppSettings.FromJson("""{"ExcludedProcesses":["legacy"]}""");
        var legacy = new ExcludedAppItem(oldSettings.ExcludedProcesses[0], false, () => { });
        legacy.MatchProcessNameOnly = false;
        if (legacy.HasPath || !legacy.MatchProcessNameOnly || oldSettings.ProcessNameOnlyExclusions.Count != 0)
            throw new Exception("A legacy name-only rule acquired an invented path.");
    }
}
