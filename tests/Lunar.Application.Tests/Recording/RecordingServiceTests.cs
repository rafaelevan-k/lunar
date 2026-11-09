using Lunar.Application.Abstractions.Audio;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Settings;
using Lunar.Application.Recording;
using Lunar.Application.Settings;
using Lunar.Application.Settings.Models;
using Lunar.Application.Tests.Fakes;
using Lunar.Domain.Recording;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Application.Tests.Recording;

public sealed class RecordingServiceTests : IDisposable
{
    private const string SecretPath = @"C:\Users\someone\Music\private\take.mp3";
    private const string SecretDevice = "Secret Headset Name";

    private readonly FakeEngine _engine = new();
    private readonly FakeCatalog _catalog = new();
    private readonly FakeFiles _files = new();
    private readonly FakeLog _log = new();
    private readonly ManualTime _time = new();
    private readonly SettingsService _settings = new(new FakeRepository());
    private readonly RecordingService _service;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public RecordingServiceTests()
    {
        _service = new RecordingService(_engine, _catalog, _files, _settings, new FakeClock(new DateTimeOffset(2026, 11, 9, 14, 30, 5, TimeSpan.Zero)), _log, _time);
    }

    public void Dispose()
    {
        _service.Dispose();
        _settings.Dispose();
    }

    [Fact]
    public async Task StartRecordsPauseAndResumeMoveThePhaseAndStopSaves()
    {
        var phases = new List<RecordingPhase>();
        using var subscription = _service.Changes.Subscribe(new Observer(s => phases.Add(s.Phase)));

        Assert.True((await _service.StartAsync(Ct)).IsSuccess);
        Assert.True((await _service.PauseAsync(Ct)).IsSuccess);
        Assert.True((await _service.ResumeAsync(Ct)).IsSuccess);
        Assert.True((await _service.StopAsync()).IsSuccess);

        Assert.Equal([RecordingPhase.Starting, RecordingPhase.Recording, RecordingPhase.Paused, RecordingPhase.Recording, RecordingPhase.Saving, RecordingPhase.Idle], phases);
        Assert.Equal("Recording 2026-11-09 14-30-05", _files.CommittedBase);
        Assert.Equal(_service.Folder, _files.ReservedFolder);
        Assert.Equal("Recording 2026-11-09 14-30-05.mp3", _service.Current.LastSaved!.FileName);
        Assert.True(_engine.Session!.Finished);
        Assert.True(_engine.Session.Disposed);
    }

    [Fact]
    public async Task DoublePressesFindTheWorkDone()
    {
        await _service.StartAsync(Ct);
        Assert.True((await _service.StartAsync(Ct)).IsSuccess);
        Assert.Equal(1, _engine.Starts);

        await _service.PauseAsync(Ct);
        Assert.True((await _service.PauseAsync(Ct)).IsSuccess);
        Assert.Equal(1, _engine.Session!.Pauses);

        await Task.WhenAll(_service.StopAsync(), _service.StopAsync(), _service.PauseAsync(Ct), _service.ResumeAsync(Ct));
        Assert.Equal(1, _files.Commits);
        Assert.Equal(RecordingPhase.Idle, _service.Current.Phase);

        Assert.True((await _service.StopAsync()).IsSuccess);
        Assert.True((await _service.PauseAsync(Ct)).IsSuccess);
        Assert.Equal(1, _files.Commits);
    }

    [Fact]
    public async Task ConcurrentStartsOpenOneMicrophone()
    {
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => _service.StartAsync(Ct)));

        Assert.Equal(1, _engine.Starts);
        Assert.Equal(RecordingPhase.Recording, _service.Current.Phase);
    }

    [Fact]
    public async Task TheTimerCountsTheEnginesRecordedTimeOnly()
    {
        await _service.StartAsync(Ct);
        _engine.Session!.Recorded = TimeSpan.FromSeconds(7);

        Assert.Equal(TimeSpan.FromSeconds(7), _service.Recorded);
        await _service.StopAsync();
        Assert.Equal(TimeSpan.Zero, _service.Recorded);
    }

    [Theory]
    [InlineData(ErrorCode.AccessDenied)]
    [InlineData(ErrorCode.DeviceUnavailable)]
    [InlineData(ErrorCode.DependencyMissing)]
    public async Task AStartThatFailsIsToldAndAbandonsThePartAndCanBeTriedAgain(ErrorCode code)
    {
        _engine.FailWith = new OperationError(code, "no");

        var result = await _service.StartAsync(Ct);

        Assert.Equal(code, result.Error!.Code);
        Assert.Equal(RecordingPhase.Failed, _service.Current.Phase);
        Assert.Equal(code, _service.Current.Error!.Code);
        Assert.Equal(1, _files.Abandons);

        _engine.FailWith = null;
        Assert.True((await _service.StartAsync(Ct)).IsSuccess);
        Assert.Equal(RecordingPhase.Recording, _service.Current.Phase);
        Assert.Null(_service.Current.Error);
    }

    [Fact]
    public async Task NotEnoughFreeSpaceRefusesBeforeTheMicrophoneOpens()
    {
        _files.FreeBytes = RecordingRules.MinFreeBytes - 1;

        var result = await _service.StartAsync(Ct);

        Assert.Equal(ErrorCode.IoFailure, result.Error!.Code);
        Assert.Equal(0, _engine.Starts);
    }

    [Fact]
    public async Task AChosenInputIsUsedAndOneThatIsGoneFallsBackToAutomaticWithANotice()
    {
        _catalog.Inputs = [new AudioInput("a", "A", true), new AudioInput("b", "B", false)];
        await _service.ChooseInputAsync("b", Ct);
        await _service.StartAsync(Ct);
        Assert.Equal("b", _engine.LastInputId);
        Assert.Equal(RecordingNotice.None, _service.Current.Notice);
        await _service.StopAsync();

        _catalog.Inputs = [new AudioInput("a", "A", true)];
        await _service.StartAsync(Ct);

        Assert.Null(_engine.LastInputId);
        Assert.Equal(RecordingNotice.ChosenInputMissing, _service.Current.Notice);
        Assert.Equal("b", _service.ChosenInputId); // kept: it may be plugged in again
    }

    [Fact]
    public async Task ALostDeviceSavesWhatWasCaptured()
    {
        await _service.StartAsync(Ct);

        _engine.Session!.RaiseFailed(new OperationError(ErrorCode.DeviceUnavailable, "gone"));
        await _service.ShutdownAsync();

        Assert.Equal(RecordingPhase.Idle, _service.Current.Phase);
        Assert.Equal(RecordingNotice.DeviceLost, _service.Current.Notice);
        Assert.NotNull(_service.Current.LastSaved);
        Assert.Equal(1, _files.Commits);
    }

    [Fact]
    public async Task AFileThatCouldNotBeFinishedStillGetsItsChanceToBeSaved()
    {
        await _service.StartAsync(Ct);
        _engine.Session!.FinishResult = Result.Failure(new OperationError(ErrorCode.Unexpected, "graph is dead"));

        await _service.StopAsync();

        Assert.Equal(1, _files.Commits);
        Assert.NotNull(_service.Current.LastSaved);
    }

    [Fact]
    public async Task ACommitThatFailsIsToldAndTheFileIsNotDiscarded()
    {
        await _service.StartAsync(Ct);
        _files.CommitError = new OperationError(ErrorCode.IoFailure, "disk");

        var result = await _service.StopAsync();

        Assert.Equal(ErrorCode.IoFailure, result.Error!.Code);
        Assert.Equal(RecordingPhase.Failed, _service.Current.Phase);
        Assert.Equal(0, _files.Abandons);
    }

    [Fact]
    public async Task ReachingTheLengthLimitSaves()
    {
        await _service.StartAsync(Ct);
        _engine.Session!.Recorded = RecordingRules.MaxDuration;

        _time.Fire();
        await _service.ShutdownAsync();

        Assert.Equal(RecordingNotice.LengthLimitReached, _service.Current.Notice);
        Assert.Equal(1, _files.Commits);
    }

    [Fact]
    public async Task ADiskRunningOutSaves()
    {
        await _service.StartAsync(Ct);
        _files.FreeBytes = RecordingRules.MinFreeBytes - 1;

        _time.Fire();
        await _service.ShutdownAsync();

        Assert.Equal(RecordingNotice.DiskNearlyFull, _service.Current.Notice);
        Assert.Equal(1, _files.Commits);
    }

    [Fact]
    public async Task ShutdownSavesARunningRecordingAndStopsTheLimitChecks()
    {
        await _service.StartAsync(Ct);

        await _service.ShutdownAsync();

        Assert.Equal(1, _files.Commits);
        Assert.True(_time.TimerDisposed);
    }

    [Fact]
    public async Task ARecordingLeftBehindIsReportedOnce()
    {
        _files.Recovered = new RecoveredRecording(SecretPath, RecoveryOutcome.Recovered);

        Assert.True((await _service.RecoverAsync(Ct)).IsSuccess);

        Assert.Equal(RecordingNotice.RecordingRecovered, _service.Current.Notice);
        Assert.Equal(RecoveryOutcome.Recovered, _service.Current.Recovered!.Outcome);
    }

    [Fact]
    public async Task LevelsAreForwardedAndGoFlatAtTheEnd()
    {
        var seen = new List<VoiceLevels>();
        _service.LevelsChanged += seen.Add;
        await _service.StartAsync(Ct);

        _engine.Session!.RaiseLevels(new VoiceLevels(Enumerable.Repeat(0.5f, VoiceLevelBands.Count).ToArray()));
        await _service.StopAsync();

        Assert.Equal(2, seen.Count);
        Assert.All(seen[1].Bands, b => Assert.Equal(0f, b));
    }

    [Fact]
    public async Task AFolderIsCheckedBeforeItIsSavedAndTheChoiceIsRemembered()
    {
        _files.FolderError = new OperationError(ErrorCode.InvalidInput, "no");
        Assert.Equal(ErrorCode.InvalidInput, (await _service.ChooseFolderAsync(@"\\server\share", Ct)).Error!.Code);
        Assert.Equal(_files.DefaultFolder, _service.Folder);

        _files.FolderError = null;
        Assert.True((await _service.ChooseFolderAsync(@"D:\Takes", Ct)).IsSuccess);
        Assert.Equal(@"D:\Takes", _service.Folder);
    }

    [Fact]
    public async Task NothingSensitiveReachesTheLog()
    {
        _catalog.Inputs = [new AudioInput(SecretDevice, SecretDevice, true)];
        await _service.ChooseInputAsync(SecretDevice, Ct);
        await _service.ChooseFolderAsync(@"C:\Users\someone\Music\private", Ct);
        _files.Recovered = new RecoveredRecording(SecretPath, RecoveryOutcome.KeptAsItIs);
        await _service.RecoverAsync(Ct);
        await _service.StartAsync(Ct);
        _engine.Session!.RaiseFailed(new OperationError(ErrorCode.DeviceUnavailable, SecretDevice));
        await _service.ShutdownAsync();

        Assert.NotEmpty(_log.Events);
        foreach (var text in _log.Events.Select(e => e.ToString()))
        {
            Assert.DoesNotContain("someone", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(SecretDevice, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class Observer(Action<RecordingSnapshot> next) : IObserver<RecordingSnapshot>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(RecordingSnapshot value) => next(value);
    }

    private sealed class FakeRepository : ISettingsRepository
    {
        public Task<Result<SettingsLoadResult>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success(new SettingsLoadResult(LunarSettings.Defaults, SettingsRecovery.None)));

        public Task<Result> SaveAsync(LunarSettings settings, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
    }

    private sealed class FakeLog : IDiagnosticLog
    {
        private readonly List<DiagnosticEvent> _events = [];

        public IReadOnlyList<DiagnosticEvent> Events
        {
            get
            {
                lock (_events)
                    return _events.ToArray();
            }
        }

        public void Write(DiagnosticEvent diagnosticEvent)
        {
            lock (_events)
                _events.Add(diagnosticEvent);
        }
    }

    private sealed class FakeCatalog : IAudioInputCatalog
    {
        public IReadOnlyList<AudioInput> Inputs { get; set; } = [];

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public Task<Result<IReadOnlyList<AudioInput>>> GetInputsAsync(CancellationToken ct) => Task.FromResult(Result.Success(Inputs));
    }

    private sealed class FakeEngine : IVoiceCaptureEngine
    {
        private int _starts;

        public int Starts => _starts;

        public string? LastInputId { get; private set; }

        public OperationError? FailWith { get; set; }

        public FakeSession? Session { get; private set; }

        public async Task<Result<IVoiceCaptureSession>> StartAsync(string? inputId, string partFilePath, CancellationToken ct)
        {
            await Task.Yield();
            Interlocked.Increment(ref _starts);
            LastInputId = inputId;
            if (FailWith is not null)
                return Result.Failure<IVoiceCaptureSession>(FailWith);
            Session = new FakeSession();
            return Result.Success<IVoiceCaptureSession>(Session);
        }
    }

    private sealed class FakeSession : IVoiceCaptureSession
    {
        public TimeSpan Recorded { get; set; }

        public int Pauses { get; private set; }

        public bool Finished { get; private set; }

        public bool Disposed { get; private set; }

        public Result FinishResult { get; set; } = Result.Success();

        public event Action<VoiceLevels>? LevelsChanged;

        public event Action<OperationError>? Failed;

        public void RaiseLevels(VoiceLevels levels) => LevelsChanged?.Invoke(levels);

        public void RaiseFailed(OperationError error) => Failed?.Invoke(error);

        public Task<Result> PauseAsync(CancellationToken ct)
        {
            Pauses++;
            return Task.FromResult(Result.Success());
        }

        public Task<Result> ResumeAsync(CancellationToken ct) => Task.FromResult(Result.Success());

        public Task<Result> FinishAsync(CancellationToken ct)
        {
            Finished = true;
            return Task.FromResult(FinishResult);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeFiles : IRecordingFiles
    {
        public string DefaultFolder => @"C:\Music\Lunar Recordings";

        public long FreeBytes { get; set; } = long.MaxValue;

        public OperationError? FolderError { get; set; }

        public OperationError? CommitError { get; set; }

        public RecoveredRecording? Recovered { get; set; }

        public string? ReservedFolder { get; private set; }

        public string? CommittedBase { get; private set; }

        public int Commits { get; private set; }

        public int Abandons { get; private set; }

        public Result ValidateFolder(string folder) => FolderError is null ? Result.Success() : Result.Failure(FolderError);

        public Result<long> GetFreeBytes(string folder) => Result.Success(FreeBytes);

        public Task<Result<string>> ReservePartAsync(string folder, string baseName, CancellationToken ct)
        {
            ReservedFolder = folder;
            return Task.FromResult(Result.Success(folder + @"\~part.lunar-part"));
        }

        public Task<Result<SavedRecording>> CommitAsync(string partPath, string baseName, CancellationToken ct)
        {
            Commits++;
            CommittedBase = baseName;
            return Task.FromResult(CommitError is null
                ? Result.Success(new SavedRecording(@"C:\Music\" + baseName + ".mp3", baseName + ".mp3"))
                : Result.Failure<SavedRecording>(CommitError));
        }

        public Task AbandonAsync(string partPath, CancellationToken ct)
        {
            Abandons++;
            return Task.CompletedTask;
        }

        public Task<Result<RecoveredRecording?>> RecoverAsync(CancellationToken ct) => Task.FromResult(Result.Success(Recovered));
    }

    private sealed class ManualTime : TimeProvider
    {
        private ManualTimer? _timer;

        public bool TimerDisposed => _timer?.IsDisposed == true;

        public void Fire() => _timer!.Fire();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _timer = new ManualTimer(callback, state);

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            public bool IsDisposed { get; private set; }

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() => IsDisposed = true;

            public ValueTask DisposeAsync()
            {
                IsDisposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }
}
