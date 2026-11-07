using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Domain.Shared;
using Lunar.Windows.Shell;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Shell;

/// <summary>Result mapping of the capture exclusion, with the user32 calls replaced.</summary>
public sealed class WindowsCaptureExclusionTests
{
    private sealed class RecordingLog : IDiagnosticLog
    {
        public List<DiagnosticEvent> Events { get; } = [];

        public void Write(DiagnosticEvent diagnosticEvent) => Events.Add(diagnosticEvent);
    }

    private sealed class FakeNative : ICaptureAffinityNative
    {
        public bool Alive { get; init; } = true;
        public int SetError { get; init; }
        public int GetError { get; init; }
        public uint ReadBack { get; set; } = WindowsCaptureExclusion.WdaExcludeFromCapture;
        public List<uint> Applied { get; } = [];
        public int Calls { get; private set; }

        public nint Root { get; set; } = -1; // -1: every window is its own root
        public nint Hook { get; init; } = 7;
        public WinEventCallback? Callback { get; private set; }
        public List<nint> Removed { get; } = [];

        public bool IsWindow(nint hwnd)
        {
            Calls++;
            return Alive;
        }

        public nint GetRoot(nint hwnd) => Root == -1 ? hwnd : Root;

        public nint InstallHook(uint firstEvent, uint lastEvent, WinEventCallback callback)
        {
            Calls++;
            Assert.Equal(WindowsCaptureExclusion.EventObjectCreate, firstEvent);
            Assert.Equal(WindowsCaptureExclusion.EventObjectShow, lastEvent);
            Callback = callback;
            return Hook;
        }

        public void RemoveHook(nint hook) => Removed.Add(hook);

        public bool SetAffinity(nint hwnd, uint affinity, out int win32Error)
        {
            Calls++;
            win32Error = SetError;
            if (SetError == 0)
                Applied.Add(affinity);
            return SetError == 0;
        }

        public bool GetAffinity(nint hwnd, out uint affinity, out int win32Error)
        {
            Calls++;
            affinity = ReadBack;
            win32Error = GetError;
            return GetError == 0;
        }
    }

    private const int CurrentBuild = 26300;

    [Fact]
    public void ALiveWindowIsExcludedAndConfirmedWithoutLogging()
    {
        var native = new FakeNative();
        var log = new RecordingLog();

        var result = new WindowsCaptureExclusion(native, CurrentBuild, log).Exclude(42);

        Assert.True(result.IsSuccess);
        Assert.Equal([WindowsCaptureExclusion.WdaExcludeFromCapture], native.Applied);
        Assert.Empty(log.Events);
    }

    [Fact]
    public void ABuildOlderThanVersion2004IsUnsupportedAndWindowsIsNotAsked()
    {
        var native = new FakeNative();

        var result = new WindowsCaptureExclusion(native, WindowsCaptureExclusion.FirstSupportedBuild - 1).Exclude(42);

        Assert.Equal(ErrorCode.Unsupported, result.Error!.Code);
        Assert.Equal(0, native.Calls);
    }

    [Fact]
    public void BuildVersion2004ItselfIsSupported() =>
        Assert.True(new WindowsCaptureExclusion(new FakeNative(), WindowsCaptureExclusion.FirstSupportedBuild).Exclude(42).IsSuccess);

    [Fact]
    public void AZeroHandleIsInvalidInputAndWindowsIsNotAsked()
    {
        var native = new FakeNative();

        var result = new WindowsCaptureExclusion(native, CurrentBuild).Exclude(0);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Equal(0, native.Calls);
    }

    [Fact]
    public void AHandleThatIsNoLongerAWindowIsInvalidInput()
    {
        var native = new FakeNative { Alive = false };

        var result = new WindowsCaptureExclusion(native, CurrentBuild).Exclude(42);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Empty(native.Applied);
    }

    [Theory]
    [InlineData(WindowsCaptureExclusion.ErrorAccessDenied, ErrorCode.AccessDenied)]
    [InlineData(WindowsCaptureExclusion.ErrorInvalidParameter, ErrorCode.AccessDenied)]
    [InlineData(8, ErrorCode.AccessDenied)]
    [InlineData(WindowsCaptureExclusion.ErrorInvalidWindowHandle, ErrorCode.InvalidInput)]
    public void ARefusedSetIsATypedFailureLoggedWithItsCodeOnly(int win32Error, ErrorCode expected)
    {
        var log = new RecordingLog();

        var result = new WindowsCaptureExclusion(new FakeNative { SetError = win32Error }, CurrentBuild, log).Exclude(42);

        Assert.Equal(expected, result.Error!.Code);
        var logged = Assert.Single(log.Events);
        Assert.Equal(new DiagnosticEvent(DiagnosticLevel.Warning, "capture.exclude.failed", "Shell", "ExcludeFromCapture", expected), logged);
    }

    [Fact]
    public void AFailedReadBackIsATypedFailure() =>
        Assert.Equal(ErrorCode.AccessDenied,
            new WindowsCaptureExclusion(new FakeNative { GetError = 5 }, CurrentBuild).Exclude(42).Error!.Code);

    [Theory]
    [InlineData(0x00u)] // WDA_NONE: Windows accepted the call but nothing is applied.
    [InlineData(0x01u)] // WDA_MONITOR: what builds before 2004 apply instead, a black box in captures.
    public void AReadBackOtherThanExcludeFromCaptureIsUnsupported(uint readBack) =>
        Assert.Equal(ErrorCode.Unsupported,
            new WindowsCaptureExclusion(new FakeNative { ReadBack = readBack }, CurrentBuild).Exclude(42).Error!.Code);

    // ---- ExcludeEveryNewWindow (FE-30) ------------------------------------------------------------------------

    [Fact]
    public void TheWatcherExcludesATopLevelWindowWhenItIsShownAndStopsWhenDisposed()
    {
        var native = new FakeNative { ReadBack = 0 };
        var exclusion = new WindowsCaptureExclusion(native, CurrentBuild);

        var watch = exclusion.ExcludeEveryNewWindow();
        Assert.True(watch.IsSuccess);
        native.Callback!(7, WindowsCaptureExclusion.EventObjectShow, 42, WindowsCaptureExclusion.ObjIdWindow, 0, 1, 0);
        Assert.Equal([WindowsCaptureExclusion.WdaExcludeFromCapture], native.Applied);

        watch.Value.Dispose();
        watch.Value.Dispose();
        Assert.Equal([(nint)7], native.Removed);
        Assert.True(exclusion.ExcludeEveryNewWindow().IsSuccess); // a stopped watcher can be started again
    }

    [Fact]
    public void ACreatedWindowIsTriedQuietlyAndARefusalIsLoggedOnlyWhenItIsShown()
    {
        var native = new FakeNative { ReadBack = 0, SetError = WindowsCaptureExclusion.ErrorAccessDenied };
        var log = new RecordingLog();
        var exclusion = new WindowsCaptureExclusion(native, CurrentBuild, log);
        Assert.True(exclusion.ExcludeEveryNewWindow().IsSuccess);

        native.Callback!(7, WindowsCaptureExclusion.EventObjectCreate, 42, WindowsCaptureExclusion.ObjIdWindow, 0, 1, 0);
        Assert.Empty(log.Events);

        native.Callback!(7, WindowsCaptureExclusion.EventObjectShow, 42, WindowsCaptureExclusion.ObjIdWindow, 0, 1, 0);
        Assert.Equal(ErrorCode.AccessDenied, Assert.Single(log.Events).Error);
    }

    [Fact]
    public void ChildWindowsAccessibleObjectsAndAlreadyExcludedWindowsAreLeftAlone()
    {
        var native = new FakeNative { ReadBack = 0 };
        var exclusion = new WindowsCaptureExclusion(native, CurrentBuild);
        Assert.True(exclusion.ExcludeEveryNewWindow().IsSuccess);
        var show = WindowsCaptureExclusion.EventObjectShow;

        native.Callback!(7, show, 42, -4, 0, 1, 0); // OBJID_CLIENT, not the window
        native.Callback!(7, show, 42, WindowsCaptureExclusion.ObjIdWindow, 3, 1, 0); // a child element
        native.Callback!(7, show, 0, WindowsCaptureExclusion.ObjIdWindow, 0, 1, 0);
        native.Root = 99;
        native.Callback!(7, show, 42, WindowsCaptureExclusion.ObjIdWindow, 0, 1, 0); // a child window
        native.Root = -1;
        native.ReadBack = WindowsCaptureExclusion.WdaExcludeFromCapture;
        native.Callback!(7, show, 42, WindowsCaptureExclusion.ObjIdWindow, 0, 1, 0); // already excluded

        Assert.Empty(native.Applied);
    }

    [Fact]
    public void ASecondWatcherIsInvalidInput()
    {
        var exclusion = new WindowsCaptureExclusion(new FakeNative(), CurrentBuild);
        Assert.True(exclusion.ExcludeEveryNewWindow().IsSuccess);

        Assert.Equal(ErrorCode.InvalidInput, exclusion.ExcludeEveryNewWindow().Error!.Code);
    }

    [Fact]
    public void TheWatcherIsUnsupportedBeforeVersion2004AndAFailedHookIsAnIoFailure()
    {
        var old = new FakeNative();
        Assert.Equal(ErrorCode.Unsupported,
            new WindowsCaptureExclusion(old, WindowsCaptureExclusion.FirstSupportedBuild - 1).ExcludeEveryNewWindow().Error!.Code);
        Assert.Equal(0, old.Calls);

        var log = new RecordingLog();
        Assert.Equal(ErrorCode.IoFailure,
            new WindowsCaptureExclusion(new FakeNative { Hook = 0 }, CurrentBuild, log).ExcludeEveryNewWindow().Error!.Code);
        Assert.Equal("capture.watch.failed", Assert.Single(log.Events).EventId);
    }
}
