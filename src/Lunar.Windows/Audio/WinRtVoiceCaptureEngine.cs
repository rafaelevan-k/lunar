using Lunar.Application.Abstractions.Audio;
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
/// </summary>
public sealed class WinRtVoiceCaptureEngine : IVoiceCaptureEngine
{
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

    private static async Task<Result<IVoiceCaptureSession>> OpenAsync(string? inputId, string path, CancellationToken ct)
    {
        var device = await FindDeviceAsync(inputId, ct).ConfigureAwait(false);
        if (device.IsFailure)
            return Failed(device.Error!);

        var created = CreateEmptyFile(path);
        if (created.IsFailure)
            return Failed(created.Error!);

        AudioGraph? graph = null;
        var started = false;
        try
        {
            var graphResult = await AudioGraph.CreateAsync(new AudioGraphSettings(AudioRenderCategory.Speech)).AsTask(ct).ConfigureAwait(false);
            if (graphResult.Status != AudioGraphCreationStatus.Success)
                return Failed(VoiceCaptureErrors.FromGraph(graphResult.Status));
            graph = graphResult.Graph;

            var input = await graph.CreateDeviceInputNodeAsync(MediaCategory.Speech, graph.EncodingProperties, device.Value).AsTask(ct).ConfigureAwait(false);
            if (input.Status != AudioDeviceNodeCreationStatus.Success)
                return Failed(VoiceCaptureErrors.FromInput(input.Status));

            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(ct).ConfigureAwait(false);
            var output = await graph.CreateFileOutputNodeAsync(file, MediaEncodingProfile.CreateMp3(AudioEncodingQuality.High)).AsTask(ct).ConfigureAwait(false);
            if (output.Status != AudioFileNodeCreationStatus.Success)
                return Failed(VoiceCaptureErrors.FromFile(output.Status));

            var meter = graph.CreateFrameOutputNode();
            input.DeviceInputNode.AddOutgoingConnection(output.FileOutputNode);
            input.DeviceInputNode.AddOutgoingConnection(meter);
            var session = new WinRtVoiceCaptureSession(graph, output.FileOutputNode, meter, device.Value.Id);
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
