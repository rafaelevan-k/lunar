using System.Text;
using System.Text.Json.Nodes;
using Lunar.Application.Settings.Models;
using Lunar.Infrastructure.Settings;
using Xunit;

namespace Lunar.Infrastructure.Tests.Settings;

// BE-28: the settings file holds only choices, never a secret. The one stored secret (the pasted usage token) lives in its own encrypted
// file and has no field here; this test fails if a field that looks like a secret is ever added to the settings.
public sealed class SettingsSecretsTests
{
    private static IEnumerable<string> Names(JsonNode? node, string path = "")
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, value) in obj)
                {
                    var full = path.Length == 0 ? name : path + "." + name;
                    yield return full;
                    foreach (var inner in Names(value, full))
                        yield return inner;
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    foreach (var inner in Names(item, path))
                        yield return inner;
                }

                break;
        }
    }

    private static LunarSettings Full() => LunarSettings.Defaults with
    {
        LaunchAtStartup = true,
        Locale = "id-ID",
        Island = new IslandSettings { PreferredMonitorId = "\\\\.\\DISPLAY2", TopOffsetDip = 12 },
        Media = new MediaSettings { PreferredSessionId = "app.example" },
        Conversion = new ConversionSettings { OutputDirectory = "C:\\Results" },
        Privacy = new PrivacySettings { DiagnosticsEnabled = true },
        Usage = new UsageSettings { Enabled = true }
    };

    [Fact]
    public void TheSettingsFileHasExactlyTheKnownChoiceFieldsAndNothingThatNamesASecret()
    {
        var json = JsonNode.Parse(Encoding.UTF8.GetString(SettingsSerializer.Write(Full(), schemaVersion: 1)))!;

        var names = Names(json).ToList();

        Assert.DoesNotContain(names, n => n.Contains("token", StringComparison.OrdinalIgnoreCase)
            || n.Contains("password", StringComparison.OrdinalIgnoreCase)
            || n.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || n.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || n.Contains("apikey", StringComparison.OrdinalIgnoreCase)
            || n.Contains("ssid", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("usage.enabled", names);
        // A new field is a reviewed decision: it must be added here, with a reason it is not private.
        Assert.Equal(
            [
                "appearance", "appearance.autoCollapse", "appearance.eyeAnimationEnabled", "appearance.reduceMotion", "appearance.theme",
                "conversion", "conversion.outputDirectory",
                "island", "island.preferredMonitorId", "island.topOffsetDip",
                "launchAtStartup", "locale",
                "media", "media.preferredSessionId",
                "privacy", "privacy.diagnosticsEnabled",
                "schemaVersion",
                "usage", "usage.enabled"
            ],
            names.Order(StringComparer.Ordinal).ToList());
    }
}
