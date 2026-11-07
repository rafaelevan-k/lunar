using Lunar.Domain.Notes;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Domain.Tests.Notes;

public sealed class NoteTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Id = Guid.Parse("4c7f0a52-1f0e-4bb2-9f0c-3d1a6e2b8a10");

    [Fact]
    public void ANewNoteKeepsItsTextAndIsCreatedAndChangedNow()
    {
        var note = Note.Create(Id, "Shopping\nmilk", Now).Value;

        Assert.Equal(Id, note.Id);
        Assert.Equal("Shopping\nmilk", note.Text);
        Assert.Equal(Now, note.Created);
        Assert.Equal(Now, note.Changed);
        Assert.Equal("Shopping", note.Title);
    }

    [Fact]
    public void AnEmptyNoteIsAllowedAndHasNoTitle()
    {
        var note = Note.Create(Id, null, Now).Value;

        Assert.Equal(string.Empty, note.Text);
        Assert.Equal(string.Empty, note.Title);
    }

    [Fact]
    public void TheTextMayBeExactlyTheLimitButNotOneCharacterMore()
    {
        Assert.True(Note.Create(Id, new string('a', NoteRules.MaxTextLength), Now).IsSuccess);

        var tooLong = Note.Create(Id, new string('a', NoteRules.MaxTextLength + 1), Now);

        Assert.Equal(ErrorCode.InvalidInput, tooLong.Error!.Code);
        Assert.Contains("100,000", tooLong.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyIdOrAChangeBeforeTheCreationIsInvalid()
    {
        Assert.Equal(ErrorCode.InvalidInput, Note.Create(Guid.Empty, "x", Now).Error!.Code);
        Assert.Equal(ErrorCode.InvalidInput, Note.From(Id, "x", Now, Now.AddSeconds(-1)).Error!.Code);
    }

    [Theory]
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("a\rb", "a\nb")]
    [InlineData("a\r\n\r\nb", "a\n\nb")]
    [InlineData("a\0b", "ab")]
    [InlineData("smile 😀 ok", "smile 😀 ok")]
    [InlineData("tab\there", "tab\there")]
    public void TextIsCleanedSoWhatIsSavedIsWhatIsReadBack(string input, string expected) =>
        Assert.Equal(expected, Note.Create(Id, input, Now).Value.Text);

    // Built in code: xUnit cannot carry a lone surrogate through attribute data intact.
    [Fact]
    public void ALoneSurrogateBecomesAReplacementCharacter()
    {
        const char replacement = (char)0xFFFD;
        Assert.Equal(new string(['a', replacement, 'b']), Note.Create(Id, new string(['a', (char)0xD800, 'b']), Now).Value.Text);
        Assert.Equal(new string(['a', replacement]), Note.Create(Id, new string(['a', (char)0xDC00]), Now).Value.Text);
        Assert.Equal(new string([replacement, replacement]), Note.Create(Id, new string([(char)0xDC00, (char)0xD800]), Now).Value.Text);
    }

    [Fact]
    public void TheTitleIsTheFirstNonBlankLineTrimmedAndShortened()
    {
        Assert.Equal("Second", Note.Create(Id, "\n   \n  Second  \nthird", Now).Value.Title);

        var title = Note.Create(Id, new string('x', 200), Now).Value.Title;
        Assert.Equal(NoteRules.MaxTitleLength, title.Length);
        Assert.EndsWith("…", title, StringComparison.Ordinal);
    }

    [Fact]
    public void AShortenedTitleNeverCutsAnEmojiInHalf()
    {
        var text = new string('x', NoteRules.MaxTitleLength - 2) + "😀😀";

        var title = Note.Create(Id, text, Now).Value.Title;

        for (var i = 0; i < title.Length; i++)
        {
            if (char.IsHighSurrogate(title[i]))
                Assert.True(i + 1 < title.Length && char.IsLowSurrogate(title[++i]), "A title ends in half an emoji.");
            else
                Assert.False(char.IsLowSurrogate(title[i]), "A title starts with half an emoji.");
        }

        Assert.EndsWith("…", title, StringComparison.Ordinal);
    }

    [Fact]
    public void NewTextKeepsTheIdAndCreationAndNeverChangesBeforeTheCreation()
    {
        var note = Note.Create(Id, "one", Now).Value;

        var later = note.WithText("two", Now.AddMinutes(5)).Value;
        var clockWentBack = note.WithText("three", Now.AddMinutes(-5)).Value;

        Assert.Equal((Id, Now, Now.AddMinutes(5), "two"), (later.Id, later.Created, later.Changed, later.Text));
        Assert.Equal(Now, clockWentBack.Changed);
        Assert.Equal(ErrorCode.InvalidInput, note.WithText(new string('a', NoteRules.MaxTextLength + 1), Now).Error!.Code);
    }

    [Fact]
    public void PrintingANoteNeverShowsItsText()
    {
        var note = Note.Create(Id, "my secret pin 4321", Now).Value;

        Assert.DoesNotContain("secret", note.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("4321", note.ToString(), StringComparison.Ordinal);
    }
}
