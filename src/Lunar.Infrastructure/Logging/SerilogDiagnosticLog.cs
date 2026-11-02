using System.Globalization;
using System.Text;
using Lunar.Application.Abstractions.Diagnostics;
using Serilog;
using Serilog.Events;

namespace Lunar.Infrastructure.Logging;

/// <summary>
/// Writes <see cref="DiagnosticEvent"/>s to bounded rolling files (ADR-0009). Every text field is reduced to a
/// short token or replaced by "redacted", so nothing free-form can reach the file. Failures to create or write the
/// log are contained: <see cref="Write"/> never throws.
/// </summary>
public sealed class SerilogDiagnosticLog : IDiagnosticLog, IDisposable
{
    internal const string Redacted = "redacted";
    private const int MaxTokenLength = 64;
    private const string Absent = "-";

    private const string MessageTemplate =
        "{EventId} {Component}.{Operation} code={ErrorCode} ms={DurationMs} ex={ExceptionType}";

    private const string OutputTemplate = "{Timestamp:o} [{Level:u3}] {Message:lj} v={AppVersion} os={OsBuild}{NewLine}";

    private readonly Serilog.Core.Logger? _logger;
    private long _dropped;
    private bool _disposed;

    public SerilogDiagnosticLog(string directory, bool diagnosticsEnabled)
        : this(directory, diagnosticsEnabled,
            diagnosticsEnabled ? LoggingConfiguration.DiagnosticFileSizeLimit : LoggingConfiguration.NormalFileSizeLimit,
            LoggingConfiguration.RetainedFileCount)
    {
    }

    internal SerilogDiagnosticLog(string directory, bool diagnosticsEnabled, long fileSizeLimit, int retainedFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        try
        {
            Directory.CreateDirectory(directory);
            _logger = new LoggerConfiguration()
                .MinimumLevel.Is(diagnosticsEnabled ? LogEventLevel.Debug : LogEventLevel.Information)
                .Enrich.WithProperty("AppVersion", typeof(SerilogDiagnosticLog).Assembly.GetName().Version?.ToString() ?? Absent)
                .Enrich.WithProperty("OsBuild", Environment.OSVersion.Version.ToString())
                .WriteTo.File(
                    Path.Combine(directory, "lunar-.log"),
                    outputTemplate: OutputTemplate,
                    formatProvider: CultureInfo.InvariantCulture,
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: fileSizeLimit,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: retainedFiles,
                    shared: true,
                    encoding: new UTF8Encoding(false))
                .CreateLogger();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Logging must never stop the app from starting; the log stays inactive and drops events.
            _logger = null;
            InitializationFailure = ex.GetType().Name;
        }
    }

    /// <summary>False when the log directory or file could not be created; events are then dropped.</summary>
    public bool IsOperational => _logger is not null && !_disposed;

    /// <summary>Exception type name when creation failed, otherwise null.</summary>
    public string? InitializationFailure { get; }

    /// <summary>Events that were dropped because writing failed or the log is not operational.</summary>
    public long DroppedEvents => Interlocked.Read(ref _dropped);

    public void Write(DiagnosticEvent diagnosticEvent)
    {
        var logger = _logger;
        if (logger is null || _disposed || diagnosticEvent is null)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        try
        {
            logger.Write(
                ToSerilogLevel(diagnosticEvent.Level),
                MessageTemplate,
                Token(diagnosticEvent.EventId),
                Token(diagnosticEvent.Component),
                Token(diagnosticEvent.Operation),
                diagnosticEvent.Error?.ToString() ?? Absent,
                diagnosticEvent.Duration is { } d ? ((long)d.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) : Absent,
                TypeName(diagnosticEvent.ExceptionType));
        }
        catch (Exception)
        {
            // Contract: Write never throws. The drop is counted, not hidden.
            Interlocked.Increment(ref _dropped);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _logger?.Dispose();
    }

    /// <summary>Allows only short fixed-vocabulary tokens; everything else becomes "redacted".</summary>
    internal static string Token(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxTokenLength || !IsAsciiLetterOrDigit(value[0]))
            return Redacted;

        foreach (var c in value)
        {
            if (!IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))
                return Redacted;
        }

        return value;
    }

    private static string TypeName(Type? type)
    {
        if (type is null)
            return Absent;

        // Generic names look like "List`1"; keep only the part before the backtick.
        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        return Token(tick < 0 ? name : name[..tick]);
    }

    private static bool IsAsciiLetterOrDigit(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9');

    private static LogEventLevel ToSerilogLevel(DiagnosticLevel level) => level switch
    {
        DiagnosticLevel.Debug => LogEventLevel.Debug,
        DiagnosticLevel.Information => LogEventLevel.Information,
        DiagnosticLevel.Warning => LogEventLevel.Warning,
        DiagnosticLevel.Error => LogEventLevel.Error,
        _ => LogEventLevel.Information
    };
}
