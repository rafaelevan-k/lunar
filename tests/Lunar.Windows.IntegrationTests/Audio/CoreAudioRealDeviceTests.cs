using Lunar.Application.System.Models;
using Lunar.Windows.Audio;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Audio;

// These use the real Core Audio default output device of the machine they run on. They never make an audible change:
// writes set a value to the one it already has. A machine without an output device (for example a build server) skips
// them, since "no device" is covered with the fake backend. A real, audible change made by someone else is a manual
// check recorded in the BE-10 evidence.
public sealed class CoreAudioRealDeviceTests
{
    private static async Task<(CoreAudioVolumeService Service, VolumeState State)> OpenOrSkip()
    {
        var service = new CoreAudioVolumeService();
        var read = await service.GetAsync(CancellationToken.None);
        Assert.True(read.IsSuccess, "Reading must succeed whether or not a device exists.");
        if (read.Value.Capability != CapabilityStatus.Supported)
        {
            await service.DisposeAsync();
            Assert.Skip("This machine has no audio output device.");
        }

        return (service, read.Value);
    }

    [Fact]
    public async Task ReadsTheRealDefaultOutputVolumeAndMute()
    {
        var (service, state) = await OpenOrSkip();
        await using (service)
        {
            Assert.Equal(CapabilityStatus.Supported, state.Capability);
            Assert.InRange(state.Scalar, 0f, 1f);

            var again = await service.GetAsync(CancellationToken.None);
            Assert.True(again.IsSuccess);
            Assert.InRange(Math.Abs(again.Value.Scalar - state.Scalar), 0f, 0.05f); // nobody is expected to be changing it
        }
    }

    [Fact]
    public async Task WritingTheCurrentValuesSucceedsAndChangesNothingAudible()
    {
        var (service, state) = await OpenOrSkip();
        await using (service)
        {
            var volume = await service.SetVolumeAsync(state.Scalar, CancellationToken.None);
            var mute = await service.SetMuteAsync(state.IsMuted, CancellationToken.None);

            Assert.True(volume.IsSuccess, volume.Error?.SafeMessage);
            Assert.True(mute.IsSuccess, mute.Error?.SafeMessage);

            var after = await service.GetAsync(CancellationToken.None);
            Assert.Equal(state.IsMuted, after.Value.IsMuted);
            Assert.InRange(Math.Abs(after.Value.Scalar - state.Scalar), 0f, 0.011f); // drivers round to their own steps
        }
    }

    [Fact]
    public async Task OutOfRangeWritesAreRefusedBeforeTheDeviceIsTouched()
    {
        var (service, state) = await OpenOrSkip();
        await using (service)
        {
            Assert.True((await service.SetVolumeAsync(1.5f, CancellationToken.None)).IsFailure);
            Assert.True((await service.SetVolumeAsync(float.NaN, CancellationToken.None)).IsFailure);
            var after = await service.GetAsync(CancellationToken.None);
            Assert.InRange(Math.Abs(after.Value.Scalar - state.Scalar), 0f, 0.05f);
        }
    }

    [Fact]
    public async Task TheChangeNotificationPipelineStartsAndStopsCleanly()
    {
        var (service, state) = await OpenOrSkip();
        var seen = new List<VolumeState>();
        var observer = new Observer(seen);
        service.Changes.Subscribe(observer);

        await service.SetVolumeAsync(state.Scalar, CancellationToken.None); // may or may not raise a notification
        await Task.Delay(500, TestContext.Current.CancellationToken);
        await service.DisposeAsync();

        Assert.True(observer.Completed);
        Assert.All(seen, s => Assert.InRange(s.Scalar, 0f, 1f));
    }

    [Fact]
    public async Task CreatingAndDisposingManyServicesReleasesTheNativeObjects()
    {
        var (first, _) = await OpenOrSkip();
        await first.DisposeAsync();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetTotalMemory(forceFullCollection: true);

        for (var i = 0; i < 100; i++)
        {
            var service = new CoreAudioVolumeService();
            Assert.True((await service.GetAsync(CancellationToken.None)).IsSuccess);
            await service.DisposeAsync();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var after = GC.GetTotalMemory(forceFullCollection: true);
        Assert.True(after - before < 20_000_000, $"Managed memory grew by {after - before} bytes over 100 cycles.");
    }

    [Fact]
    public async Task SeveralServicesCanReadTheSameDeviceAtOnce()
    {
        var (probe, _) = await OpenOrSkip();
        await probe.DisposeAsync();

        var services = Enumerable.Range(0, 4).Select(_ => new CoreAudioVolumeService()).ToArray();
        try
        {
            var reads = await Task.WhenAll(services.SelectMany(s => Enumerable.Range(0, 10).Select(_ => s.GetAsync(CancellationToken.None))));

            Assert.All(reads, read => Assert.True(read.IsSuccess));
            Assert.All(reads, read => Assert.Equal(CapabilityStatus.Supported, read.Value.Capability));
        }
        finally
        {
            foreach (var service in services)
                await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheComBackendOpensTheEndpointReadsItAndRefusesUseAfterDisposal()
    {
        var (probe, _) = await OpenOrSkip();
        await probe.DisposeAsync();

        using var backend = new ComAudioBackend();
        var endpoint = backend.OpenDefault();
        Assert.NotNull(endpoint);
        var reading = endpoint.Read();
        Assert.InRange(reading.Scalar, 0f, 1f);

        endpoint.Dispose();
        endpoint.Dispose(); // idempotent
        Assert.Throws<ObjectDisposedException>(() => endpoint.Read());

        backend.Dispose();
        backend.Dispose();
        Assert.Throws<ObjectDisposedException>(() => backend.OpenDefault());
    }

    [Theory]
    [InlineData(unchecked((int)0x88890004), "DeviceGone")] // AUDCLNT_E_DEVICE_INVALIDATED
    [InlineData(unchecked((int)0x80070490), "DeviceGone")] // E_NOTFOUND
    [InlineData(unchecked((int)0x88890010), "DeviceGone")] // AUDCLNT_E_SERVICE_NOT_RUNNING
    [InlineData(unchecked((int)0x8007048F), "DeviceGone")] // ERROR_DEVICE_NOT_CONNECTED
    [InlineData(unchecked((int)0x80070005), "AccessDenied")] // E_ACCESSDENIED
    [InlineData(unchecked((int)0x80004005), "Other")] // E_FAIL
    [InlineData(unchecked((int)0x80070057), "Other")] // E_INVALIDARG
    public void HResultsAreClassifiedForTheService(int hresult, string expected) =>
        Assert.Equal(Enum.Parse<AudioFailure>(expected), ComAudioBackend.Classify(hresult));

    [Fact]
    public void CheckThrowsOnlyForFailureCodes()
    {
        ComAudioBackend.Check(0); // S_OK
        ComAudioBackend.Check(1); // S_FALSE is a success code
        var ex = Assert.Throws<AudioException>(() => ComAudioBackend.Check(unchecked((int)0x88890004)));
        Assert.Equal(AudioFailure.DeviceGone, ex.Failure);
        Assert.Equal(unchecked((int)0x88890004), ex.HResult);
    }

    private sealed class Observer(List<VolumeState> seen) : IObserver<VolumeState>
    {
        public bool Completed { get; private set; }

        public void OnNext(VolumeState value) => seen.Add(value);

        public void OnError(Exception error) => throw new InvalidOperationException("OnError must never be called.");

        public void OnCompleted() => Completed = true;
    }
}
