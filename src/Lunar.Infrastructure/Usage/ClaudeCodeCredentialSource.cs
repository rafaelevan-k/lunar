using System.Security.Cryptography;
using System.Text.Json;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Usage;
using Lunar.Application.Usage.Models;
using Lunar.Domain.Shared;

namespace Lunar.Infrastructure.Usage;

/// <summary>
/// Gives the usage adapter the access token of the user's own Claude Code login (ADR-0015). It is a narrow, read-only reader of one file,
/// <c>%USERPROFILE%\.claude\.credentials.json</c>, that Claude Code writes when the user signs in with its normal browser login:
/// - it reads only <c>claudeAiOauth.accessToken</c>, <c>expiresAt</c> and <c>scopes</c>; every other field, the refresh token included, is skipped
///   while scanning and never turned into text;
/// - it only opens the file for reading and allows others to write at the same time, so Claude Code is never blocked and nothing is ever
///   written, renamed or locked;
/// - the bytes it read are wiped as soon as the three fields are out, and the token is handed on for one request, never stored, logged or shown;
/// - it never signs in, never renews the token and never contacts anyone.
/// Before any network request it refuses what cannot work, each with its own fixed message (no token, path or file text in a message): no file,
/// an unreadable or odd file, no token, a login without the <c>user:profile</c> permission (a token from "claude setup-token" has none), and an
/// expired token (with a minute of margin). Every refusal is <see cref="ErrorCode.AccessDenied"/>, which pauses the monitor until the island
/// opens again or the user refreshes. Thread-safe: it keeps no state.
/// </summary>
public sealed class ClaudeCodeCredentialSource : IUsageCredentialSource
{
    internal const long MaxFileBytes = 64 * 1024;
    internal const int MaxTokenChars = 4096;
    internal const string RequiredScope = "user:profile";
    internal static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(1);

    private readonly string _path;
    private readonly IDiagnosticLog? _log;
    private readonly TimeProvider _time;

    public ClaudeCodeCredentialSource(IDiagnosticLog? log = null)
        : this(DefaultPath, log, TimeProvider.System)
    {
    }

    internal ClaudeCodeCredentialSource(string path, IDiagnosticLog? log, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(time);
        _path = path;
        _log = log;
        _time = time;
    }

    /// <summary>Where Claude Code keeps its login on Windows.</summary>
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    internal static OperationError NotSignedIn { get; } = new(ErrorCode.AccessDenied,
        "Claude Code is not signed in on this PC. Open a terminal, run \"claude\" and sign in, then press Refresh.");

    internal static OperationError Unreadable { get; } = new(ErrorCode.AccessDenied,
        "Lunar could not read the Claude Code sign-in. Run \"claude\" in a terminal and sign in again, then press Refresh.");

    internal static OperationError NoPermission { get; } = new(ErrorCode.AccessDenied,
        "The Claude Code sign-in on this PC does not allow reading usage. Sign in again with \"claude\" (not \"claude setup-token\"), then press Refresh.");

    internal static OperationError Expired { get; } = new(ErrorCode.AccessDenied,
        "The Claude Code sign-in has expired. Use Claude Code once (run \"claude\") and Lunar will pick up the new one.");

    public Task<Result<UsageCredential>> GetAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Task.FromResult(Result.Cancelled<UsageCredential>());

        // A small local file: read it on the calling thread. No await is needed, so nothing is left running.
        return Task.FromResult(Read());
    }

    private Result<UsageCredential> Read()
    {
        byte[]? bytes = null;
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists)
                return Refuse("usage.login.missing", NotSignedIn);
            if (info.LinkTarget is not null || info.Length is 0 or > MaxFileBytes)
                return Refuse("usage.login.unreadable", Unreadable);

            bytes = new byte[info.Length];
            using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan))
            {
                var total = 0;
                int read;
                while (total < bytes.Length && (read = stream.Read(bytes, total, bytes.Length - total)) > 0)
                    total += read;
                if (total != bytes.Length)
                    return Refuse("usage.login.unreadable", Unreadable); // the file changed while it was read
            }

            var fields = Scan(bytes);
            if (fields is null)
                return Refuse("usage.login.unreadable", Unreadable);
            if (string.IsNullOrEmpty(fields.AccessToken) || fields.AccessToken.Length > MaxTokenChars || fields.AccessToken.Any(c => c is < '!' or > '~'))
                return Refuse("usage.login.unreadable", Unreadable);
            if (!fields.Scopes.Contains(RequiredScope, StringComparer.Ordinal))
                return Refuse("usage.login.no_permission", NoPermission);
            if (fields.ExpiresAtMilliseconds is not { } expiresAt)
                return Refuse("usage.login.unreadable", Unreadable);

            var expires = expiresAt is > 0 and < 253_402_300_799_000 ? DateTimeOffset.FromUnixTimeMilliseconds(expiresAt) : (DateTimeOffset?)null;
            if (expires is null)
                return Refuse("usage.login.unreadable", Unreadable);
            if (_time.GetUtcNow() >= expires.Value - ExpiryMargin)
                return Refuse("usage.login.expired", Expired);

            return Result.Success(new UsageCredential(fields.AccessToken));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        {
            return Refuse("usage.login.unreadable", Unreadable);
        }
        finally
        {
            if (bytes is not null)
                CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private sealed record LoginFields(string? AccessToken, long? ExpiresAtMilliseconds, IReadOnlyList<string> Scopes);

    // Walks the JSON once. Only the three wanted fields of "claudeAiOauth" become values; everything else is skipped without being copied.
    private static LoginFields? Scan(byte[] bytes)
    {
        var span = bytes.AsSpan();
        if (span.StartsWith("﻿"u8))
            span = span[3..];

        try
        {
            var reader = new Utf8JsonReader(span, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return null;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isLogin = reader.ValueTextEquals("claudeAiOauth"u8);
                reader.Read();
                if (!isLogin)
                {
                    reader.Skip();
                    continue;
                }

                return reader.TokenType == JsonTokenType.StartObject ? ReadLogin(ref reader) : null;
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static LoginFields? ReadLogin(ref Utf8JsonReader reader)
    {
        string? token = null;
        long? expires = null;
        var scopes = new List<string>();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("accessToken"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.String)
                    return null;
                token = reader.GetString();
            }
            else if (reader.ValueTextEquals("expiresAt"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out var value))
                    return null;
                expires = value;
            }
            else if (reader.ValueTextEquals("scopes"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.StartArray)
                    return null;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.String)
                        return null;
                    if (scopes.Count < 64)
                        scopes.Add(reader.GetString() ?? string.Empty);
                }
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        return new LoginFields(token, expires, scopes);
    }

    private Result<UsageCredential> Refuse(string eventId, OperationError error)
    {
        _log?.Write(new DiagnosticEvent(DiagnosticLevel.Debug, eventId, "Usage", "Login", error.Code));
        return Result.Failure<UsageCredential>(error);
    }
}
