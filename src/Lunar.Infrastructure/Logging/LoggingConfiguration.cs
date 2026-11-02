namespace Lunar.Infrastructure.Logging;

/// <summary>Where logs go and how large they may get (ADR-0009).</summary>
public static class LoggingConfiguration
{
    public const long NormalFileSizeLimit = 1L * 1024 * 1024;
    public const long DiagnosticFileSizeLimit = 5L * 1024 * 1024;
    public const int RetainedFileCount = 7;

    /// <summary>Environment variable that opts in to diagnostics until a user setting exists (BE-06/FE-23).</summary>
    public const string DiagnosticsVariable = "LUNAR_DIAGNOSTICS";

    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lunar", "Logs");

    public static bool IsDiagnosticsRequested() =>
        IsDiagnosticsRequested(Environment.GetEnvironmentVariable(DiagnosticsVariable));

    /// <summary>Only the exact value "1" opts in; anything else, including "true", leaves diagnostics off.</summary>
    public static bool IsDiagnosticsRequested(string? value) => value == "1";
}
