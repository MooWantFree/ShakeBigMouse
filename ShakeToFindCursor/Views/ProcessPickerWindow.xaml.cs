using ShakeToFindCursor.Localization;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ShakeToFindCursor;

public class ProcessInfo
{
    public string ProcessName { get; set; } = "";
    public string? ExecutablePath { get; set; }
    public string PathLabel => ExecutablePath ?? UiText.Current["ProcessPathUnavailable"];
    public string DisplayName { get; set; } = "";
    public string WindowTitle { get; set; } = "";
    public int ProcessId { get; set; }
    public ImageSource? Icon { get; set; }
}

public partial class ProcessPickerWindow : Window
{
    private ObservableCollection<ProcessInfo> _allProcesses = new();
    private ObservableCollection<ProcessInfo> _filteredProcesses = new();

    public string? SelectedExecutablePath { get; private set; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public ProcessPickerWindow()
    {
        InitializeComponent();
        ProcessList.ItemsSource = _filteredProcesses;
        Loaded += ProcessPickerWindow_Loaded;
    }

    private void ProcessPickerWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshProcessList();
    }

    private void RefreshProcessList()
    {
        _allProcesses.Clear();
        _filteredProcesses.Clear();

        var processDict = new Dictionary<string, ProcessInfo>(StringComparer.OrdinalIgnoreCase);
        var windowTitles = new Dictionary<int, string>();

        // Get window titles for processes
        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            GetWindowThreadProcessId(hWnd, out uint processId);

            var sb = new System.Text.StringBuilder(256);
            GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString();

            if (!string.IsNullOrWhiteSpace(title) && !windowTitles.ContainsKey((int)processId))
            {
                windowTitles[(int)processId] = title;
            }

            return true;
        }, IntPtr.Zero);

        // Get all processes with visible windows
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            try
            {
                // Skip system processes and this app
                if (string.IsNullOrEmpty(process.ProcessName)) continue;
                if (process.Id == Environment.ProcessId) continue;
                if (process.ProcessName == "svchost") continue;
                if (process.ProcessName == "csrss") continue;
                if (process.ProcessName == "wininit") continue;
                if (process.ProcessName == "System") continue;
                if (process.ProcessName == "Idle") continue;

                // Only include processes with windows
                if (!windowTitles.ContainsKey(process.Id) && process.MainWindowHandle == IntPtr.Zero)
                {
                    continue;
                }

                string? executablePath = ProcessPaths.Read(process.Id);
                // Same-name executables in different folders must stay separate.
                string key = executablePath ?? $"pid:{process.Id}";
                if (processDict.ContainsKey(key)) continue;

                var info = new ProcessInfo
                {
                    ProcessName = process.ProcessName,
                    ExecutablePath = executablePath,
                    DisplayName = process.ProcessName,
                    ProcessId = process.Id,
                    WindowTitle = windowTitles.TryGetValue(process.Id, out var title) ? title : ""
                };

                // Try to get display name from file description
                try
                {
                    if (executablePath != null)
                    {
                        var versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
                        info.DisplayName = !string.IsNullOrWhiteSpace(versionInfo.FileDescription)
                            ? versionInfo.FileDescription
                            : process.ProcessName;

                        // Get icon
                        info.Icon = GetProcessIcon(executablePath);
                    }
                    else
                    {
                        info.DisplayName = process.ProcessName;
                    }
                }
                catch
                {
                    info.DisplayName = process.ProcessName;
                }

                processDict[key] = info;
            }
            catch { }
        }

        // Sort by display name and add to collection
        var sortedProcesses = processDict.Values
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var proc in sortedProcesses)
        {
            _allProcesses.Add(proc);
            _filteredProcesses.Add(proc);
        }

        StatusText.Text = UiText.Current.Format("PickerCount", _filteredProcesses.Count);
    }

    private ImageSource? GetProcessIcon(string filePath)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(filePath);
            if (icon == null) return null;

            using var bitmap = icon.ToBitmap();
            var hBitmap = bitmap.GetHbitmap();

            try
            {
                var imageSource = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                imageSource.Freeze();
                return imageSource;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        catch
        {
            return null;
        }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        string filter = SearchBox.Text.ToLowerInvariant().Trim();

        _filteredProcesses.Clear();

        foreach (var proc in _allProcesses)
        {
            if (string.IsNullOrEmpty(filter) ||
                proc.ProcessName.ToLowerInvariant().Contains(filter) ||
                proc.DisplayName.ToLowerInvariant().Contains(filter) ||
                proc.WindowTitle.ToLowerInvariant().Contains(filter) ||
                proc.ExecutablePath?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true)
            {
                _filteredProcesses.Add(proc);
            }
        }

        StatusText.Text = UiText.Current.Format("PickerFiltered", _filteredProcesses.Count, _allProcesses.Count);
    }

    private void ProcessList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        BtnSelect.IsEnabled = ProcessList.SelectedItem is ProcessInfo { ExecutablePath: not null };
        if (ProcessList.SelectedItem is ProcessInfo { ExecutablePath: null })
            StatusText.Text = UiText.Current["ProcessPathUnavailable"];
        else
            StatusText.Text = UiText.Current.Format("PickerFiltered", _filteredProcesses.Count, _allProcesses.Count);
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        RefreshProcessList();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BtnSelect_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessList.SelectedItem is ProcessInfo { ExecutablePath: not null } selected)
        {
            SelectedExecutablePath = selected.ExecutablePath;
            DialogResult = true;
            Close();
        }
    }
}
