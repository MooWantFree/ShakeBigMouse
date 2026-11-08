using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ShakeToFindCursor;

internal static class ProcessPaths
{
    internal static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
            else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
            return System.IO.Path.IsPathFullyQualified(path)
                ? System.IO.Path.GetFullPath(path).Replace('/', '\\') : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
        { return null; }
    }

    internal static string? Read(int processId)
    {
        using var process = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process.IsInvalid) return null;
        var buffer = new StringBuilder(1024);
        int length = buffer.Capacity;
        if (QueryFullProcessImageName(process, 0, buffer, ref length)) return Normalize(buffer.ToString());
        if (Marshal.GetLastWin32Error() != 122) return null; // ERROR_INSUFFICIENT_BUFFER
        buffer = new StringBuilder(32768);
        length = buffer.Capacity;
        return QueryFullProcessImageName(process, 0, buffer, ref length) ? Normalize(buffer.ToString()) : null;
    }

    internal static bool Matches(IEnumerable<string> rules, string processName, string? executablePath,
        IEnumerable<string>? processNameOnlyRules = null)
    {
        string? path = Normalize(executablePath);
        var nameOnly = processNameOnlyRules?.Select(rule => Normalize(rule) ?? rule)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string rule in rules)
        {
            string? rulePath = Normalize(rule);
            if (rulePath != null)
            {
                if (nameOnly?.Contains(rulePath) == true)
                {
                    if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(rulePath), processName,
                        StringComparison.OrdinalIgnoreCase)) return true;
                }
                else if (path != null && string.Equals(rulePath, path, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (string.Equals(rule, processName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
}
