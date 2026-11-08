using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using ShakeToFindCursor;

internal static partial class Program
{
    private static void VerifyPathExclusions()
    {
        const string first = @"C:\Apps\One\same.exe", second = @"C:\Apps\Two\same.exe";
        if (!ProcessPaths.Matches([first], "same", @"c:/apps/one/same.exe") ||
            !ProcessPaths.Matches([first], "same", @"\\?\C:\Apps\One\same.exe") ||
            ProcessPaths.Matches([first], "same", second) || ProcessPaths.Matches([first], "same", null) ||
            !ProcessPaths.Matches(["SAME"], "same", second))
            throw new Exception("Exact path matching or legacy name matching failed.");
        var saved = new AppSettings { ExcludedProcesses = [first, second, "legacy"] };
        var loaded = AppSettings.FromJson(JsonSerializer.Serialize(saved));
        if (!loaded.ExcludedProcesses.SequenceEqual(saved.ExcludedProcesses)) throw new Exception("Exclusion rules did not round trip.");
        Near(ProcessPaths.Read(Environment.ProcessId) == ProcessPaths.Normalize(Environment.ProcessPath) ? 1 : 0, 1);
        if (ProcessPaths.Read(int.MaxValue) != null) throw new Exception("An inaccessible process produced a path.");

        string folder = Directory.CreateDirectory("tests/BehaviorTests/obj/path-targets").FullName;
        var children = new List<Process>();
        try
        {
            foreach (string name in new[] { "one", "two" })
            {
                string destination = Directory.CreateDirectory(Path.Combine(folder, name)).FullName;
                foreach (string file in Directory.EnumerateFiles(AppContext.BaseDirectory))
                    File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
                var child = Process.Start(new ProcessStartInfo(Path.Combine(destination, "BehaviorTests.exe"), "--path-rule-target")
                    { UseShellExecute = false, CreateNoWindow = true }) ?? throw new Exception("Could not start path target.");
                children.Add(child);
                WaitUntil(() =>
                {
                    child.Refresh();
                    if (child.HasExited) throw new Exception($"Path target exited early: {child.ExitCode}.");
                    return child.MainWindowHandle != 0;
                }, 10000);
            }
            string a = ProcessPaths.Read(children[0].Id) ?? throw new Exception("First path not readable.");
            string b = ProcessPaths.Read(children[1].Id) ?? throw new Exception("Second path not readable.");
            if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) throw new Exception("Same-name targets lost their distinct paths.");
            if (!FullscreenDetector.IsExcludedWindow(children[0].MainWindowHandle, [a]) ||
                FullscreenDetector.IsExcludedWindow(children[1].MainWindowHandle, [a]) ||
                !FullscreenDetector.IsExcludedWindow(children[1].MainWindowHandle, [a], [a]) ||
                !FullscreenDetector.IsExcludedWindow(children[1].MainWindowHandle, ["BehaviorTests"]))
                throw new Exception("Live process exclusions failed to distinguish paths or preserve legacy rules.");
            using var history = new ForegroundAppHistory();
            typeof(ForegroundAppHistory).GetMethod("Observe", BindingFlags.Instance | BindingFlags.NonPublic,
                [typeof(IntPtr)])!.Invoke(history, [children[0].MainWindowHandle]);
            if (!string.Equals(history.Target(), a, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Add-current history did not retain the executable path.");

            var picker = new ProcessPickerWindow();
            typeof(ProcessPickerWindow).GetMethod("RefreshProcessList", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(picker, null);
            var entries = picker.ProcessList.Items.Cast<ProcessInfo>().Where(p =>
                p.ExecutablePath == a || p.ExecutablePath == b).ToArray();
            if (entries.Length != 2) throw new Exception("Picker merged same-name executables from different folders.");
            picker.SearchBox.Text = a;
            if (picker.ProcessList.Items.Count != 1 || ((ProcessInfo)picker.ProcessList.Items[0]).ExecutablePath != a)
                throw new Exception("Picker path search selected the wrong executable.");
            var all = (ObservableCollection<ProcessInfo>)typeof(ProcessPickerWindow)
                .GetField("_allProcesses", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker)!;
            var unknown = new ProcessInfo { ProcessName = "unreadable", DisplayName = "unreadable" };
            all.Add(unknown);
            picker.SearchBox.Text = "unreadable";
            picker.ProcessList.SelectedItem = unknown;
            if (picker.BtnSelect.IsEnabled) throw new Exception("Unreadable path could become an ambiguous name-only exclusion.");
            picker.Close();
            Console.WriteLine($"  Distinct live paths: {a} / {b}");
        }
        finally
        {
            foreach (var child in children)
            {
                if (!child.HasExited)
                {
                    child.CloseMainWindow();
                    if (!child.WaitForExit(2000)) { child.Kill(); child.WaitForExit(2000); }
                }
                child.Dispose();
            }
        }
    }
}
