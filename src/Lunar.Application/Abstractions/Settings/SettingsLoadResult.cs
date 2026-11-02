using Lunar.Application.Settings.Models;

namespace Lunar.Application.Abstractions.Settings;

/// <summary>Usable settings plus how they were obtained.</summary>
public sealed record SettingsLoadResult(LunarSettings Settings, SettingsRecovery Recovery);
