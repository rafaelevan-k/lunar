using System.Security;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.System;
using Lunar.Domain.Shared;

namespace Lunar.Infrastructure.Startup;

/// <summary>
/// Writes one quoted value, "Lunar", to the current user's Run key. The path is validated before it is written
/// (absolute, local, an existing .exe, no quote or control characters), so the registered command is exactly one
/// executable with no arguments. The path itself is never logged (it contains the user name).
/// </summary>
public sealed class StartupRegistrationService : IStartupRegistration
{
    internal const string ValueName = "Lunar";

    private readonly string _executablePath;
    private readonly IRunKeyStore _store;
    private readonly IDiagnosticLog? _log;

    /// <param name="executablePath">The executable to start at sign-in, normally the running Lunar.exe.</param>
    public StartupRegistrationService(string executablePath, IDiagnosticLog? log = null)
        : this(executablePath, CreateDefaultStore(), log)
    {
    }

    internal StartupRegistrationService(string executablePath, IRunKeyStore store, IDiagnosticLog? log = null)
    {
        _executablePath = executablePath ?? string.Empty;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = log;
    }

    public Result<bool> IsEnabled()
    {
        var command = BuildCommand();
        if (command.IsFailure)
            return Result.Failure<bool>(command.Error!);

        try
        {
            var current = _store.Read(ValueName);
            return Result.Success(string.Equals(current, command.Value, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return Result.Failure<bool>(Fail("Read", ex));
        }
    }

    public Result Enable()
    {
        var command = BuildCommand();
        if (command.IsFailure)
            return Result.Failure(command.Error!);

        try
        {
            _store.Write(ValueName, command.Value!);
            Log(DiagnosticLevel.Information, "startup.enabled", "Enable");
            return Result.Success();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return Result.Failure(Fail("Enable", ex));
        }
    }

    public Result Disable()
    {
        try
        {
            _store.Delete(ValueName);
            Log(DiagnosticLevel.Information, "startup.disabled", "Disable");
            return Result.Success();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return Result.Failure(Fail("Disable", ex));
        }
    }

    private Result<string> BuildCommand()
    {
        var path = _executablePath;
        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.Any(c => c == '"' || char.IsControl(c))
            || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path))
        {
            return Result.Failure<string>(new OperationError(ErrorCode.InvalidInput,
                "Lunar cannot register itself for start with Windows from this location."));
        }

        return Result.Success("\"" + path + "\"");
    }

    private static bool IsExpected(Exception ex) =>
        ex is UnauthorizedAccessException or SecurityException or IOException or PlatformNotSupportedException;

    private OperationError Fail(string operation, Exception ex)
    {
        var (code, message) = ex switch
        {
            UnauthorizedAccessException or SecurityException => (ErrorCode.AccessDenied,
                "Windows did not allow changing the start with Windows setting."),
            PlatformNotSupportedException => (ErrorCode.Unsupported,
                "Start with Windows is not available on this system."),
            _ => (ErrorCode.IoFailure, "The start with Windows setting could not be saved.")
        };
        _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "startup.failed", "Startup", operation, code,
            ExceptionType: ex.GetType()));
        return new OperationError(code, message);
    }

    private void Log(DiagnosticLevel level, string eventId, string operation) =>
        _log?.Write(new DiagnosticEvent(level, eventId, "Startup", operation));

    private static IRunKeyStore CreateDefaultStore() =>
        OperatingSystem.IsWindows()
            ? new RegistryRunKeyStore()
            : throw new PlatformNotSupportedException("Start with Windows needs Windows.");
}
