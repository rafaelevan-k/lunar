using System.Security.Cryptography;
using System.Text;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Usage;
using Lunar.Application.Usage;
using Lunar.Application.Usage.Models;
using Lunar.Domain.Shared;

namespace Lunar.Windows.Security;

/// <summary>Encrypts and decrypts a small secret for the current Windows user.</summary>
internal interface ISecretProtector
{
    byte[] Protect(byte[] plain);

    byte[] Unprotect(byte[] protectedBytes);
}

/// <summary>
/// Keeps the usage token on this PC, encrypted with Windows DPAPI for the signed-in user (ADR-0014 amendment, BE-33), in one file under
/// <c>%LocalAppData%\Lunar</c>. What that means:
/// - The file holds only the encrypted bytes (never the token in clear text); only the same Windows user on the same PC can decrypt it. A copy
///   of the file on another PC or account is unreadable noise.
/// - Saving writes a new file next to it and renames it over the old one, so a failure never leaves half a token; the clear bytes and the
///   plain-text copy of the token are wiped from memory buffers as soon as they have been used (a .NET string cannot be wiped; it is only held
///   while a request is made).
/// - Removing overwrites the file with zeros and deletes it. A missing token and an unreadable one both answer "paste it again".
/// - Calls are serialized; nothing about the token, the path or an exception text reaches a log or a message.
/// </summary>
public sealed class DpapiUsageTokenStore : IUsageTokenStore, IDisposable
{
    private const long MaxFileBytes = 16 * 1024;

    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly IDiagnosticLog? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DpapiUsageTokenStore(IDiagnosticLog? log = null)
        : this(DefaultPath, new DpapiProtector(), log)
    {
    }

    internal DpapiUsageTokenStore(string path, ISecretProtector protector, IDiagnosticLog? log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protector);
        _path = Path.GetFullPath(path);
        _protector = protector;
        _log = log;
    }

    /// <summary>Where the encrypted token lives by default: under the user's own local application data.</summary>
    public static string DefaultPath { get; } = Path.Combine(global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.LocalApplicationData), "Lunar", "usage-token.dat");

    public async Task<Result<bool>> HasTokenAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Result.Cancelled<bool>();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return Result.Success(File.Exists(_path));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result> SaveAsync(string pastedToken, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Result.Cancelled();
        var valid = UsageTokenRules.Validate(pastedToken);
        if (valid.IsFailure)
            return Result.Failure(valid.Error!);

        var plain = Encoding.UTF8.GetBytes(valid.Value);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            var sealedBytes = _protector.Protect(plain);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, sealedBytes, ct).ConfigureAwait(false);
            File.Move(temporary, _path, overwrite: true);
            temporary = null;
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "usage.token.save_failed", "Usage", "SaveToken", ErrorCode.IoFailure, ExceptionType: ex.GetType()));
            return Result.Failure(new OperationError(ErrorCode.IoFailure, "Lunar could not save the token on this PC. Try again.", retryable: true));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (temporary is not null)
                TryDelete(temporary);
            _gate.Release();
        }
    }

    public async Task<Result<UsageCredential>> GetAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Result.Cancelled<UsageCredential>();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        byte[]? plain = null;
        try
        {
            if (!File.Exists(_path))
                return Result.Failure<UsageCredential>(NoToken);
            if (new FileInfo(_path).Length is 0 or > MaxFileBytes)
                return Unreadable("usage.token.bad_size", null);

            var sealedBytes = await File.ReadAllBytesAsync(_path, ct).ConfigureAwait(false);
            plain = _protector.Unprotect(sealedBytes);
            var text = Encoding.UTF8.GetString(plain);
            return UsageTokenRules.Validate(text).IsSuccess ? Result.Success(new UsageCredential(text.Trim())) : Unreadable("usage.token.bad_content", null);
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled<UsageCredential>();
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return Unreadable("usage.token.read_failed", ex.GetType());
        }
        finally
        {
            if (plain is not null)
                CryptographicOperations.ZeroMemory(plain);
            _gate.Release();
        }
    }

    public async Task<Result> RemoveAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Result.Cancelled();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
                return Result.Success();

            // Overwrite first, so the encrypted bytes do not linger in the file's old blocks, then delete.
            var length = new FileInfo(_path).Length;
            await using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(new byte[Math.Min(length, MaxFileBytes)], ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Delete(_path);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "usage.token.remove_failed", "Usage", "RemoveToken", ErrorCode.IoFailure, ExceptionType: ex.GetType()));
            return Result.Failure(new OperationError(ErrorCode.IoFailure, "Lunar could not remove the token. Close other programs that may use it and try again.", retryable: true));
        }
        finally
        {
            _gate.Release();
        }
    }

    private static OperationError NoToken { get; } =
        new(ErrorCode.AccessDenied, "No Claude token is saved yet. Paste your token on the Usage page.");

    private Result<UsageCredential> Unreadable(string eventId, Type? exception)
    {
        _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, eventId, "Usage", "GetToken", ErrorCode.AccessDenied, ExceptionType: exception));
        return Result.Failure<UsageCredential>(new OperationError(ErrorCode.AccessDenied, "The saved token could not be read. Paste your token again."));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file holds only encrypted bytes; the next save uses a new name.
        }
    }

    public void Dispose() => _gate.Dispose();
}
