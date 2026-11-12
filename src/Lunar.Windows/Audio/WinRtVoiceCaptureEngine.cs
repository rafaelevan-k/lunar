using Lunar.Application.Abstractions.Audio;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Domain.Shared;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.Media.Render;
using Windows.Storage;

namespace Lunar.Windows.Audio;

/// <summary>
/// Records through Windows' audio graph (ADR-0022): microphone to an MP3 file node and to a frame node for the meter. No WinRT type leaves this
/// assembly, and no package is needed. Everything runs off the calling thread.
///
/// Opening the microphone is tried in more than one way, because some drivers refuse one of them. Intel Smart Sound Technology microphone arrays
/// have been seen to refuse the speech mode (Windows' voice processing) while working in the plain mode. The order is: the chosen or default
/// microphone in speech mode, then in plain mode; for Automatic also Windows' own default input without a device lookup, in both modes. Each
/// refusal is logged as a fixed event naming the mode and Windows' status, never the device.
/// </summary>
public sealed class WinRtVoiceCaptureEngine : IVoiceCaptureEngine
{
    private const string Component = "VoiceCapture";

    private static readonly MediaCategory[] Modes = [MediaCategory.Speech, MediaCategory.Other];

    private readonly IDiagnosticLog? _log;

    public WinRtVoiceCaptureEngine(IDiagnosticLog? log = null) => _log = log;

    public async Task<Result<IVoiceCaptureSession>> StartAsync(string? inputId, string partFilePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(partFilePath) || !Path.IsPathFullyQualified(partFilePath))
            return Failed(new OperationError(ErrorCode.InvalidInput, "The recording file needs a full path."));
        if (ct.IsCancellationRequested)
            return Result.Cancelled<IVoiceCaptureSession>();
        try
        {
            return await Task.Run(() => OpenAsync(inputId, partFilePath, ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled<IVoiceCaptureSession>();
        }
    }

    private static Result<IVoiceCaptureSession> Failed(OperationError error) => Result.Failure<IVoiceCaptureSession>(error);

    private async Task<Result<IVoiceCaptureSession>> OpenAsync(string? inputId, string path, CancellationToken ct)
    {
        var device = await FindDeviceAsync(inputId, ct).ConfigureAwait(false);
        // A chosen microphone that cannot be found is gone. For Automatic, Windows' own default input is still tried below.
        if (device.IsFailure && inputId is not null)
            return Failed(device.Error!);
        if (device.IsFailure)
            Log(DiagnosticLevel.Warning, "recording.device.lookup_failed");

        var created = CreateEmptyFile(path);
        if (created.IsFailure)
            return Failed(created.Error!);

        AudioGraph? graph = null;
        var started = false;
        try
        {
            var graphResult = await AudioGraph.CreateAsync(new AudioGraphSettings(AudioRenderCategory.Speech)).AsTask(ct).ConfigureAwait(false);
            if (graphResult.Status != AudioGraphCreationStatus.Success)
            {
                Log(DiagnosticLevel.Warning, "recording.graph." + Token(graphResult.Status.ToString()));
                return Failed(VoiceCaptureErrors.FromGraph(graphResult.Status));
            }

            graph = graphResult.Graph;
            var opened = await OpenInputAsync(graph, device.IsSuccess ? device.Value : null, useDefault: inputId is null, ct).ConfigureAwait(false);
            if (opened.IsFailure)
                return Failed(opened.Error!);
            var input = opened.Value;

            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(ct).ConfigureAwait(false);
            var output = await graph.CreateFileOutputNodeAsync(file, MediaEncodingProfile.CreateMp3(AudioEncodingQuality.High)).AsTask(ct).ConfigureAwait(false);
            if (output.Status != AudioFileNodeCreationStatus.Success)
            {
                Log(DiagnosticLevel.Warning, "recording.file." + Token(output.Status.ToString()));
                return Failed(VoiceCaptureErrors.FromFile(output.Status));
            }

            var meter = graph.CreateFrameOutputNode();
            input.AddOutgoingConnection(output.FileOutputNode);
            input.AddOutgoingConnection(meter);
            var session = new WinRtVoiceCaptureSession(graph, output.FileOutputNode, meter, input.Device?.Id ?? MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default));
            started = true;
            return Result.Success<IVoiceCaptureSession>(session);
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled<IVoiceCaptureSession>();
        }
        catch (UnauthorizedAccessException)
        {
            return Failed(new OperationError(ErrorCode.AccessDenied, "Windows is not letting apps use the microphone."));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or InvalidOperationException)
        {
            Log(DiagnosticLevel.Warning, "recording.start.hr_" + ex.HResult.ToString("x8", System.Globalization.CultureInfo.InvariantCulture), ex.GetType());
            return Failed(new OperationError(ErrorCode.Unexpected, "Recording could not be started.", retryable: true));
        }
        finally
        {
            if (!started)
            {
                graph?.Dispose();
                TryDelete(path);
            }
        }
    }

    /// <summary>Tries each way of opening the microphone in turn (see the class summary) and keeps the first that Windows accepts.</summary>
    private async Task<Result<AudioDeviceInputNode>> OpenInputAsync(AudioGraph graph, DeviceInformation? device, bool useDefault, CancellationToken ct)
    {
        var attempts = new List<InputAttempt<AudioDeviceInputNode>>();
        foreach (var mode in Modes)
        {
            if (device is not null)
                attempts.Add(new(Token(mode.ToString()) + ".device", () => Try(graph.CreateDeviceInputNodeAsync(mode, graph.EncodingProperties, device).AsTask(ct))));
        }

        if (useDefault)
        {
            foreach (var mode in Modes)
                attempts.Add(new(Token(mode.ToString()) + ".default", () => Try(graph.CreateDeviceInputNodeAsync(mode).AsTask(ct))));
        }

        return await InputAttempts.FirstAsync(attempts, (name, status) => Log(DiagnosticLevel.Warning, "recording.input." + name + "." + Token(status.ToString()))).ConfigureAwait(false);
    }

    private static async Task<(AudioDeviceInputNode? Node, AudioDeviceNodeCreationStatus Status)> Try(Task<CreateAudioDeviceInputNodeResult> opening)
    {
        try
        {
            var result = await opening.ConfigureAwait(false);
            return (result.Status == AudioDeviceNodeCreationStatus.Success ? result.DeviceInputNode : null, result.Status);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
        {
            return (null, AudioDeviceNodeCreationStatus.UnknownFailure);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, AudioDeviceNodeCreationStatus.AccessDenied);
        }
    }

    private static string Token(string text) => text.ToLowerInvariant();

    private void Log(DiagnosticLevel level, string eventId, Type? exceptionType = null) =>
        _log?.Write(new DiagnosticEvent(level, eventId, Component, "Start", ExceptionType: exceptionType));

    private static async Task<Result<DeviceInformation>> FindDeviceAsync(string? inputId, CancellationToken ct)
    {
        var none = new OperationError(ErrorCode.DeviceUnavailable, "No microphone was found.");
        var id = string.IsNullOrEmpty(inputId) ? MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default) : inputId;
        if (string.IsNullOrEmpty(id))
            return Result.Failure<DeviceInformation>(none);
        try
        {
            var device = await DeviceInformation.CreateFromIdAsync(id).AsTask(ct).ConfigureAwait(false);
            return device.IsEnabled ? Result.Success(device) : Result.Failure<DeviceInformation>(none);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or FileNotFoundException)
        {
            return Result.Failure<DeviceInformation>(none);
        }
    }

    // Create-new: a recording never takes over a file that is already there.
    private static Result CreateEmptyFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            return Result.Success();
        }
        catch (UnauthorizedAccessException)
        {
            return Result.Failure(new OperationError(ErrorCode.IoFailure, "The recording file could not be created there."));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return Result.Failure(new OperationError(ErrorCode.IoFailure, "The recording file could not be created."));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only the empty file this call created; the caller's next name is a new one.
        }
    }
}
