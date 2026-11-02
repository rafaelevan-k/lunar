using System.IO.Pipes;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Shell;
using Lunar.Domain.Shared;

namespace Lunar.Infrastructure.Startup;

/// <summary>
/// Per-session single instance: a named mutex that is only ever created (never waited on) decides the role, and
/// the primary listens on a current-user-only named pipe for one constant "activate" byte (ADR-0008). No other
/// data crosses the pipe, so there is nothing to parse or log.
/// </summary>
public sealed class SingleInstanceCoordinator : IInstanceCoordinator
{
    private const byte ActivateRequest = 0x01;

    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FaultBackoff = TimeSpan.FromMilliseconds(250);

    private readonly string _mutexName;
    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _readTimeout;
    private readonly IDiagnosticLog? _log;
    private readonly object _gate = new();

    private Mutex? _mutex;
    private CancellationTokenSource? _listenerCancellation;
    private Task _listener = Task.CompletedTask;
    private InstanceRole? _role;
    private bool _disposed;

    /// <param name="name">Application-wide identifier; production uses one fixed value, tests a unique one.</param>
    public SingleInstanceCoordinator(string name, IDiagnosticLog? log = null)
        : this(name, DefaultConnectTimeout, DefaultReadTimeout, log)
    {
    }

    internal SingleInstanceCoordinator(string name, TimeSpan connectTimeout, TimeSpan readTimeout, IDiagnosticLog? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _mutexName = $@"Local\{name}.instance";
        _pipeName = $"{name}.activate";
        _connectTimeout = connectTimeout;
        _readTimeout = readTimeout;
        _log = log;
    }

    public event Action? ActivationRequested;

    /// <summary>Completes shortly after <see cref="Dispose"/>; lets tests observe that the listener stopped.</summary>
    internal Task ListenerCompletion => _listener;

    public Result<InstanceRole> Start()
    {
        lock (_gate)
        {
            if (_disposed)
                return Result.Failure<InstanceRole>(new OperationError(ErrorCode.InvalidInput, "The coordinator is closed."));
            if (_role is not null)
                return Result.Failure<InstanceRole>(new OperationError(ErrorCode.InvalidInput, "The coordinator was already started."));

            Mutex mutex;
            bool created;
            try
            {
                mutex = new Mutex(false, _mutexName, out created);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
            {
                return Result.Failure<InstanceRole>(new OperationError(ErrorCode.IoFailure, "The running instance could not be checked."));
            }

            if (!created)
            {
                mutex.Dispose();
                _role = InstanceRole.Secondary;
                return Result.Success(InstanceRole.Secondary);
            }

            _mutex = mutex;
            _role = InstanceRole.Primary;
            _listenerCancellation = new CancellationTokenSource();
            _listener = ListenAsync(_listenerCancellation);
            return Result.Success(InstanceRole.Primary);
        }
    }

    public async Task<Result> RequestPrimaryActivationAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_disposed || _role != InstanceRole.Secondary)
                return Result.Failure(new OperationError(ErrorCode.InvalidInput, "Only a secondary instance can request activation."));
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(_connectTimeout);
        try
        {
            await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(budget.Token).ConfigureAwait(false);
            await client.WriteAsync(new[] { ActivateRequest }, budget.Token).ConfigureAwait(false);
            await client.FlushAsync(budget.Token).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return ct.IsCancellationRequested
                ? Result.Cancelled()
                : Result.Failure(new OperationError(ErrorCode.Timeout, "The running instance did not respond.", retryable: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(new OperationError(ErrorCode.IoFailure, "The running instance could not be reached.", retryable: true));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;

            // Cancel only: the listener ends on its own and disposes the cancellation source, so shutdown never blocks.
            try
            {
                _listenerCancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The listener already ended and released its source; nothing is left to cancel.
            }

            _mutex?.Dispose();
            _mutex = null;
        }
    }

    private async Task ListenAsync(CancellationTokenSource owner)
    {
        var ct = owner.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                    using var read = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    read.CancelAfter(_readTimeout);
                    var buffer = new byte[1];
                    var count = await server.ReadAsync(buffer, read.Token).ConfigureAwait(false);
                    if (count == 1 && buffer[0] == ActivateRequest)
                        Raise();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    // A client connected but sent nothing in time: drop it and listen again.
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Client vanished or the pipe could not be created: back off so a persistent fault cannot spin.
                    _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "instance.listener.fault",
                        "SingleInstance", "Listen", ErrorCode.IoFailure, ExceptionType: ex.GetType()));
                    try
                    {
                        await Task.Delay(FaultBackoff, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            owner.Dispose();
        }
    }

    private void Raise()
    {
        try
        {
            ActivationRequested?.Invoke();
        }
        catch (Exception ex)
        {
            // Contract: handlers must not throw. One bad handler must not stop later activations.
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Error, "instance.handler.failed",
                "SingleInstance", "Activate", ErrorCode.Unexpected, ExceptionType: ex.GetType()));
        }
    }
}
