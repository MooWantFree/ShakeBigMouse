using System.ComponentModel;
using System.Globalization;
using System.IO;

namespace ShakeToFindCursor;

public sealed class ExcludedAppItem : INotifyPropertyChanged
{
    private bool _matchProcessNameOnly;
    private readonly Action _changed;
    private readonly string? _path;
    public string Rule { get; }
    public bool HasPath { get; }
    public string DisplayName => HasPath ? Path.GetFileName(_path)! : Rule;
    public string DisplayPath => HasPath ? CompactPath(_path!) : "";
    public bool MatchProcessNameOnly
    {
        get => !HasPath || _matchProcessNameOnly;
        set
        {
            if (!HasPath || value == _matchProcessNameOnly) return;
            _matchProcessNameOnly = value;
            PropertyChanged?.Invoke(this, new(nameof(MatchProcessNameOnly)));
            _changed();
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public override string ToString() => Rule;

    internal ExcludedAppItem(string rule, bool matchProcessNameOnly, Action changed)
    {
        string? path = ProcessPaths.Normalize(rule);
        Rule = rule;
        _path = path;
        HasPath = path != null;
        _matchProcessNameOnly = matchProcessNameOnly;
        _changed = changed;
    }

    private static string CompactPath(string path)
    {
        if (path.Length <= 64) return path;
        string root = Path.GetPathRoot(path)!;
        string[] parts = path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 5)
        {
            string compact = root + string.Join('\\', parts.Take(2)) + @"\…\" + string.Join('\\', parts.TakeLast(2));
            if (compact.Length <= 64) return compact;
        }
        int[] elements = StringInfo.ParseCombiningCharacters(path);
        return elements.Length <= 64 ? path : path[..elements[30]] + "…" + path[elements[^30]..];
    }
}
