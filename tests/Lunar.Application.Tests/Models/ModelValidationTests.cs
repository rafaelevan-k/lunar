using Lunar.Application.Media.Models;
using Lunar.Application.System.Models;
using Xunit;

namespace Lunar.Application.Tests.Models;

public sealed class ModelValidationTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void VolumeStateAcceptsRange(float scalar)
    {
        var state = new VolumeState(scalar, isMuted: true, CapabilityStatus.Supported);

        Assert.Equal(scalar, state.Scalar);
        Assert.True(state.IsMuted);
    }

    [Theory]
    [InlineData(-0.01f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void VolumeStateRejectsOutOfRange(float scalar)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VolumeState(scalar, false, CapabilityStatus.Supported));
    }

    [Fact]
    public void VolumeStateRejectsUndefinedCapability()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VolumeState(0.1f, false, (CapabilityStatus)99));
    }

    [Fact]
    public void UnavailableStatesAreDistinctFromUnknown()
    {
        Assert.Equal(CapabilityStatus.Unavailable, VolumeState.Unavailable.Capability);
        Assert.Equal(CapabilityStatus.Unknown, WifiState.Unknown.Capability);
        Assert.Equal(RadioState.Unknown, BluetoothState.Unknown.Radio);
        Assert.False(WifiState.Unknown.CanToggle);
    }

    [Fact]
    public void MediaSnapshotRejectsBlankIdAndNegativeTimes()
    {
        Assert.ThrowsAny<ArgumentException>(() => Snapshot(id: " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => Snapshot(position: TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Snapshot(duration: TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Snapshot(status: (MediaPlaybackStatus)42));
    }

    [Fact]
    public void MediaSnapshotBoundsArtwork()
    {
        var atLimit = new byte[MediaSessionSnapshot.MaxArtworkBytes];
        var overLimit = new byte[MediaSessionSnapshot.MaxArtworkBytes + 1];

        Assert.Equal(atLimit.Length, Snapshot(artwork: atLimit).Artwork.Length);
        Assert.Throws<ArgumentException>(() => Snapshot(artwork: overLimit));
        Assert.True(Snapshot().Artwork.IsEmpty);
    }

    [Theory]
    [InlineData(true, 180, true)]
    [InlineData(true, 0, false)] // zero duration: live stream or unknown timeline
    [InlineData(false, 180, false)] // session does not report seek support
    public void CanSeekRequiresFiniteDurationAndCapability(bool capable, int durationSeconds, bool expected)
    {
        var snapshot = Snapshot(
            duration: TimeSpan.FromSeconds(durationSeconds),
            capabilities: new MediaCapabilities(true, true, true, capable));

        Assert.Equal(expected, snapshot.CanSeek);
    }

    [Fact]
    public void CanSeekIsFalseWithoutDuration()
    {
        Assert.False(Snapshot(duration: null, capabilities: new MediaCapabilities(true, true, true, true)).CanSeek);
    }

    internal static MediaSessionSnapshot Snapshot(
        string id = "session-1",
        MediaPlaybackStatus status = MediaPlaybackStatus.Playing,
        TimeSpan? position = null,
        TimeSpan? duration = null,
        MediaCapabilities? capabilities = null,
        byte[]? artwork = null) =>
        new(id, "App", "Title", "Artist", status, position, duration, capabilities ?? MediaCapabilities.None,
            isCurrent: true, artwork ?? []);
}
