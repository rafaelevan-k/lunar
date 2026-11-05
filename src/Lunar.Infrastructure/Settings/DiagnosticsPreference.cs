using System.Text.Json;

namespace Lunar.Infrastructure.Settings;

/// <summary>
/// The saved diagnostics opt-in, read once when the logger is built (ADR-0016). The logger exists before the settings are
/// loaded, so this looks at the settings file directly: one bounded read, no repair, no write, and any problem (no file, too
/// large, unreadable, not the expected shape) means "off". The settings repository stays the only writer and the only
/// reader that repairs.
/// </summary>
public static class DiagnosticsPreference
{
    public static bool IsEnabled(string? directory = null)
    {
        try
        {
            var path = Path.Combine(directory ?? JsonSettingsRepository.DefaultDirectory, JsonSettingsRepository.FileName);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is 0 or > JsonSettingsRepository.MaxFileBytes || info.LinkTarget is not null)
                return false;

            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 16 });
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("privacy", out var privacy)
                && privacy.ValueKind == JsonValueKind.Object
                && privacy.TryGetProperty("diagnosticsEnabled", out var enabled)
                && enabled.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
