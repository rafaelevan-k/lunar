using System.Text;
using Lunar.Application.Abstractions.System;
using Lunar.Domain.Shared;

namespace Lunar.Infrastructure.Focus;

/// <summary>A small file beside the settings that exists only during a focus session in which Lunar turned Do Not Disturb on (ADR-0023).</summary>
public sealed class FileFocusSessionMarker : IFocusSessionMarker
{
    internal const string FileName = "focus.marker";
    private const int MaxBytes = 1024;
    private readonly string _path;

    public FileFocusSessionMarker(string? appDataFolder = null)
    {
        var root = appDataFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lunar");
        _path = Path.Combine(root, FileName);
    }

    public async Task<Result<string?>> ReadAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > MaxBytes)
                return Result.Success<string?>(null);
            var text = (await File.ReadAllTextAsync(_path, Encoding.UTF8, ct).ConfigureAwait(false)).Trim();
            return Result.Success<string?>(text.Length == 0 ? null : text);
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled<string?>();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return Result.Failure<string?>(new OperationError(ErrorCode.IoFailure, "The focus session marker could not be read."));
        }
    }

    public async Task<Result> SaveAsync(string restoreToken, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await File.WriteAllTextAsync(_path, restoreToken, Encoding.UTF8, ct).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return Result.Failure(new OperationError(ErrorCode.IoFailure, "The focus session marker could not be written."));
        }
    }

    public Task<Result> ClearAsync(CancellationToken ct)
    {
        try
        {
            File.Delete(_path);
            return Task.FromResult(Result.Success());
        }
        catch (DirectoryNotFoundException)
        {
            return Task.FromResult(Result.Success()); // no folder, so no marker: there is nothing to remove
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return Task.FromResult(Result.Failure(new OperationError(ErrorCode.IoFailure, "The focus session marker could not be removed.")));
        }
    }

    private static bool IsExpected(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}
