using System.Text.Json.Nodes;

namespace Lunar.Infrastructure.Settings;

internal enum MigrationOutcome
{
    /// <summary>The file is already at the latest version.</summary>
    Current,

    /// <summary>One or more steps were applied; the tree is now at the latest version.</summary>
    Migrated,

    /// <summary>The file was written by a newer Lunar and must not be rewritten.</summary>
    Newer,

    /// <summary>The version is missing, not an integer, below 1 or a step is missing or failed.</summary>
    Invalid
}

/// <summary>
/// Ordered chain of <c>vN → vN+1</c> steps over the parsed JSON tree (ADR-0011). Version 1 is the first schema, so
/// production has no step yet; the first breaking change adds <c>steps[1]</c> and raises <see cref="LatestVersion"/>.
/// Additive fields never need a step because a missing field means its default.
/// </summary>
internal sealed class SettingsMigration
{
    internal const int LatestVersion = 1;
    internal const string VersionProperty = "schemaVersion";

    private readonly int _latest;
    private readonly IReadOnlyDictionary<int, Action<JsonObject>> _steps;

    public SettingsMigration()
        : this(LatestVersion, new Dictionary<int, Action<JsonObject>>())
    {
    }

    internal SettingsMigration(int latestVersion, IReadOnlyDictionary<int, Action<JsonObject>> steps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(latestVersion, 1);
        ArgumentNullException.ThrowIfNull(steps);
        _latest = latestVersion;
        _steps = steps;
    }

    public int Latest => _latest;

    public MigrationOutcome Migrate(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (root[VersionProperty] is not JsonValue value || !value.TryGetValue<int>(out var version) || version < 1)
            return MigrationOutcome.Invalid;
        if (version > _latest)
            return MigrationOutcome.Newer;
        if (version == _latest)
            return MigrationOutcome.Current;

        while (version < _latest)
        {
            if (!_steps.TryGetValue(version, out var step))
                return MigrationOutcome.Invalid;

            try
            {
                step(root);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException
                or InvalidCastException or KeyNotFoundException)
            {
                // A step that cannot read this file's shape means the file is unusable, not that Lunar is broken.
                return MigrationOutcome.Invalid;
            }

            version++;
            root[VersionProperty] = version;
        }

        return MigrationOutcome.Migrated;
    }
}
