using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Conversion.Models;
using Lunar.Conversion.Output;
using Lunar.Conversion.Pipeline;
using Lunar.Conversion.Validation;
using Lunar.Domain.Conversion;
using Lunar.Domain.Shared;

namespace Lunar.Conversion.Providers;

/// <summary>How the optional layout engine is run.</summary>
public sealed record LibreOfficeOptions
{
    /// <summary>How long one document may take before the engine is stopped. A proposal for the user to confirm.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How long to wait for a stopped engine to be gone before giving up on it.</summary>
    public TimeSpan StopWait { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Turns a Word document into a PDF with the user's own LibreOffice (BE-25, PRD section 5, ARCHITECTURE section 7). Lunar does not bundle,
/// download or install it; this only uses one that <see cref="ILibreOfficeLocator"/> found. Turning the PDF into pictures is then the
/// job of <see cref="PdfRasterizationProvider"/>. What it does and promises:
/// - Missing engine: a clear typed failure (<see cref="ErrorCode.DependencyMissing"/>) that says to install LibreOffice yourself; nothing is started.
/// - The original is never opened by the engine: a copy goes into the job's private working folder and the engine works on that copy, so
///   its lock and temporary files never appear next to the user's document.
/// - The engine gets its own fresh profile folder inside the working folder (<c>-env:UserInstallation</c>), so it never reads or changes
///   the user's LibreOffice settings, runs no user macros or extensions, and two jobs never share one.
/// - The command line is a list of separate arguments (never one concatenated string, never through a shell); the only values that vary
///   are paths Lunar made up itself inside the working folder.
/// - A document that links to outside content (a web address, another file, a remote template) is refused before the engine starts, so
///   converting never makes the engine fetch anything from the network or from elsewhere on the disk.
/// - The engine is stopped, with every process it started, when the job is cancelled or takes longer than <see cref="LibreOfficeOptions.Timeout"/>;
///   the call returns only after that process is gone (or, past <see cref="LibreOfficeOptions.StopWait"/>, after saying so in the log).
/// - The PDF it produces is checked like any input (a real PDF within the size limits) before it is handed on. Its output and messages are
///   never shown: a failure says only that the document could not be converted. Nothing carries a path, a file name or document text.
///
/// Honest limits: a layout engine lays the pages out its own way, so fonts and page breaks can differ from Word; this has not been compared
/// with Word on real documents (the evidence is deferred under ADR-0005, which is why the composition root keeps the routes off for now).
/// </summary>
public sealed partial class LibreOfficeRendererProvider
{
    private const int MaxRelationshipBytes = 1024 * 1024;

    private readonly ILibreOfficeLocator _locator;
    private readonly LibreOfficeOptions _options;
    private readonly IDiagnosticLog? _log;

    public LibreOfficeRendererProvider(ILibreOfficeLocator locator, LibreOfficeOptions? options = null, IDiagnosticLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(locator);
        _locator = locator;
        _options = options ?? new LibreOfficeOptions();
        _log = log;
    }

    /// <summary>
    /// Converts <paramref name="sourcePath"/> to a PDF at <paramref name="pdfOutputPath"/> (a new file inside <paramref name="workspace"/>).
    /// Returns the size of the PDF. Expected problems come back as typed failures; a cancel as <see cref="Result.Cancelled{T}"/>.
    /// </summary>
    public async Task<Result<long>> ConvertToPdfAsync(string sourcePath, string pdfOutputPath, TempWorkspace workspace, ConversionLimits limits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(pdfOutputPath);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(limits);
        if (ct.IsCancellationRequested)
            return Result.Cancelled<long>();
        if (!workspace.Owns(pdfOutputPath))
            return Fail(ErrorCode.Unexpected, "The conversion was set up wrongly. Nothing was saved.");

        try
        {
            if (PathRules.Check(sourcePath, limits.MaxPathChars) != PathProblem.None)
                return Fail(ErrorCode.InvalidInput, "The path of this file is not usable.");
            var (_, refusal) = SourceFileValidator.Inspect(sourcePath, FileFormat.Docx, limits);
            if (refusal is not null)
                return Result.Failure<long>(refusal.ToError());
            if (LinksOutside(sourcePath))
                return Fail(ErrorCode.InvalidInput, "This Word file links to content outside the file (a web address or another file). Lunar does not load that, so it was not converted.");

            var engine = _locator.Locate();
            if (engine is null)
            {
                return Fail(ErrorCode.DependencyMissing,
                    "Converting Word documents needs LibreOffice, which isn't installed. Install it yourself and try again; Lunar never installs or downloads it.");
            }

            ct.ThrowIfCancellationRequested();
            var copy = workspace.NewFilePath(".docx");
            await CopyAsync(sourcePath, copy, ct).ConfigureAwait(false);
            var profile = Directory.CreateDirectory(workspace.NewFilePath(".profile")).FullName;
            var outDirectory = Directory.CreateDirectory(workspace.NewFilePath(".out")).FullName;

            var ran = await RunEngineAsync(engine, profile, outDirectory, copy, ct).ConfigureAwait(false);
            if (ran is not null)
                return ran;

            var produced = Path.Combine(outDirectory, Path.GetFileNameWithoutExtension(copy) + ".pdf");
            if (!File.Exists(produced))
                return Fail(ErrorCode.Unexpected, "LibreOffice could not convert this document.");
            var (pdf, bad) = SourceFileValidator.Inspect(produced, FileFormat.Pdf, limits);
            if (bad is not null)
                return Fail(ErrorCode.Unexpected, "LibreOffice could not convert this document.");

            File.Move(produced, pdfOutputPath);
            return Result.Success(pdf!.Length);
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled<long>();
        }
        catch (Exception ex) when (IoErrors.IsIoProblem(ex))
        {
            return Result.Failure<long>(IoErrors.Map(ex));
        }
        catch (Exception ex)
        {
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Error, "conversion.libreoffice.fault", "Conversion", "LibreOffice", ErrorCode.Unexpected, ExceptionType: ex.GetType()));
            return Fail(ErrorCode.Unexpected, "LibreOffice could not convert this document.");
        }
    }

    // Returns null when the engine ran and exited normally (the caller then looks for the PDF); otherwise the outcome to hand back.
    private async Task<Result<long>?> RunEngineAsync(LibreOfficeInfo engine, string profile, string outDirectory, string input, CancellationToken ct)
    {
        var start = new ProcessStartInfo(engine.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            WorkingDirectory = outDirectory
        };

        // Separate arguments, never one string and never a shell. The profile is a fresh folder of this job; nothing varies but paths Lunar made.
        foreach (var argument in ArgumentsFor(profile, outDirectory, input))
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "conversion.libreoffice.start_failed", "Conversion", "LibreOffice", ErrorCode.DependencyMissing, ExceptionType: ex.GetType()));
            return Fail(ErrorCode.DependencyMissing, "LibreOffice could not be started. Check that it is installed correctly, then try again.");
        }

        // The engine's own output is read and thrown away so it can never fill a pipe and stall; it is not kept, shown or logged.
        process.OutputDataReceived += static (_, _) => { };
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = new CancellationTokenSource(_options.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var timedOut = timeout.IsCancellationRequested && !ct.IsCancellationRequested;
            await StopAsync(process).ConfigureAwait(false);
            return timedOut
                ? Result.Failure<long>(new OperationError(ErrorCode.Timeout, "LibreOffice took too long on this document and was stopped.", retryable: true))
                : Result.Cancelled<long>();
        }
        finally
        {
            // Whatever happened above, no engine process of this job is left running.
            if (!process.HasExited)
                await StopAsync(process).ConfigureAwait(false);
        }

        return process.ExitCode == 0 ? null : Fail(ErrorCode.Unexpected, "LibreOffice could not convert this document.");
    }

    internal static IEnumerable<string> ArgumentsFor(string profile, string outDirectory, string input)
    {
        yield return "--headless";
        yield return "--norestore";
        yield return "--nologo";
        yield return "--nolockcheck";
        yield return "--nodefault";
        yield return "-env:UserInstallation=" + new Uri(Path.GetFullPath(profile).TrimEnd('\\') + "\\").AbsoluteUri.TrimEnd('/');
        yield return "--convert-to";
        yield return "pdf:writer_pdf_Export";
        yield return "--outdir";
        yield return outDirectory;
        yield return input;
    }

    // Kills the engine and every process it started, then waits for it to be gone.
    private async Task StopAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // It ended by itself in the meantime, or it could not be stopped; the wait below tells which.
        }

        try
        {
            using var wait = new CancellationTokenSource(_options.StopWait);
            await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "conversion.libreoffice.stop_timeout", "Conversion", "LibreOffice", ErrorCode.Timeout));
        }
    }

    private static async Task CopyAsync(string from, string to, CancellationToken ct)
    {
        await using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var target = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await source.CopyToAsync(target, 81920, ct).ConfigureAwait(false);
    }

    // True when any relationship part of the package points outside it (TargetMode="External"): a web address, another file, a remote template.
    private static bool LinksOutside(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                continue;
            if (entry.Length > MaxRelationshipBytes)
                return true; // a relationship list that large is not a normal document
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            if (ExternalTarget().IsMatch(reader.ReadToEnd()))
                return true;
        }

        return false;
    }

    [GeneratedRegex("""TargetMode\s*=\s*["']External["']""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExternalTarget();

    private static Result<long> Fail(ErrorCode code, string message) => Result.Failure<long>(new OperationError(code, message));
}
