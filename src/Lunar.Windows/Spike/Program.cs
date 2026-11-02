using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.Devices.Radios;
using Windows.Media.Control;

namespace Lunar.Windows.Spike;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main()
    {
        // No device identifiers, SSIDs, titles, paths or raw exception messages.
        Run("CoreAudio", NativeProbes.Audio);
        Run("NativeWifi", NativeProbes.Wifi);
        Run("Monitors", NativeProbes.Monitors);
        await RunAsync("BluetoothRadios", async ct =>
        {
            var radios = await Radio.GetRadiosAsync().AsTask(ct);
            var bluetooth = radios.Where(r => r.Kind == RadioKind.Bluetooth).ToArray();
            return new
            {
                Status = bluetooth.Length == 0 ? "DeviceAbsent" : "Available",
                Count = bluetooth.Length,
                States = bluetooth.Select(r => r.State.ToString()).ToArray(),
                Toggle = "NOT_RUN: no permission request or state change"
            };
        });
        await RunAsync("GSMTC", async ct =>
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(ct);
            var sessions = manager.GetSessions();
            return new
            {
                Status = sessions.Count == 0 ? "NoSession" : "Available",
                Count = sessions.Count,
                HasCurrentSession = manager.GetCurrentSession() is not null,
                Sessions = sessions.Select(session =>
                {
                    var controls = session.GetPlaybackInfo().Controls;
                    var timeline = session.GetTimelineProperties();
                    return new
                    {
                        controls.IsPlayEnabled,
                        controls.IsPauseEnabled,
                        controls.IsNextEnabled,
                        controls.IsPreviousEnabled,
                        controls.IsPlaybackPositionEnabled,
                        HasFiniteTimeline = timeline.EndTime > timeline.StartTime,
                        CanOfferSeek = controls.IsPlaybackPositionEnabled && timeline.EndTime > timeline.StartTime
                    };
                }).ToArray(),
                Commands = "NOT_RUN: discovery does not change playback"
            };
        });
        // Exit 0 means probe runner completed; inspect each result, not this code, for feasibility.
        return 0;
    }

    private static void Run(string component, Func<object> action)
    {
        try { Write(component, action()); }
        catch (Exception ex) { Write(component, Failure(ex)); }
    }

    private static async Task RunAsync(string component, Func<CancellationToken, Task<object>> action)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try { Write(component, await action(timeout.Token)); }
        catch (Exception ex) { Write(component, Failure(ex)); }
    }

    private static object Failure(Exception ex) => new
    {
        Status = ex switch
        {
            OperationCanceledException => "Timeout",
            UnauthorizedAccessException => "AccessDenied",
            COMException when ex.HResult == unchecked((int)0x80070490) => "DeviceAbsent",
            _ => "ProbeFailed"
        },
        HResult = $"0x{ex.HResult:X8}",
        ErrorType = ex.GetType().Name
    };

    private static void Write(string component, object result) => Console.WriteLine(
        JsonSerializer.Serialize(new { Timestamp = DateTimeOffset.UtcNow, Component = component, Result = result }));
}
