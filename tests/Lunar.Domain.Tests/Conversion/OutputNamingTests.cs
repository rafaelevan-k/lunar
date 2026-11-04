using Lunar.Domain.Conversion;
using Xunit;

namespace Lunar.Domain.Tests.Conversion;

// BE-20: names of converted files are clean, safe, and numbered in page order.
public sealed class OutputNamingTests
{
    [Theory]
    [InlineData("photo", "photo")]
    [InlineData("my photo (1)", "my photo (1)")]
    [InlineData("foto ürün 写真", "foto ürün 写真")]
    [InlineData("a<b>c", "a_b_c")]
    [InlineData("a:b", "a_b")]
    [InlineData("a/b\\c", "a_b_c")]
    [InlineData("what?*\"|", "what____")]
    [InlineData("tab\there", "tab_here")]
    [InlineData("new\nline", "new_line")]
    [InlineData("  padded  ", "padded")]
    [InlineData("dots...", "dots")]
    [InlineData("trail. . ", "trail")]
    [InlineData(".hidden", ".hidden")]
    public void CharactersWindowsForbidsAreReplacedAndTheEdgesAreTrimmed(string input, string expected)
    {
        Assert.Equal(expected, OutputNaming.SanitizeBaseName(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    [InlineData(" . ")]
    public void NothingUsableBecomesTheFallbackName(string? input)
    {
        Assert.Equal("converted", OutputNaming.SanitizeBaseName(input));
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("nul", "_nul")]
    [InlineData("Com1", "_Com1")]
    [InlineData("LPT9", "_LPT9")]
    [InlineData("aux.backup", "_aux.backup")]
    public void ADeviceNameIsMadeHarmless(string input, string expected)
    {
        Assert.Equal(expected, OutputNaming.SanitizeBaseName(input));
    }

    [Theory]
    [InlineData("CON", true)]
    [InlineData("con", true)]
    [InlineData("NUL.txt", true)]
    [InlineData("COM9 .png", true)]
    [InlineData("COM0", false)]
    [InlineData("COMX", false)]
    [InlineData("CONSOLE", false)]
    [InlineData("communication", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ReservedDeviceNamesAreRecognisedWhateverTheCaseOrExtension(string? name, bool expected)
    {
        Assert.Equal(expected, OutputNaming.IsReservedDeviceName(name));
    }

    [Fact]
    public void ALongNameIsCutToTheLengthGiven()
    {
        var result = OutputNaming.SanitizeBaseName(new string('x', 500), 100);

        Assert.Equal(100, result.Length);
    }

    [Fact]
    public void CuttingNeverSplitsACharacterMadeOfTwoParts()
    {
        // each emoji is two UTF-16 units; cutting after an odd count must not leave half of one
        var result = OutputNaming.SanitizeBaseName(string.Concat(Enumerable.Repeat("😀", 10)), 5);

        Assert.Equal(4, result.Length);
        Assert.All(result.Where(char.IsSurrogate).Select((c, i) => (c, i)), p => Assert.Equal(p.i % 2 == 0, char.IsHighSurrogate(p.c)));
    }

    [Fact]
    public void CuttingTrimsWhatWouldBeLeftDangling()
    {
        Assert.Equal("abc", OutputNaming.SanitizeBaseName("abc. . . . def", 7));
    }

    [Fact]
    public void ALengthBelowOneIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OutputNaming.SanitizeBaseName("a", 0));
    }

    [Fact]
    public void ACleanedNameCanBeUsedAsAFileNameOnAnyHost()
    {
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var input in new[] { "a<b>:\"/\\|?*c", "\u0001\u0002x", "COM1", "....", "end.", new string('é', 400) })
        {
            var name = OutputNaming.SanitizeBaseName(input);

            Assert.DoesNotContain(name, c => invalid.Contains(c));
            Assert.True(name.Length is >= 1 and <= 255);
            Assert.False(OutputNaming.IsReservedDeviceName(name));
            Assert.False(name.EndsWith('.') || name.EndsWith(' ') || name.StartsWith(' '));
        }
    }

    [Theory]
    [InlineData("photo", 1, "photo")]
    [InlineData("photo", 0, "photo")]
    [InlineData("photo", 2, "photo (2)")]
    [InlineData("photo", 15, "photo (15)")]
    public void ACounterFollowsTheNameFromTwoUp(string name, int counter, string expected)
    {
        Assert.Equal(expected, OutputNaming.WithCounter(name, counter));
    }

    [Theory]
    [InlineData(1, 1, "doc_page_001.png")]
    [InlineData(7, 12, "doc_page_007.png")]
    [InlineData(100, 100, "doc_page_100.png")]
    [InlineData(999, 999, "doc_page_999.png")]
    [InlineData(1, 1000, "doc_page_0001.png")]
    [InlineData(1000, 1000, "doc_page_1000.png")]
    [InlineData(5, 123456, "doc_page_000005.png")]
    public void PageNamesCarryAZeroPaddedNumberThatSortsInPageOrder(int page, int count, string expected)
    {
        Assert.Equal(expected, OutputNaming.PageFileName("doc", page, count, ".png"));
    }

    [Fact]
    public void PageNamesSortInPageOrderAsText()
    {
        var names = Enumerable.Range(1, 120).Select(p => OutputNaming.PageFileName("doc", p, 120, ".jpg")).ToList();

        Assert.Equal(names, names.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(6, 5)]
    [InlineData(-1, 5)]
    [InlineData(1, 0)]
    public void APageOutsideTheRangeIsRejected(int page, int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OutputNaming.PageFileName("doc", page, count, ".png"));
    }

    [Fact]
    public void ThePagePatternFindsEveryPageFileOfABaseName()
    {
        Assert.Equal("doc_page_*.png", OutputNaming.PagePattern("doc", ".png"));
    }
}
