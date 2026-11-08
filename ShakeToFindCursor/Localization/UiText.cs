using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows.Data;
using System.Windows.Markup;

namespace ShakeToFindCursor.Localization;

public sealed class UiText : INotifyPropertyChanged
{
    internal const string SystemLanguage = "system";
    internal const string FallbackLanguage = "en";
    internal static readonly ResourceManager Resources = new("ShakeToFindCursor.Resources.Strings", typeof(UiText).Assembly);
    private static readonly CultureInfo WindowsLanguage = CultureInfo.CurrentUICulture;
    public static UiText Current { get; } = new();
    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo(FallbackLanguage);
    public XmlLanguage Language => XmlLanguage.GetLanguage(Culture.IetfLanguageTag);
    public string this[string key] => Resources.GetString(key, Culture) ?? throw new MissingManifestResourceException(key);
    public string Format(string key, params object[] args) => string.Format(Culture, this[key], args);
    public event PropertyChangedEventHandler? PropertyChanged;

    internal static string Normalize(string? preference) =>
        preference is SystemLanguage or "zh-Hans" ? preference : FallbackLanguage;
    internal static CultureInfo Resolve(string? preference, CultureInfo system) => CultureInfo.GetCultureInfo(
        Normalize(preference) switch { SystemLanguage when system.TwoLetterISOLanguageName == "zh" => "zh-Hans", "zh-Hans" => "zh-Hans", _ => FallbackLanguage });

    internal void Apply(string preference)
    {
        var culture = Resolve(preference, WindowsLanguage);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
        if (Culture.Equals(culture)) return;
        Culture = culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}

// A one-way binding keeps existing windows and control templates in sync on Apply.
public sealed class LocExtension(string key) : MarkupExtension
{
    public string? Slider { get; set; }
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var text = new System.Windows.Data.Binding($"[{key}]") { Source = UiText.Current, Mode = BindingMode.OneWay };
        if (Slider == null) return text.ProvideValue(serviceProvider);
        var format = new MultiBinding { Mode = BindingMode.OneWay, Converter = ValueFormatter.Instance };
        format.Bindings.Add(text);
        format.Bindings.Add(new System.Windows.Data.Binding("Value") { ElementName = Slider, Mode = BindingMode.OneWay });
        return format.ProvideValue(serviceProvider);
    }

    private sealed class ValueFormatter : IMultiValueConverter
    {
        internal static readonly ValueFormatter Instance = new();
        public object Convert(object[] values, Type type, object parameter, CultureInfo culture) =>
            values is [string format, double value] ? string.Format(UiText.Current.Culture, format, value) : "";
        public object[] ConvertBack(object value, Type[] types, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
