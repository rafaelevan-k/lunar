using System.Globalization;
using System.Text.RegularExpressions;
using Lunar.Desktop.Resources;
using Lunar.Desktop.UITests.Themes;
using Xunit;

namespace Lunar.Desktop.UITests.Accessibility;

// FE-24, ADR-0017: the texts. Proves the resource files agree with each other and with the code that uses them, that nothing new is written
// straight into XAML, and that the language rules do what the ADR says. Whether the Indonesian reads well is a human check (the user).
public sealed class LocalizationTests
{
    private static readonly Regex Placeholder = new(@"\{(\d+)(?:[:,][^}]*)?\}", RegexOptions.Compiled);

    private static IEnumerable<string> Files(string pattern) =>
        Directory.EnumerateFiles(ThemeTokenTests.DesktopDirectory, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "Spike" + Path.DirectorySeparatorChar));

    private static string[] PlaceholdersOf(string text) => Placeholder.Matches(text).Select(m => m.Groups[1].Value).Distinct().Order().ToArray();

    [Fact]
    public void EnglishAndIndonesianHaveTheSameKeys()
    {
        var english = Strings.All("en");
        var indonesian = Strings.All("id");

        Assert.True(english.Count > 300, $"Only {english.Count} English texts were found.");
        Assert.Empty(english.Keys.Except(indonesian.Keys));
        Assert.Empty(indonesian.Keys.Except(english.Keys));
    }

    [Fact]
    public void EveryTextIsNotEmptyAndKeepsTheSamePlaceholdersInBothLanguages()
    {
        var english = Strings.All("en");
        var indonesian = Strings.All("id");

        foreach (var (key, text) in english)
        {
            Assert.False(string.IsNullOrWhiteSpace(text), key + " is empty in English.");
            Assert.False(string.IsNullOrWhiteSpace(indonesian[key]), key + " is empty in Indonesian.");
            // A placeholder missing in one language would drop a number or throw when the text is formatted.
            Assert.True(PlaceholdersOf(text).SequenceEqual(PlaceholdersOf(indonesian[key])), key + " has different placeholders in the two languages.");
        }
    }

    [Fact]
    public void EveryTextCanBeFormattedWithItsOwnPlaceholders()
    {
        foreach (var language in new[] { "en", "id" })
        {
            var culture = CultureInfo.GetCultureInfo(language);
            foreach (var (key, text) in Strings.All(language))
            {
                var count = PlaceholdersOf(text).Select(int.Parse).DefaultIfEmpty(-1).Max() + 1;
                var args = Enumerable.Repeat((object)"x", count).ToArray();
                Assert.False(string.IsNullOrEmpty(string.Format(culture, text, args)), key);
            }
        }
    }

    [Fact]
    public void EveryKeyUsedInTheCodeAndTheXamlExistsAndEveryKeyIsUsed()
    {
        var all = Strings.All("en").Keys.ToHashSet(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var keyLiteral = new Regex("\"([A-Z][A-Za-z]*\\.[A-Za-z0-9]+)\"", RegexOptions.Compiled);

        foreach (var file in Files("*.cs"))
        {
            foreach (var line in File.ReadAllLines(file))
            {
                if (!line.Contains("Strings.") && !line.Contains("Get(") && !line.Contains("Format("))
                    continue;
                foreach (Match m in keyLiteral.Matches(line))
                {
                    // "Lunar.Desktop..." resource names and palette file names are not texts.
                    if (m.Groups[1].Value.StartsWith("Lunar.", StringComparison.Ordinal) || m.Groups[1].Value.StartsWith("Colors.", StringComparison.Ordinal))
                        continue;
                    used.Add(m.Groups[1].Value);
                }
            }
        }

        foreach (var file in Files("*.xaml"))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\{res:Text\s+([^\s}]+)\}"))
                used.Add(m.Groups[1].Value);
        }

        Assert.Empty(used.Except(all));
        Assert.Empty(all.Except(used));
    }

    // The ratchet: user-visible words go through the resource files. A literal in XAML would be skipped by the translation.
    [Fact]
    public void NoTextIsWrittenStraightIntoXaml()
    {
        var attributes = new Regex(
            @"\b(Text|Content|ToolTip|Header|Title|automation:AutomationProperties\.(?:Name|HelpText))=""([^""{][^""]*)""",
            RegexOptions.Compiled);
        // Brand names and a template placeholder are not words to translate.
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "Lunar", "Claude", "Height" };
        var offenders = new List<string>();

        foreach (var file in Files("*.xaml"))
        {
            if (file.EndsWith("App.xaml", StringComparison.Ordinal) || file.Contains(Path.DirectorySeparatorChar + "Themes" + Path.DirectorySeparatorChar))
                continue;
            foreach (Match m in attributes.Matches(File.ReadAllText(file)))
            {
                var value = m.Groups[2].Value;
                if (value.StartsWith("&#x", StringComparison.Ordinal) || allowed.Contains(value) || !Regex.IsMatch(value, "[A-Za-z]{2,}"))
                    continue;
                offenders.Add(Path.GetFileName(file) + ": " + m.Groups[1].Value + "=\"" + value + "\"");
            }
        }

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData(null, "en")]
    [InlineData("", "en")]
    [InlineData("en", "en")]
    [InlineData("en-US", "en")]
    [InlineData("id", "id")]
    [InlineData("id-ID", "id")]
    [InlineData("ID", "id")]
    [InlineData("fr-FR", "en")]
    [InlineData("zz", "en")]
    public void AStoredLanguageResolvesToATextSetLunarHas(string? stored, string expected) =>
        Assert.Equal(expected, Strings.Resolve(stored));

    [Fact]
    public void FollowWindowsUsesIndonesianOnlyWhenWindowsDisplaysIndonesian()
    {
        Assert.Equal("id", Strings.FromWindows(CultureInfo.GetCultureInfo("id-ID")));
        Assert.Equal("en", Strings.FromWindows(CultureInfo.GetCultureInfo("de-DE")));
        Assert.Equal("en", Strings.FromWindows(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void TheLanguageIsEnglishUntilChosenAndTextsFollowTheChoice()
    {
        try
        {
            Assert.Equal("E&xit", Strings.Get("Tray.Exit"));

            Strings.Use("id");
            Assert.Equal("&Keluar", Strings.Get("Tray.Exit"));
            Assert.Equal("Halaman 2", Strings.Format("Nav.PageDot", 2));

            Strings.Use("fr");
            Assert.Equal("E&xit", Strings.Get("Tray.Exit"));
        }
        finally
        {
            Strings.Use("en");
        }
    }

    [Fact]
    public void AMissingKeyShowsTheKeyInsteadOfThrowing() =>
        Assert.Equal("No.Such.Key", Strings.Get("No.Such.Key"));

    [Fact]
    public void NumbersAndWordsReadInIndonesianThroughTheSamePlaceholders()
    {
        try
        {
            Strings.Use("id");
            Assert.Equal("5 mnt", Strings.Format("Usage.SpanMin", 5));
            Assert.Equal("30% terpakai", Strings.Format("Usage.Percent", 30));
            Assert.Equal("Menunggu", Strings.Get("Queue.Waiting"));
            Assert.Equal("Pengaturan", Strings.Get("Settings.Title"));
        }
        finally
        {
            Strings.Use("en");
        }
    }
}
