using Lunar.Conversion.Providers;

namespace Lunar.Conversion.Tests.Support;

/// <summary>No LibreOffice on this PC, whatever the real PC has, so a test never depends on what is installed.</summary>
internal sealed class NoLibreOffice : ILibreOfficeLocator
{
    public LibreOfficeInfo? Locate() => null;
}
