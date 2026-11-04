using Lunar.Application.Conversion.Models;
using Lunar.Conversion.Validation;
using Xunit;

namespace Lunar.Conversion.Tests.Validation;

// BE-20: path safety is decided on the text of the path, the same on every host.
public sealed class PathRulesTests
{
    private const int Max = 1024;

    [Theory]
    [InlineData(@"C:\Users\ann\Pictures\photo.png")]
    [InlineData(@"c:/Users/ann/photo.png")]
    [InlineData(@"D:\a\b\c\d\e.pdf")]
    [InlineData(@"C:\foto ürün 写真.png")]
    [InlineData(@"C:\folder with spaces\my file (2).docx")]
    [InlineData(@"C:\a\.hidden.png")]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Users\ann\")]
    public void OrdinaryFullLocalPathsPass(string path)
    {
        Assert.Equal(PathProblem.None, PathRules.Check(path, Max));
    }

    [Theory]
    [InlineData(null, PathProblem.Empty)]
    [InlineData("", PathProblem.Empty)]
    [InlineData("   ", PathProblem.Empty)]
    [InlineData("photo.png", PathProblem.NotFullPath)]
    [InlineData(@"Pictures\photo.png", PathProblem.NotFullPath)]
    [InlineData(@"\Users\ann\photo.png", PathProblem.NotFullPath)]
    [InlineData(@"C:photo.png", PathProblem.NotFullPath)]
    [InlineData(@"..\photo.png", PathProblem.NotFullPath)]
    [InlineData(@"\\server\share\photo.png", PathProblem.Network)]
    [InlineData("//server/share/photo.png", PathProblem.Network)]
    [InlineData(@"\\?\C:\photo.png", PathProblem.DeviceNamespace)]
    [InlineData(@"\\.\PhysicalDrive0", PathProblem.DeviceNamespace)]
    [InlineData("//./COM1", PathProblem.DeviceNamespace)]
    [InlineData(@"C:\a\photo.png:secret", PathProblem.StreamName)]
    [InlineData(@"C:\a\photo.png:Zone.Identifier:$DATA", PathProblem.StreamName)]
    [InlineData(@"C:\a\*.png", PathProblem.Wildcard)]
    [InlineData(@"C:\a\photo?.png", PathProblem.Wildcard)]
    [InlineData(@"C:\a\pho<to>.png", PathProblem.Wildcard)]
    [InlineData("C:\\a\\pho\"to.png", PathProblem.Wildcard)]
    [InlineData(@"C:\a\pho|to.png", PathProblem.Wildcard)]
    [InlineData(@"C:\a\..\b\photo.png", PathProblem.Traversal)]
    [InlineData(@"C:\a\.\photo.png", PathProblem.Traversal)]
    [InlineData(@"C:\a\..", PathProblem.Traversal)]
    [InlineData(@"C:\CON", PathProblem.ReservedName)]
    [InlineData(@"C:\a\nul.png", PathProblem.ReservedName)]
    [InlineData(@"C:\a\COM1.txt", PathProblem.ReservedName)]
    [InlineData(@"C:\a\lpt9 .png", PathProblem.ReservedName)]
    [InlineData(@"C:\AUX\photo.png", PathProblem.ReservedName)]
    [InlineData(@"C:\a\photo.png.", PathProblem.BadName)]
    [InlineData(@"C:\a\photo.png ", PathProblem.BadName)]
    [InlineData(@"C:\a \photo.png", PathProblem.BadName)]
    [InlineData("C:\\a\\pho\u0001to.png", PathProblem.ControlCharacter)]
    [InlineData("C:\\a\\photo.png\0.exe", PathProblem.ControlCharacter)]
    [InlineData("C:\\a\\pho\nto.png", PathProblem.ControlCharacter)]
    public void AnythingWindowsTreatsSpeciallyIsRefused(string? path, PathProblem expected)
    {
        Assert.Equal(expected, PathRules.Check(path, Max));
    }

    [Fact]
    public void ANameLongerThanTwoHundredFiftyFiveCharactersIsRefused()
    {
        var path = @"C:\a\" + new string('x', 256) + ".png";

        Assert.Equal(PathProblem.NameTooLong, PathRules.Check(path, 4096));
        Assert.Equal(PathProblem.None, PathRules.Check(@"C:\a\" + new string('x', 251) + ".png", 4096));
    }

    [Fact]
    public void APathLongerThanTheLimitIsRefused()
    {
        var path = @"C:\" + string.Join('\\', Enumerable.Repeat(new string('d', 50), 25)) + @"\f.png";

        Assert.Equal(PathProblem.PathTooLong, PathRules.Check(path, 1024));
        Assert.Equal(PathProblem.None, PathRules.Check(path, 2048));
    }

    [Theory]
    [InlineData(@"C:\a\b.png", true)]
    [InlineData(@"C:\a\", false)]
    [InlineData("C:/a/", false)]
    public void APathEndingInASeparatorNamesAFolderNotAFile(string path, bool namesAFile)
    {
        Assert.Equal(namesAFile, PathRules.NamesAFile(path));
    }

    [Fact]
    public void EveryProblemMapsToAnActionableFailureWithoutAPath()
    {
        foreach (var problem in Enum.GetValues<PathProblem>().Where(p => p != PathProblem.None))
        {
            var failure = PathRules.ToFailure(problem);

            Assert.False(string.IsNullOrWhiteSpace(failure.Message));
            Assert.DoesNotContain(@":\", failure.Message, StringComparison.Ordinal);
            Assert.Equal(Lunar.Domain.Shared.ErrorCode.InvalidInput, failure.Code);
        }
    }

    [Theory]
    [InlineData(PathProblem.Network, RejectionReason.NetworkPath)]
    [InlineData(PathProblem.PathTooLong, RejectionReason.PathTooLong)]
    [InlineData(PathProblem.NameTooLong, RejectionReason.PathTooLong)]
    [InlineData(PathProblem.Traversal, RejectionReason.UnsafePath)]
    [InlineData(PathProblem.StreamName, RejectionReason.UnsafePath)]
    [InlineData(PathProblem.ReservedName, RejectionReason.UnsafePath)]
    [InlineData(PathProblem.NotFullPath, RejectionReason.UnsafePath)]
    public void ProblemsMapToTheRightReason(PathProblem problem, RejectionReason expected)
    {
        Assert.Equal(expected, PathRules.ToFailure(problem).Reason);
    }
}
