using System.Text;
using Lunar.Domain.Shared;

namespace Lunar.Domain.Notes;

/// <summary>
/// One plain-text note (ADR-0020): no formatting, images or attachments. Immutable; a change makes a new value. The text
/// is user content and must never reach a log, a notification or an error message.
/// </summary>
public sealed record Note
{
    private Note(Guid id, string text, DateTimeOffset created, DateTimeOffset changed)
    {
        Id = id;
        Text = text;
        Created = created;
        Changed = changed;
    }

    public Guid Id { get; }

    public string Text { get; }

    public DateTimeOffset Created { get; }

    public DateTimeOffset Changed { get; }

    /// <summary>The first line that is not blank, trimmed and shortened for a list; empty for a blank note.</summary>
    public string Title => NoteRules.TitleOf(Text);

    /// <summary>A new note created at <paramref name="now"/>. Fails with <see cref="ErrorCode.InvalidInput"/> when the text is too long.</summary>
    public static Result<Note> Create(Guid id, string? text, DateTimeOffset now) =>
        From(id, text, now, now);

    /// <summary>
    /// A note read back from storage or restored by Undo. Fails with <see cref="ErrorCode.InvalidInput"/> for an empty id,
    /// a text over <see cref="NoteRules.MaxTextLength"/>, or a change time before the creation time.
    /// </summary>
    public static Result<Note> From(Guid id, string? text, DateTimeOffset created, DateTimeOffset changed)
    {
        if (id == Guid.Empty)
            return Result.Failure<Note>(new OperationError(ErrorCode.InvalidInput, "A note needs an id."));
        if (changed < created)
            return Result.Failure<Note>(new OperationError(ErrorCode.InvalidInput, "A note cannot change before it was created."));

        var clean = NoteRules.Clean(text);
        if (clean.Length > NoteRules.MaxTextLength)
            return Result.Failure<Note>(NoteRules.TooLong());

        return Result.Success(new Note(id, clean, created, changed));
    }

    /// <summary>The same note with new text, changed at <paramref name="now"/> (never earlier than it was created).</summary>
    public Result<Note> WithText(string? text, DateTimeOffset now) =>
        From(Id, text, Created, now < Created ? Created : now);

    // The text is deliberately left out so that a note printed by accident shows no content.
    public override string ToString() => $"Note {Id:N} ({Text.Length} characters)";
}

/// <summary>The bounds of the notes feature (ADR-0020, AGENTS.md §9). Reaching one is explained to the user, never silent.</summary>
public static class NoteRules
{
    /// <summary>Characters (UTF-16 code units) in one note.</summary>
    public const int MaxTextLength = 100_000;

    /// <summary>Notes in total.</summary>
    public const int MaxNotes = 10_000;

    /// <summary>Bytes of the stored file.</summary>
    public const int MaxFileBytes = 20 * 1024 * 1024;

    /// <summary>Characters of a list title.</summary>
    public const int MaxTitleLength = 80;

    public static OperationError TooLong() =>
        new(ErrorCode.InvalidInput, $"A note can hold up to {MaxTextLength:N0} characters.");

    public static OperationError TooMany() =>
        new(ErrorCode.InvalidInput, $"Lunar can keep up to {MaxNotes:N0} notes. Delete a note to make room.");

    public static OperationError TooLarge() =>
        new(ErrorCode.InvalidInput, "Your notes are too large to save together. Shorten or delete a note.");

    /// <summary>
    /// Line breaks become "\n", NUL is removed and a lone surrogate (which cannot be stored as UTF-8) becomes U+FFFD, so
    /// what is saved is exactly what is read back.
    /// </summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\0')
                continue;
            if (c == '\r')
            {
                builder.Append('\n');
                if (i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                continue;
            }

            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                builder.Append(c).Append(text[i + 1]);
                i++;
                continue;
            }

            builder.Append(char.IsSurrogate(c) ? '�' : c);
        }

        return builder.ToString();
    }

    public static string TitleOf(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;
            if (trimmed.Length <= MaxTitleLength)
                return trimmed;
            var cut = MaxTitleLength - 1;
            if (char.IsHighSurrogate(trimmed[cut - 1]))
                cut--;
            return trimmed[..cut].TrimEnd() + "…";
        }

        return string.Empty;
    }
}
