using System.Xml.Linq;
using Xunit;

namespace Lunar.Architecture.Tests;

public sealed class ArchitectureRuleTests
{
    private static readonly string[] Layers =
        ["Domain", "Application", "Windows", "Conversion", "Infrastructure", "Desktop"];

    public static IEnumerable<object[]> ReferenceCases()
    {
        foreach (var source in Layers)
            foreach (var target in Layers)
            {
                var expected = source switch
                {
                    "Domain" => false,
                    "Application" => target == "Domain",
                    "Desktop" => target != "Desktop",
                    _ => target is "Domain" or "Application"
                };
                yield return ["Lunar." + source, "Lunar." + target, expected];
            }
    }

    [Theory]
    [MemberData(nameof(ReferenceCases))]
    public void GuardAcceptsOnlyDocumentedReferenceDirections(string source, string target, bool expected)
        => Assert.Equal(expected, LayerRules.AllowsProjectReference(source, target));

    [Theory]
    [InlineData("Lunar.Domain", "System.IO.File", false)]
    [InlineData("Lunar.Application", "System.Runtime.InteropServices.DllImportAttribute", false)]
    [InlineData("Lunar.Application", "System.Windows.Window", false)]
    [InlineData("Lunar.Application", "System.IO.File", false)]
    [InlineData("Lunar.Application", "System.IO.Stream", true)]
    [InlineData("Lunar.Conversion", "Windows.Data.Pdf.PdfDocument", false)]
    [InlineData("Lunar.Infrastructure", "System.Windows.Window", false)]
    [InlineData("Lunar.Windows", "System.Windows.Window", false)]
    [InlineData("Lunar.Domain", "System.DateTime", true)]
    [InlineData("Lunar.Conversion", "System.IO.Stream", true)]
    [InlineData("Lunar.Windows", "Windows.Data.Pdf.PdfDocument", true)]
    [InlineData("Lunar.Desktop", "System.Windows.Window", true)]
    public void GuardDistinguishesPlatformAndPortableTypes(string layer, string type, bool expected)
        => Assert.Equal(expected, LayerRules.AllowsType(layer, type));

    [Fact]
    public void SolutionContainsExactlyCanonicalProjectsAndNoSpikes()
    {
        var root = LayerBoundaryTests.RepositoryRoot;
        var solution = File.ReadAllLines(Path.Combine(root, "Lunar.sln"));
        var projects = solution.Where(line => line.StartsWith("Project(", StringComparison.Ordinal)
            && line.Contains(".csproj", StringComparison.Ordinal))
            .Select(line => line.Split('"')[5].Replace('\\', Path.DirectorySeparatorChar)).ToArray();
        string[] tests = ["Domain.Tests", "Application.Tests", "Windows.IntegrationTests",
            "Conversion.Tests", "Infrastructure.Tests", "Desktop.UITests"];
        var expected = Layers.Select(layer => Path.Combine("src", "Lunar." + layer, "Lunar." + layer + ".csproj"))
            .Concat(tests.Select(test => Path.Combine("tests", "Lunar." + test, "Lunar." + test + ".csproj")));
        Assert.Equal(expected.Order(StringComparer.Ordinal), projects.Order(StringComparer.Ordinal));
        Assert.All(projects, project => Assert.True(File.Exists(Path.Combine(root, project))));

        var domain = XDocument.Load(Path.Combine(root, "src/Lunar.Domain/Lunar.Domain.csproj"));
        Assert.Empty(domain.Descendants("ProjectReference"));
    }
}
