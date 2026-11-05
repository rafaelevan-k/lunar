using System.Text;
using Lunar.Application.Settings.Models;
using Lunar.Infrastructure.Settings;
using Xunit;

namespace Lunar.Infrastructure.Tests.Settings;

// Real files in a temporary directory; nothing touches the user's %LocalAppData%\Lunar.
public sealed class DiagnosticsPreferenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Lunar.Tests." + Guid.NewGuid().ToString("N"));

    public DiagnosticsPreferenceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private string MainPath => Path.Combine(_directory, JsonSettingsRepository.FileName);

    private void Write(string content) => File.WriteAllText(MainPath, content, new UTF8Encoding(false));

    [Fact]
    public void NoFileMeansOff() => Assert.False(DiagnosticsPreference.IsEnabled(_directory));

    [Fact]
    public void AMissingDirectoryMeansOff() => Assert.False(DiagnosticsPreference.IsEnabled(Path.Combine(_directory, "nowhere")));

    [Fact]
    public void TheSavedOptInIsRead()
    {
        Write("""{ "schemaVersion": 1, "privacy": { "diagnosticsEnabled": true } }""");

        Assert.True(DiagnosticsPreference.IsEnabled(_directory));
    }

    [Fact]
    public void ASavedOffAndAMissingValueAreOff()
    {
        Write("""{ "schemaVersion": 1, "privacy": { "diagnosticsEnabled": false } }""");
        Assert.False(DiagnosticsPreference.IsEnabled(_directory));

        Write("""{ "schemaVersion": 1 }""");
        Assert.False(DiagnosticsPreference.IsEnabled(_directory));
    }

    [Theory]
    [InlineData("""{ "privacy": { "diagnosticsEnabled": "true" } }""")]
    [InlineData("""{ "privacy": { "diagnosticsEnabled": 1 } }""")]
    [InlineData("""{ "privacy": true }""")]
    [InlineData("""[ true ]""")]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{")]
    public void AnythingButTheExactShapeIsOff(string content)
    {
        Write(content);

        Assert.False(DiagnosticsPreference.IsEnabled(_directory));
    }

    [Fact]
    public void ATooLargeFileIsNotReadAndMeansOff()
    {
        var padding = new string(' ', JsonSettingsRepository.MaxFileBytes + 10);
        Write("""{ "privacy": { "diagnosticsEnabled": true } }""" + padding);

        Assert.False(DiagnosticsPreference.IsEnabled(_directory));
    }

    [Fact]
    public void ADeeplyNestedFileIsOffAndDoesNotThrow()
    {
        Write(new string('[', 200) + new string(']', 200));

        Assert.False(DiagnosticsPreference.IsEnabled(_directory));
    }

    [Fact]
    public async Task WhatTheRepositoryWritesIsWhatThisReads()
    {
        using var repository = new JsonSettingsRepository(_directory);
        var on = LunarSettings.Defaults with { Privacy = new PrivacySettings { DiagnosticsEnabled = true } };

        Assert.True((await repository.SaveAsync(on, TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True(DiagnosticsPreference.IsEnabled(_directory));

        Assert.True((await repository.SaveAsync(LunarSettings.Defaults, TestContext.Current.CancellationToken)).IsSuccess);
        Assert.False(DiagnosticsPreference.IsEnabled(_directory));
    }

    [Fact]
    public void ItNeverWritesOrRepairsAnything()
    {
        Write("not json");

        Assert.False(DiagnosticsPreference.IsEnabled(_directory));

        Assert.Equal("not json", File.ReadAllText(MainPath));
        Assert.Single(Directory.GetFileSystemEntries(_directory));
    }
}
