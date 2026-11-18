using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.System;
using Lunar.Application.Abstractions.Translate;
using Lunar.Application.Settings;
using Lunar.Application.Shell.Models;
using Lunar.Application.Translate;
using Lunar.Desktop.Features.Translate;
using Lunar.Desktop.Shell;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// FE-41 (ADR-0028): the overlay, the trigger with consent and notices, the Settings card and the "Translating…" pill, over the real
// ScreenTranslationService with fakes for the screen, OCR, the service, the key and the shortcut. A real screen, real OCR on it and a real Azure
// request are the owner's manual check (the request needs the owner's own key).
public sealed class ScreenTranslationUiTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void TranslatingComesRightAfterARecording()
    {
        Assert.Equal(IslandActivity.Recording, IslandActivitySelector.Choose(true, true, true, true));
        Assert.Equal(IslandActivity.Translating, IslandActivitySelector.Choose(false, true, true, true));
        Assert.Equal(IslandActivity.FocusCountdown, IslandActivitySelector.Choose(false, false, true, true));
    }

    [Fact]
    public void TheOverlayDrawsEachTranslationOverItsOriginalAndFitsTheText() => RunOnSta(() =>
    {
        var picture = Picture();
        var blocks = new[]
        {
            new TranslatedBlock(new Lunar.Application.Abstractions.Translate.TextBox(20, 20, 300, 24), "Save your changes", "Simpan perubahan Anda", new Rgb(250, 250, 250), new Rgb(20, 20, 20), 24, 1, false),
            new TranslatedBlock(new Lunar.Application.Abstractions.Translate.TextBox(20, 80, 60, 12), "OK", "Sebuah terjemahan yang sangat panjang sekali untuk kotak kecil ini", new Rgb(10, 20, 80), new Rgb(240, 240, 240), 12, 1, false),
            new TranslatedBlock(new Lunar.Application.Abstractions.Translate.TextBox(20, 140, 200, 24), "Simpan", null, new Rgb(250, 250, 250), new Rgb(20, 20, 20), 24, 1, false),
            new TranslatedBlock(new Lunar.Application.Abstractions.Translate.TextBox(20, 200, 300, 30), "Close the window", "ウィンドウを閉じる", new Rgb(250, 250, 250), new Rgb(20, 20, 20), 30, 1, false)
        };
        var targets = new List<TranslationLanguage>();
        var overlay = new TranslationOverlayWindow(new ScreenTranslationOutcome(picture, blocks, TranslationLanguage.Indonesian, null, null), targets.Add) { ShowActivated = false };
        overlay.Show();
        try
        {
            Settle();
            Assert.Equal(3, overlay.Blocks.Count); // the block already in Indonesian stays uncovered

            var first = overlay.Blocks[0];
            Assert.Equal(18, Canvas.GetLeft(first));
            Assert.Equal(19, Canvas.GetTop(first));
            Assert.Equal(Color.FromRgb(250, 250, 250), ((SolidColorBrush)first.Background).Color);
            var firstText = (TextBlock)first.Child;
            Assert.Equal("Simpan perubahan Anda", firstText.Text);
            Assert.Null(firstText.ToolTip); // it fits

            var tight = (TextBlock)overlay.Blocks[1].Child;
            var scale = VisualTreeHelper.GetDpi(overlay).DpiScaleY;
            Assert.Equal(TranslationOverlayWindow.MinimumFontDip * scale, tight.FontSize, 3);
            Assert.Equal(TextTrimming.CharacterEllipsis, tight.TextTrimming);
            Assert.Equal(tight.Text, tight.ToolTip); // the whole text on hover

            var japanese = (TextBlock)overlay.Blocks[2].Child;
            Assert.Contains("Yu Gothic UI", japanese.FontFamily.Source, StringComparison.Ordinal);
            Assert.Equal("ウィンドウを閉じる", System.Windows.Automation.AutomationProperties.GetName(japanese));

            // Picking another language asks for it once; showing the result updates the radio without asking again.
            var english = FindRadio(overlay, "English");
            english.IsChecked = true;
            Assert.Equal([TranslationLanguage.English], targets);
            overlay.Show(new ScreenTranslationOutcome(picture, blocks, TranslationLanguage.Japanese, null, null));
            Assert.Equal([TranslationLanguage.English], targets);

            overlay.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(overlay)!, 0, System.Windows.Input.Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
            Settle();
            Assert.False(overlay.IsVisible); // Escape closes
            Assert.Empty(overlay.Blocks);
        }
        finally
        {
            overlay.Close();
        }
    });

    [Fact]
    public void TheTriggerAsksOnceForConsentShowsTheOverlayAndTheSameTriggerClosesIt() => RunOnSta(() =>
    {
        using var rig = new Rig();
        var asked = 0;
        rig.Consent = _ =>
        {
            asked++;
            return true;
        };

        Run(rig.Coordinator.TriggerAsync());
        Assert.Equal(1, asked);
        Assert.True(rig.Settings.Current.Translate.Consented);
        Assert.True(rig.Coordinator.IsOverlayOpen);
        Assert.Equal("Halo", ((TextBlock)rig.Coordinator.Overlay!.Blocks[0].Child).Text);
        var picture = rig.Capture.Last!;

        Run(rig.Coordinator.TriggerAsync()); // the shortcut again closes it
        Settle();
        Assert.False(rig.Coordinator.IsOverlayOpen);
        Assert.True(picture.IsCleared);

        Run(rig.Coordinator.TriggerAsync());
        Assert.Equal(1, asked); // only once
        rig.Coordinator.Overlay!.Close();
    });

    [Fact]
    public void RefusedConsentSendsNothingAndAMissingKeyPointsToSettings() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.Consent = _ => false;
        Run(rig.Coordinator.TriggerAsync());
        Assert.False(rig.Coordinator.IsOverlayOpen);
        Assert.Equal(0, rig.Translator.Calls);
        Assert.Equal(0, rig.Capture.Calls);

        rig.Keys.Key = null;
        Run(rig.Coordinator.TriggerAsync());
        var (message, settings) = Assert.Single(rig.Notices);
        Assert.Contains("key", message, StringComparison.OrdinalIgnoreCase);
        Assert.True(settings);
    });

    [Fact]
    public void ServiceProblemsBecomeANoticeInWords() => RunOnSta(() =>
    {
        using var rig = new Rig(consented: true);
        rig.Translator.Problem = TranslationProblem.FreeLimitReached;
        Run(rig.Coordinator.TriggerAsync());
        var (message, settings) = Assert.Single(rig.Notices);
        Assert.Contains("free", message, StringComparison.OrdinalIgnoreCase);
        Assert.False(settings);
        Assert.False(rig.Coordinator.IsOverlayOpen);
    });

    [Fact]
    public void TheShortcutFollowsTheSettingsAndATakenOneIsSaid() => RunOnSta(() =>
    {
        using var rig = new Rig(consented: true);
        Run(rig.Coordinator.StartAsync());
        Assert.Equal("Ctrl+Alt+T", rig.Shortcut.Current!.ToString());
        Assert.False(rig.Card.ShortcutProblem);

        rig.Card.ShortcutText = "Ctrl+Alt+Y";
        rig.Shortcut.Taken = true;
        rig.Card.ApplyShortcutCommand.Execute(null);
        Pump(() => rig.Card.ShortcutProblem);
        Assert.Contains("Another app", rig.Card.ShortcutMessage, StringComparison.Ordinal);

        rig.Shortcut.Taken = false;
        Run(rig.Settings.UpdateAsync(s => s with { Translate = s.Translate with { Enabled = false } }, CancellationToken.None));
        Pump(() => rig.Shortcut.Current is null);

        rig.Shortcut.Press();
        Pump(() => rig.Notices.Count > 0);
        Assert.Single(rig.Notices); // off: the press is told it is off, nothing is captured
        Assert.Equal(0, rig.Capture.Calls);
    });

    [Fact]
    public void TheSettingsCardSavesTestsAndRemovesTheKeyWithoutShowingIt() => RunOnSta(() =>
    {
        using var rig = new Rig(consented: true);
        rig.Keys.Key = null;
        rig.Card.SaveKeyCommand.Execute("short");
        Pump(() => rig.Card.KeyMessage is not null);
        Assert.False(rig.Card.HasKey);

        var box = new PasswordBox { Password = Key };
        rig.Card.SaveKeyCommand.Execute(box);
        Pump(() => rig.Card.HasKey);
        Assert.Equal(string.Empty, box.Password); // the box forgets the key at once
        Assert.DoesNotContain(Key, rig.Card.KeyStatus + rig.Card.KeyMessage, StringComparison.Ordinal);

        rig.Card.TestKeyCommand.Execute(null);
        Pump(() => rig.Translator.Calls == 1 && rig.Card.KeyMessage is { } m && !m.Contains('…'));
        Assert.Equal(["Hello"], rig.Translator.LastTexts);

        rig.Card.RegionText = "bad region";
        rig.Card.ApplyRegionCommand.Execute(null);
        Pump(() => rig.Card.KeyMessage!.Contains("one word", StringComparison.Ordinal));

        rig.Card.RemoveKeyCommand.Execute(null);
        Pump(() => !rig.Card.HasKey);

        rig.Card.Targets[3].Command.Execute(null);
        Pump(() => rig.Settings.Current.Translate.Target == TranslationLanguage.Japanese);
        Assert.True(rig.Card.Targets[3].IsSelected);
    });

    [Fact]
    public void ThePillSaysTranslatingAndCancelStopsIt() => RunOnSta(() =>
    {
        using var rig = new Rig(consented: true);
        var gate = new TaskCompletionSource();
        rig.Translator.Wait = gate.Task;
        var running = rig.Coordinator.TriggerAsync();
        Pump(() => rig.Coordinator.ShowsInIsland);

        var pill = new TranslatingPillView(rig.Coordinator.CancelCommand);
        Assert.Equal("Cancel", System.Windows.Automation.AutomationProperties.GetName(pill.CancelButton) is { Length: > 0 } ? "Cancel" : string.Empty);
        rig.Coordinator.CancelCommand.Execute(null);
        gate.SetResult();
        Run(running);
        Pump(() => !rig.Coordinator.ShowsInIsland);
        Assert.False(rig.Coordinator.IsOverlayOpen);
        Assert.Empty(rig.Notices); // a cancel is not a problem
    });

    // ---- rig ---------------------------------------------------------------------------------------------------------

    private sealed class Rig : IDisposable
    {
        public Rig(bool consented = false)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            Settings = new SettingsService(new RecordsPageTests.Repository());
            if (consented)
                Run(Settings.UpdateAsync(s => s with { Translate = s.Translate with { Consented = true } }, CancellationToken.None));
            Service = new ScreenTranslationService(Capture, Reader, Translator, Keys, Settings);
            Card = new TranslationSettingsViewModel(Settings, Keys, Translator, Shortcut, work => work());
            var ui = Dispatcher.CurrentDispatcher;
            Coordinator = new TranslateCoordinator(Service, Shortcut, Settings, Card, work => ui.BeginInvoke(work), () => { }, () => "en",
                (message, settings) => Notices.Add((message, settings)), askConsent: w => Consent(w));
        }

        public Func<Window, bool?> Consent { get; set; } = _ => true;

        public List<(string Message, bool Settings)> Notices { get; } = [];

        public SettingsService Settings { get; }

        public FakeCapture Capture { get; } = new();

        public FakeReader Reader { get; } = new();

        public FakeTranslator Translator { get; } = new();

        public FakeKeys Keys { get; } = new();

        public FakeShortcut Shortcut { get; } = new();

        public ScreenTranslationService Service { get; }

        public TranslationSettingsViewModel Card { get; }

        public TranslateCoordinator Coordinator { get; }

        public void Dispose()
        {
            Coordinator.Dispose();
            Card.Dispose();
            Service.Dispose();
            Settings.Dispose();
        }
    }

    internal sealed class FakeCapture : IScreenCapture
    {
        public int Calls { get; private set; }

        public ScreenPicture? Last { get; private set; }

        public Task<Result<ScreenPicture>> CaptureActiveDisplayAsync(CancellationToken ct)
        {
            Calls++;
            Last = Picture();
            return Task.FromResult(Result.Success(Last));
        }
    }

    internal sealed class FakeReader : IScreenTextReader
    {
        public IReadOnlyList<OcrLanguage> AvailableLanguages => [OcrLanguage.English];

        public Task<Result<IReadOnlyList<OcrLine>>> ReadAsync(ScreenPicture picture, OcrLanguage language, CancellationToken ct) =>
            Task.FromResult(Result.Success<IReadOnlyList<OcrLine>>([new OcrLine("Hello", [new OcrWord("Hello", new Lunar.Application.Abstractions.Translate.TextBox(20, 20, 80, 24))])]));
    }

    internal sealed class FakeTranslator : ITextTranslator
    {
        public int Calls { get; private set; }

        public TranslationProblem? Problem { get; set; }

        public Task? Wait { get; set; }

        public IReadOnlyList<string>? LastTexts { get; private set; }

        public async Task<Result<TranslationReply>> TranslateAsync(IReadOnlyList<string> texts, TranslationLanguage target, TranslatorCredential credential, CancellationToken ct)
        {
            Calls++;
            LastTexts = texts;
            if (Wait is not null)
                await Wait;
            if (ct.IsCancellationRequested)
                return Result.Cancelled<TranslationReply>();
            return Result.Success(Problem is { } p ? TranslationReply.Failed(p) : TranslationReply.Of([.. texts.Select(_ => new TranslatedText("Halo", "en"))]));
        }
    }

    internal sealed class FakeKeys : ITranslationKeyStore
    {
        public string? Key { get; set; } = ScreenTranslationUiTests.Key;

        public bool HasKey => Key is not null;

        public Task<Result> SaveAsync(string key, CancellationToken ct)
        {
            var valid = TranslationKeyRules.ValidateKey(key);
            if (valid.IsSuccess)
                Key = valid.Value;
            return Task.FromResult(valid.IsSuccess ? Result.Success() : Result.Failure(valid.Error!));
        }

        public Task<Result<string>> ReadAsync(CancellationToken ct) =>
            Task.FromResult(Key is null ? Result.Failure<string>(new OperationError(ErrorCode.AccessDenied, "No key.")) : Result.Success(Key));

        public Task<Result> RemoveAsync(CancellationToken ct)
        {
            Key = null;
            return Task.FromResult(Result.Success());
        }
    }

    internal sealed class FakeShortcut : IGlobalShortcut
    {
        public Shortcut? Current { get; private set; }

        public bool Taken { get; set; }

        public event Action? Pressed;

        public Task<Result> RegisterAsync(Shortcut shortcut, CancellationToken ct)
        {
            if (Taken)
                return Task.FromResult(Result.Failure(new OperationError(ErrorCode.AccessDenied, "Taken.")));
            Current = shortcut;
            return Task.FromResult(Result.Success());
        }

        public void Unregister() => Current = null;

        public void Press() => Pressed?.Invoke();

        public void Dispose() => Unregister();
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------

    /// <summary>A 400×300 white picture far off screen, so the overlay window never covers the desktop during tests.</summary>
    private static ScreenPicture Picture()
    {
        var pixels = new byte[400 * 300 * 4];
        Array.Fill(pixels, (byte)250);
        return new ScreenPicture(new PixelRect(-20000, -20000, 400, 300), pixels);
    }

    private static RadioButton FindRadio(DependencyObject root, string content)
    {
        var queue = new Queue<DependencyObject>([root]);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node is RadioButton radio && Equals(radio.Content, content))
                return radio;
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
                queue.Enqueue(child);
        }

        throw new InvalidOperationException("No radio " + content);
    }

    private static void Settle() => Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));

    private static void Run(Task task)
    {
        var end = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < end)
        {
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            Thread.Sleep(2);
        }

        Assert.True(task.IsCompleted, "The task did not finish in time.");
        task.GetAwaiter().GetResult();
        Settle();
    }

    private static void Pump(Func<bool> done)
    {
        var end = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < end)
        {
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            Thread.Sleep(2);
        }

        Assert.True(done(), "The expected state was not reached in time.");
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                StaDispatcher.ShutDownCurrent();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new InvalidOperationException("WPF screen translation check failed.", failure);
    }
}
