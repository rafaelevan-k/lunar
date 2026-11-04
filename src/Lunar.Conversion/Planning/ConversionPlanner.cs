using Lunar.Application.Abstractions.Conversion;
using Lunar.Application.Conversion.Models;
using Lunar.Conversion.Validation;
using Lunar.Domain.Conversion;
using Lunar.Domain.Shared;

namespace Lunar.Conversion.Planning;

/// <summary>
/// Plans a conversion request (BE-20, PRD FR-CON-01/02/03/05). For each file, in order: the path is checked as text, duplicates
/// are refused, the extension must name a supported format, the route to the chosen format must exist and what it needs must be
/// installed, then the file itself is inspected read-only (content matches the extension, size and shape within the limits), and
/// finally its output name is decided: cleaned, unique within the request, never an input file, and handled by the collision
/// policy against what is already in the folder. A file that fails any step is listed with its reason; the rest are planned.
///
/// Planning reads inputs and looks at the output folder but never writes, creates or deletes anything. Names are decided at
/// plan time against the folder as it is now; the output transaction (BE-26) decides again when it commits, because a file can
/// appear in between. Thread-safe: it holds no state between calls.
/// </summary>
public sealed class ConversionPlanner : IConversionPlanner
{
    private const int MaxCounter = 9_999;
    private const int CounterReserve = 7;   // " (9999)"
    private const int PageReserve = 12;     // "_page_" and up to six digits

    private readonly IRendererAvailability _renderer;
    private readonly TimeProvider _time;

    public ConversionPlanner(IRendererAvailability renderer, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        _renderer = renderer;
        _time = time ?? TimeProvider.System;
    }

    public async Task<Result<ConversionPlan>> PlanAsync(ConversionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ct.IsCancellationRequested)
            return Result.Cancelled<ConversionPlan>();

        ConversionLimits limits;
        try
        {
            limits = (request.Limits ?? new ConversionLimits()).Validated();
        }
        catch (ArgumentOutOfRangeException)
        {
            return Fail("The conversion limits are not valid.");
        }

        var paths = request.SourcePaths;
        if (paths is null || paths.Count == 0)
            return Fail("Choose at least one file to convert.");
        if (paths.Count > limits.MaxFilesPerRequest)
            return Fail($"Choose at most {limits.MaxFilesPerRequest} files at a time.");
        if (!Enum.IsDefined(request.Target) || !Enum.IsDefined(request.Collision))
            return Fail("The chosen format or option is not valid.");

        var directory = CheckOutputDirectory(request.OutputDirectory, limits);
        if (directory.Message is not null)
            return Fail(directory.Message);

        var outputDirectory = directory.Path!;
        var sources = new HashSet<string>(paths.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var jobs = new List<PlannedConversion>();
        var skipped = new List<SkippedFile>();
        var rejected = new List<RejectedFile>();
        bool? rendererAvailable = null;

        for (var index = 0; index < paths.Count; index++)
        {
            if (ct.IsCancellationRequested)
                return Result.Cancelled<ConversionPlan>();

            var path = paths[index];
            var name = DisplayName(path);

            ValidationFailure? failure = null;
            var problem = PathRules.Check(path, limits.MaxPathChars);
            if (problem != PathProblem.None)
                failure = PathRules.ToFailure(problem);
            else if (!PathRules.NamesAFile(path))
                failure = ValidationFailure.Invalid(RejectionReason.NotAFile, "This is a folder, not a file.");
            else if (!seen.Add(Normalize(path)))
                failure = ValidationFailure.Invalid(RejectionReason.Duplicate, "This file is already in the list.");

            ConversionRoute? route = null;
            FileFormat sourceFormat = default;
            if (failure is null)
            {
                if (!FileFormats.TryFromExtension(ExtensionOf(name), out sourceFormat))
                {
                    failure = ValidationFailure.Invalid(RejectionReason.UnsupportedType,
                        "This type of file isn't supported. Lunar converts PNG, JPG, JPEG, DOCX and PDF files.");
                }
                else
                {
                    route = ConversionPolicy.Resolve(sourceFormat, request.Target);
                    failure = await CheckRoute(route, ct, v => rendererAvailable = v, rendererAvailable).ConfigureAwait(false);
                    if (ct.IsCancellationRequested)
                        return Result.Cancelled<ConversionPlan>();
                }
            }

            InspectedSource? inspected = null;
            if (failure is null)
                (inspected, failure) = SourceFileValidator.Inspect(path, sourceFormat, limits);

            if (failure is not null)
            {
                rejected.Add(new RejectedFile(index, name, failure.Reason, failure.ToError()));
                continue;
            }

            var named = ResolveName(outputDirectory, path, route!, request.Collision, reserved, sources, limits);
            switch (named)
            {
                case { Failure: { } nameFailure }:
                    rejected.Add(new RejectedFile(index, name, nameFailure.Reason, nameFailure.ToError()));
                    break;
                case { Skip: true }:
                    skipped.Add(new SkippedFile(index, name));
                    break;
                default:
                    jobs.Add(new PlannedConversion(
                        index, path, sourceFormat, inspected!.Length, inspected.LastWriteUtc, route!,
                        outputDirectory, named.BaseName!, FileFormats.Extension(request.Target), named.Renamed, named.Replaces)
                    {
                        Limits = limits
                    });
                    break;
            }
        }

        return Result.Success(new ConversionPlan(jobs, skipped, rejected, limits, _time.GetUtcNow()));
    }

    // ---- route -------------------------------------------------------------------------------------------------------

    private async Task<ValidationFailure?> CheckRoute(ConversionRoute route, CancellationToken ct, Action<bool> remember, bool? known)
    {
        if (!route.IsSupported)
        {
            return new ValidationFailure(RejectionReason.RouteUnsupported, ErrorCode.Unsupported,
                "Converting this kind of file to the chosen format isn't available in this version.");
        }

        if (!route.RequiresExternalRenderer)
            return null;

        var available = known;
        if (available is null)
        {
            available = await _renderer.IsAvailableAsync(ct).ConfigureAwait(false);
            remember(available.Value);
        }

        return available.Value
            ? null
            : new ValidationFailure(RejectionReason.RendererMissing, ErrorCode.DependencyMissing,
                "Converting Word documents needs LibreOffice, which isn't installed. Install it yourself and try again; Lunar never installs or downloads it.");
    }

    // ---- output folder -----------------------------------------------------------------------------------------------

    private static (string? Path, string? Message) CheckOutputDirectory(string? directory, ConversionLimits limits)
    {
        var problem = PathRules.Check(directory, limits.MaxPathChars);
        if (problem != PathProblem.None)
        {
            return (null, problem switch
            {
                PathProblem.Empty => "Choose a folder for the results.",
                PathProblem.Network => "Results can't be saved to a network share in this version. Choose a folder on this PC.",
                PathProblem.PathTooLong or PathProblem.NameTooLong => "The folder's path is too long. Choose a folder closer to the top of a drive.",
                _ => "The folder for the results isn't a usable path. Choose a normal folder on this PC."
            });
        }

        var trimmed = directory!.Length > 3 ? directory.TrimEnd('\\', '/') : directory;
        try
        {
            if (!Directory.Exists(trimmed))
                return (null, "The folder for the results doesn't exist. Choose an existing folder.");
            if ((new DirectoryInfo(trimmed).Attributes & FileAttributes.ReparsePoint) != 0)
                return (null, "The folder for the results is a link to another place. Choose the real folder instead.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return (null, "Lunar can't open the folder for the results. Choose another folder.");
        }

        return (trimmed, null);
    }

    // ---- names and collisions ----------------------------------------------------------------------------------------

    private sealed record NameResult(string? BaseName, bool Renamed, bool Replaces, bool Skip, ValidationFailure? Failure);

    private static NameResult ResolveName(
        string directory,
        string sourcePath,
        ConversionRoute route,
        CollisionPolicy policy,
        HashSet<string> reserved,
        HashSet<string> sources,
        ConversionLimits limits)
    {
        var extension = FileFormats.Extension(route.Target);
        var perPage = route.ProducesOneOutputPerPage;
        var overhead = extension.Length + CounterReserve + (perPage ? PageReserve : 0);
        var maxBase = Math.Min(OutputNaming.MaxNameChars - overhead, limits.MaxPathChars - directory.Length - 1 - overhead);
        if (maxBase < 1)
        {
            return new NameResult(null, false, false, false,
                ValidationFailure.Invalid(RejectionReason.PathTooLong, "The folder's path is too long to save results there. Choose a folder closer to the top of a drive."));
        }

        var desired = OutputNaming.SanitizeBaseName(StemOf(DisplayName(sourcePath)), maxBase);
        for (var counter = 1; counter <= MaxCounter; counter++)
        {
            var candidate = OutputNaming.WithCounter(desired, counter);
            var state = Probe(directory, candidate, extension, perPage, reserved, sources);

            if (state == Occupant.None)
            {
                Reserve(directory, candidate, extension, perPage, reserved);
                return new NameResult(candidate, counter > 1, false, false, null);
            }

            if (state == Occupant.Input && policy == CollisionPolicy.Overwrite && counter == 1)
            {
                return new NameResult(null, false, false, false, new ValidationFailure(
                    RejectionReason.OutputWouldReplaceOriginal, ErrorCode.InvalidInput,
                    "Replacing would overwrite one of your original files. Choose another folder, or keep the existing files by choosing to rename."));
            }

            if (policy == CollisionPolicy.Skip && state is Occupant.Input or Occupant.File)
                return new NameResult(null, false, false, true, null);

            // Only the file at the name the user would expect is replaced; a name that was already moved aside is never overwritten.
            if (policy == CollisionPolicy.Overwrite && state == Occupant.File && counter == 1)
            {
                Reserve(directory, candidate, extension, perPage, reserved);
                return new NameResult(candidate, false, true, false, null);
            }
        }

        return new NameResult(null, false, false, false,
            ValidationFailure.Invalid(RejectionReason.NoFreeName, "Lunar couldn't find a free name for the result in that folder. Choose another folder."));
    }

    private enum Occupant
    {
        None,
        Batch,
        Input,
        File,
        Folder
    }

    private static Occupant Probe(string directory, string baseName, string extension, bool perPage, HashSet<string> reserved, HashSet<string> sources)
    {
        if (perPage)
        {
            if (reserved.Contains(PageKey(directory, baseName, extension)))
                return Occupant.Batch;

            var prefix = baseName + "_page_";
            foreach (var source in sources)
            {
                var split = source.LastIndexOf('\\');
                if (split > 0 && string.Equals(source[..split], directory, StringComparison.OrdinalIgnoreCase)
                    && source[(split + 1)..].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && source.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    return Occupant.Input;
                }
            }

            try
            {
                return Directory.EnumerateFiles(directory, OutputNaming.PagePattern(baseName, extension)).Any() ? Occupant.File : Occupant.None;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Occupant.None;
            }
        }

        var path = Path.Combine(directory, baseName + extension);
        if (reserved.Contains(path))
            return Occupant.Batch;
        if (sources.Contains(path))
            return Occupant.Input;
        try
        {
            if (Directory.Exists(path))
                return Occupant.Folder;
            return File.Exists(path) ? Occupant.File : Occupant.None;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Occupant.None;
        }
    }

    private static void Reserve(string directory, string baseName, string extension, bool perPage, HashSet<string> reserved) =>
        reserved.Add(perPage ? PageKey(directory, baseName, extension) : Path.Combine(directory, baseName + extension));

    private static string PageKey(string directory, string baseName, string extension) => directory + "|" + baseName + "_page_|" + extension;

    // ---- text helpers ------------------------------------------------------------------------------------------------

    private static Result<ConversionPlan> Fail(string message) =>
        Result.Failure<ConversionPlan>(new OperationError(ErrorCode.InvalidInput, message));

    private static string Normalize(string path) => path.Replace('/', '\\');

    // The last part of a path, for showing the user which file a message is about (never inside a message or a log).
    private static string DisplayName(string path)
    {
        var text = path ?? string.Empty;
        var split = Math.Max(text.LastIndexOf('\\'), text.LastIndexOf('/'));
        var name = split >= 0 ? text[(split + 1)..] : text;
        var cleaned = new string([.. name.Select(c => c < ' ' || c == '\u007f' ? '_' : c)]);
        if (cleaned.Length > OutputNaming.MaxNameChars)
            cleaned = cleaned[..OutputNaming.MaxNameChars];
        return cleaned.Length == 0 ? "(no name)" : cleaned;
    }

    private static string ExtensionOf(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot < 0 ? string.Empty : fileName[dot..];
    }

    private static string StemOf(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot <= 0 ? fileName : fileName[..dot];
    }
}
