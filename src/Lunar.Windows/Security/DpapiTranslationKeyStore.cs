using System.Security.Cryptography;
using System.Text;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Translate;
using Lunar.Application.Translate;
using Lunar.Domain.Shared;

namespace Lunar.Windows.Security;

/// <summary>
/// The user's translation key (ADR-0028 §8), encrypted with Windows DPAPI for the signed-in user with its own entropy, in
/// <c>%LocalAppData%\Lunar\translate-key.dat</c>. Saving writes a new file and renames it over the old one; removing overwrites it with zeros and
/// deletes it. Clear bytes are wiped after use. Calls are serialized. Nothing about the key, the path or an exception text reaches a log or a message.
/// </summary>
public sealed class DpapiTranslationKeyStore : ITranslationKeyStore, IDisposable
{
    private const long MaxFileBytes = 16 * 1024;
    private const string Component = "Translate";

    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly IDiagnosticLog? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DpapiTranslationKeyStore(IDiagnosticLog? log = null)
        : this(DefaultPath, new DpapiProtector("Lunar.TranslateKey.v1"), log)
    {
    }

    internal DpapiTranslationKeyStore(string path, ISecretProtector protector, IDiagnosticLog? log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protector);
        _path = Path.GetFullPath(path);
        _protector = protector;
        _log = log;
    }

    public static string DefaultPath { get; } =
        Path.Combine(global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.LocalApplicationData), "Lunar", "translate-key.dat");

    public bool HasKey => File.Exists(_path);

    public async Task<Result> SaveAsync(string key, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Result.Cancelled();
        var valid = TranslationKeyRules.ValidateKey(key);
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
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "translate.key.save_failed", Component, "SaveKey", ErrorCode.IoFailure, ExceptionType: ex.GetType()));
            return Result.Failure(new OperationError(ErrorCode.IoFailure, "Lunar could not save the key on this PC. Try again.", retryable: true));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (temporary is not null)
                TryDelete(temporary);
            _gate.Release();
        }
    }

    public async Task<Result<string>> ReadAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Result.Cancelled<string>();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        byte[]? plain = null;
        try
        {
            if (!File.Exists(_path))
                return Missing();
            if (new FileInfo(_path).Length is 0 or > MaxFileBytes)
                return Unreadable(null);
            plain = _protector.Unprotect(await File.ReadAllBytesAsync(_path, ct).ConfigureAwait(false));
            var valid = TranslationKeyRules.ValidateKey(Encoding.UTF8.GetString(plain));
            return valid.IsSuccess ? valid : Unreadable(null);
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled<string>();
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return Unreadable(ex.GetType());
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
            var length = new FileInfo(_path).Length;
            await File.WriteAllBytesAsync(_path, new byte[Math.Min(length, MaxFileBytes)], ct).ConfigureAwait(false);
            File.Delete(_path);
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "translate.key.remove_failed", Component, "RemoveKey", ErrorCode.IoFailure, ExceptionType: ex.GetType()));
            return Result.Failure(new OperationError(ErrorCode.IoFailure, "Lunar could not remove the key. Try again.", retryable: true));
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private static Result<string> Missing() =>
        Result.Failure<string>(new OperationError(ErrorCode.AccessDenied, "No translation key is saved. Add your key in Settings."));

    private Result<string> Unreadable(Type? exception)
    {
        _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "translate.key.unreadable", Component, "ReadKey", ErrorCode.AccessDenied, ExceptionType: exception));
        return Result.Failure<string>(new OperationError(ErrorCode.AccessDenied, "The saved translation key can no longer be read. Add it again in Settings."));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file holds only encrypted bytes.
        }
    }
}
