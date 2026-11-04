using Lunar.Domain.Conversion;

namespace Lunar.Application.Conversion.Models;

/// <summary>What to do when the output name is already taken.</summary>
public enum CollisionPolicy
{
    /// <summary>Keep the existing file and write the new one as "name (2)", "name (3)" and so on. The safe default.</summary>
    Rename,

    /// <summary>Replace an existing output file. Never replaces an input file or another output of the same request.</summary>
    Overwrite,

    /// <summary>Leave the file alone and do not convert this input.</summary>
    Skip
}

/// <summary>
/// What the user asked for: these files, as this format, into this folder. Paths must be full paths as the file picker or
/// drop gave them; nothing is read or created until the request is planned.
/// </summary>
/// <param name="SourcePaths">Full paths of the files to convert.</param>
/// <param name="Target">The format wanted.</param>
/// <param name="OutputDirectory">Full path of an existing folder for the results.</param>
/// <param name="Collision">What to do when an output name is taken (rename unless the user chose otherwise).</param>
/// <param name="Limits">The caps to apply; null means <see cref="ConversionLimits"/> defaults.</param>
public sealed record ConversionRequest(
    IReadOnlyList<string> SourcePaths,
    FileFormat Target,
    string OutputDirectory,
    CollisionPolicy Collision = CollisionPolicy.Rename,
    ConversionLimits? Limits = null);
