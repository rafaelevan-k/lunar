using Lunar.Desktop.Composition;
using Xunit;

namespace Lunar.Desktop.UITests.Composition;

// ADR-0018: the installer's command line, and nothing else, selects the uninstall cleanup; the user is asked only when the uninstall is not silent.
public sealed class UninstallModeTests
{
    [Theory]
    [InlineData()]
    [InlineData("--something")]
    [InlineData("--ui-level=5", "--uninstall-cleanup")] // the switch must come first
    public void OtherCommandLinesStartLunarNormally(params string[] args) =>
        Assert.Null(UninstallMode.From(args));

    [Theory]
    [InlineData("--ui-level=5", true)]  // full UI
    [InlineData("--ui-level=3", true)]  // basic UI (Settings > Apps)
    [InlineData("--ui-level=2", false)] // silent (msiexec /qn)
    [InlineData("--ui-level=34", false)] // silent with the "hide cancel" flag
    [InlineData("--ui-level=37", true)]  // full with a flag
    [InlineData("--ui-level=", false)]
    [InlineData("--ui-level=abc", false)]
    [InlineData("--ui-level=-5", false)]
    public void AskingDependsOnTheInstallerUiLevel(string level, bool mayAsk) =>
        Assert.Equal(mayAsk, UninstallMode.From(["--uninstall-cleanup", level])!.MayAsk);

    [Fact]
    public void WithoutAUiLevelNobodyIsAsked() =>
        Assert.False(UninstallMode.From(["--UNINSTALL-CLEANUP"])!.MayAsk);
}
