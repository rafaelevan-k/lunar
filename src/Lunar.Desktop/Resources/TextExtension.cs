using System.Windows.Markup;

namespace Lunar.Desktop.Resources;

/// <summary>XAML access to a localized text: <c>Content="{res:Text SettingsTitle}"</c> (FE-24, ADR-0017).</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TextExtension : MarkupExtension
{
    public TextExtension()
    {
    }

    public TextExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Strings.Get(Key);
}
