using System.Security;
using Lunar.Application.Abstractions.System;
using Lunar.Domain.Shared;

namespace Lunar.Infrastructure.Startup;

/// <summary>
/// What a full uninstall does for the current user (ADR-0018): always removes Lunar's start-with-Windows entry, and deletes the user's
/// Lunar data folder (settings, logs, temporary work, the usage token) only when the user said yes. The folder is fixed by the caller,
/// never taken from the command line. Each step is attempted even if the other fails.
/// </summary>
public sealed class UninstallCleanup
{
    private readonly IStartupRegistration _startup;
    private readonly string _dataFolder;

    /// <param name="startup">Removes the start-with-Windows entry.</param>
    /// <param name="dataFolder">The user's Lunar data folder, normally <see cref="DefaultDataFolder"/>.</param>
    public UninstallCleanup(IStartupRegistration startup, string dataFolder)
    {
        _startup = startup ?? throw new ArgumentNullException(nameof(startup));
        if (string.IsNullOrWhiteSpace(dataFolder)
            || !Path.IsPathFullyQualified(dataFolder)
            || !string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(dataFolder)), "Lunar", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The data folder must be an absolute path to a folder named Lunar.", nameof(dataFolder));
        }

        _dataFolder = Path.TrimEndingDirectorySeparator(dataFolder);
    }

    /// <summary>%LocalAppData%\Lunar (the install folder is %LocalAppData%\Programs\Lunar and is not touched).</summary>
    public static string DefaultDataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lunar");

    /// <summary>Runs the cleanup; both steps are always tried and reported separately.</summary>
    public UninstallCleanupResult Run(bool deleteUserData)
    {
        var startup = _startup.Disable();
        var data = deleteUserData ? DeleteDataFolder() : Result.Success();
        return new UninstallCleanupResult(startup, data);
    }

    private Result DeleteDataFolder()
    {
        try
        {
            var folder = new DirectoryInfo(_dataFolder);
            if (!folder.Exists)
                return Result.Success();
            // A link in place of the folder is removed as a link; its target is never followed.
            if (folder.LinkTarget is not null)
                folder.Delete();
            else
                folder.Delete(recursive: true);
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return Result.Failure(new OperationError(
                ex is IOException ? ErrorCode.IoFailure : ErrorCode.AccessDenied,
                "Some Lunar settings or logs could not be deleted."));
        }
    }
}

/// <summary>What each cleanup step did. <see cref="Data"/> is a success when the data was kept.</summary>
public sealed record UninstallCleanupResult(Result Startup, Result Data)
{
    public bool IsSuccess => Startup.IsSuccess && Data.IsSuccess;
}
