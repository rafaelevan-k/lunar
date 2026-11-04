using Lunar.Application.Conversion.Models;
using Lunar.Domain.Conversion;

namespace Lunar.Conversion.Validation;

/// <summary>What is wrong with a path, if anything.</summary>
public enum PathProblem
{
    None,
    Empty,
    ControlCharacter,
    NotFullPath,
    Network,
    DeviceNamespace,
    StreamName,
    Wildcard,
    Traversal,
    ReservedName,
    BadName,
    NameTooLong,
    PathTooLong
}

/// <summary>
/// Windows path rules applied to the text of a path, the same on every host so the answer does not depend on the machine
/// running the check: a path must be a full local path as it will be opened, with nothing Windows treats specially. A path that
/// fails is refused, never "fixed", so what was checked is exactly what is later opened. This looks at text only; whether the
/// file exists or is a link is decided by the validator.
/// </summary>
internal static class PathRules
{
    private const string Wildcards = "*?\"<>|";

    public static PathProblem Check(string? path, int maxPathChars)
    {
        if (string.IsNullOrWhiteSpace(path))
            return PathProblem.Empty;
        if (path.Length > maxPathChars)
            return PathProblem.PathTooLong;

        foreach (var c in path)
        {
            if (c < ' ' || c == '\u007f')
                return PathProblem.ControlCharacter;
        }

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || path.StartsWith("//?/", StringComparison.Ordinal) || path.StartsWith("//./", StringComparison.Ordinal))
        {
            return PathProblem.DeviceNamespace;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return PathProblem.Network;

        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/'))
            return PathProblem.NotFullPath;

        var rest = path[3..];
        if (rest.Contains(':', StringComparison.Ordinal))
            return PathProblem.StreamName;
        if (rest.AsSpan().IndexOfAny(Wildcards) >= 0)
            return PathProblem.Wildcard;

        foreach (var segment in rest.Split('\\', '/'))
        {
            if (segment.Length == 0)
                continue;
            if (segment is "." or "..")
                return PathProblem.Traversal;
            if (segment.Length > OutputNaming.MaxNameChars)
                return PathProblem.NameTooLong;
            if (segment[^1] is ' ' or '.')
                return PathProblem.BadName;
            if (OutputNaming.IsReservedDeviceName(segment))
                return PathProblem.ReservedName;
        }

        return PathProblem.None;
    }

    /// <summary>The last part of a path (the file name) is not empty: a path that ends in a separator is a folder.</summary>
    public static bool NamesAFile(string path) => path.Length > 0 && path[^1] is not ('\\' or '/');

    public static ValidationFailure ToFailure(PathProblem problem) => problem switch
    {
        PathProblem.Empty => ValidationFailure.Invalid(RejectionReason.UnsafePath, "Choose a file to convert."),
        PathProblem.NotFullPath => ValidationFailure.Invalid(RejectionReason.UnsafePath, "The file's location isn't a full path. Pick the file again."),
        PathProblem.Network => ValidationFailure.Invalid(RejectionReason.NetworkPath, "Files on a network share aren't read in this version. Copy the file to this PC first."),
        PathProblem.NameTooLong or PathProblem.PathTooLong => ValidationFailure.Invalid(RejectionReason.PathTooLong, "The file's path is too long. Move the file closer to the top of a drive and try again."),
        _ => ValidationFailure.Invalid(RejectionReason.UnsafePath, "The file's path contains something Windows treats specially, so Lunar won't open it. Rename or move the file and try again.")
    };
}
