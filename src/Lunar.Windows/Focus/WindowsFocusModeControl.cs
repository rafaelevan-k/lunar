using System.Runtime.InteropServices;
using Lunar.Application.Abstractions.System;
using Lunar.Domain.Shared;

namespace Lunar.Windows.Focus;

/// <summary>
/// Do Not Disturb through the Windows shell (ADR-0023). "On" is reported only after Windows itself announces the new profile; the profile the shell hands
/// back is not enough on its own, because it is only what was stored. The announcement lags by a second or more, so it is waited for, within a limit.
/// </summary>
public sealed class WindowsFocusModeControl : IFocusModeControl
{
    internal const string Off = "Microsoft.QuietHoursProfile.Unrestricted";
    internal const string AlarmsOnly = "Microsoft.QuietHoursProfile.AlarmsOnly";
    private const string ProfilePrefix = "Microsoft.QuietHoursProfile.";
    private const int AnnouncedAlarmsOnly = 2;
    private const int ClassNotRegistered = unchecked((int)0x80040154);
    private const int NoInterface = unchecked((int)0x80004002);
    private const int NotImplemented = unchecked((int)0x80004001);
    private const int AccessDenied = unchecked((int)0x80070005);
    private static readonly TimeSpan CallLimit = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IQuietHoursBackend _backend;
    private readonly int _confirmAttempts;
    private readonly TimeSpan _confirmDelay;

    public WindowsFocusModeControl()
        : this(new ComQuietHoursBackend(), confirmAttempts: 34, confirmDelay: TimeSpan.FromMilliseconds(150))
    {
    }

    internal WindowsFocusModeControl(IQuietHoursBackend backend, int confirmAttempts, TimeSpan confirmDelay)
    {
        _backend = backend;
        _confirmAttempts = confirmAttempts;
        _confirmDelay = confirmDelay;
    }

    public Task<Result<FocusModeState>> ReadAsync(CancellationToken ct) => Guarded(() =>
    {
        var profile = _backend.GetProfile();
        return Task.FromResult(Result.Success(new FocusModeState(profile != Off, profile)));
    }, ct);

    public async Task<Result> TurnOnAsync(CancellationToken ct)
    {
        var outcome = await Guarded(async () =>
        {
            var earlier = _backend.GetProfile();
            if (earlier != AlarmsOnly)
                _backend.SetProfile(AlarmsOnly);
            try
            {
                if (_backend.GetProfile() == AlarmsOnly && await AnnouncedAsync(ct).ConfigureAwait(false))
                    return Result.Success(true);
            }
            catch (OperationCanceledException)
            {
                _backend.SetProfile(earlier); // a cancelled request leaves nothing switched on behind it
                throw;
            }

            _backend.SetProfile(earlier);
            return Result.Failure<bool>(new OperationError(ErrorCode.Timeout, "Windows did not confirm Do Not Disturb."));
        }, ct).ConfigureAwait(false);
        return outcome.IsSuccess ? Result.Success() : Result.Failure(outcome.Error!);
    }

    public async Task<Result> RestoreAsync(string restoreToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(restoreToken) || !restoreToken.StartsWith(ProfilePrefix, StringComparison.Ordinal))
            return Result.Failure(new OperationError(ErrorCode.InvalidInput, "That is not a Do Not Disturb state."));

        var outcome = await Guarded(() =>
        {
            _backend.SetProfile(restoreToken);
            return Task.FromResult(_backend.GetProfile() == restoreToken
                ? Result.Success(true)
                : Result.Failure<bool>(new OperationError(ErrorCode.Timeout, "Windows did not confirm Do Not Disturb.")));
        }, ct).ConfigureAwait(false);
        return outcome.IsSuccess ? Result.Success() : Result.Failure(outcome.Error!);
    }

    internal static OperationError ErrorFor(int hresult) => hresult switch
    {
        ClassNotRegistered or NoInterface or NotImplemented => new OperationError(ErrorCode.Unsupported, "This version of Windows does not let apps turn on Do Not Disturb."),
        AccessDenied => new OperationError(ErrorCode.AccessDenied, "Windows refused to change Do Not Disturb."),
        _ => new OperationError(ErrorCode.Unexpected, "Do Not Disturb could not be changed.", retryable: true)
    };

    private async Task<bool> AnnouncedAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < _confirmAttempts; attempt++)
        {
            if (_backend.ReadAnnouncedProfile() == AnnouncedAlarmsOnly)
                return true;
            await Task.Delay(_confirmDelay, ct).ConfigureAwait(false);
        }

        return _backend.ReadAnnouncedProfile() == AnnouncedAlarmsOnly;
    }

    private async Task<Result<T>> Guarded<T>(Func<Task<Result<T>>> action, CancellationToken ct)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled<T>();
        }

        try
        {
            // A stuck shell must not hold the caller; the limit is above the longest confirmation.
            return await Task.Run(action, ct).WaitAsync(CallLimit, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled<T>();
        }
        catch (TimeoutException)
        {
            return Result.Failure<T>(new OperationError(ErrorCode.Timeout, "Windows did not answer about Do Not Disturb."));
        }
        catch (COMException ex)
        {
            return Result.Failure<T>(ErrorFor(ex.HResult));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidCastException)
        {
            return Result.Failure<T>(ex is UnauthorizedAccessException ? ErrorFor(AccessDenied) : ErrorFor(NoInterface));
        }
        finally
        {
            _gate.Release();
        }
    }
}
