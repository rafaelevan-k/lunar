using System.Text.Json.Nodes;
using Lunar.Infrastructure.Settings;
using Xunit;

namespace Lunar.Infrastructure.Tests.Settings;

public sealed class SettingsMigrationTests
{
    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void ProductionChainStartsAtVersionOneWithoutSteps()
    {
        var migration = new SettingsMigration();

        Assert.Equal(1, migration.Latest);
        Assert.Equal(MigrationOutcome.Current, migration.Migrate(Parse("""{ "schemaVersion": 1 }""")));
    }

    [Theory]
    [InlineData("""{ }""")]
    [InlineData("""{ "schemaVersion": "1" }""")]
    [InlineData("""{ "schemaVersion": 0 }""")]
    [InlineData("""{ "schemaVersion": -3 }""")]
    [InlineData("""{ "schemaVersion": 1.5 }""")]
    [InlineData("""{ "schemaVersion": null }""")]
    [InlineData("""{ "schemaVersion": true }""")]
    [InlineData("""{ "schemaVersion": [1] }""")]
    public void MissingOrInvalidVersionIsInvalid(string json) =>
        Assert.Equal(MigrationOutcome.Invalid, new SettingsMigration().Migrate(Parse(json)));

    [Fact]
    public void NewerVersionIsReportedAndNotTouched()
    {
        var root = Parse("""{ "schemaVersion": 2, "future": 7 }""");

        var outcome = new SettingsMigration().Migrate(root);

        Assert.Equal(MigrationOutcome.Newer, outcome);
        Assert.Equal(2, (int)root["schemaVersion"]!);
        Assert.Equal(7, (int)root["future"]!);
    }

    [Fact]
    public void StepsRunInOrderAndEachRaisesTheVersion()
    {
        var order = new List<int>();
        var migration = new SettingsMigration(3, new Dictionary<int, Action<JsonObject>>
        {
            [1] = root => { order.Add(1); root["a"] = 1; },
            [2] = root => { order.Add(2); root["b"] = (int)root["a"]! + 1; }
        });
        var file = Parse("""{ "schemaVersion": 1 }""");

        var outcome = migration.Migrate(file);

        Assert.Equal(MigrationOutcome.Migrated, outcome);
        Assert.Equal([1, 2], order);
        Assert.Equal(3, (int)file["schemaVersion"]!);
        Assert.Equal(2, (int)file["b"]!);
    }

    [Fact]
    public void MigrationStartsFromTheFilesVersion()
    {
        var order = new List<int>();
        var migration = new SettingsMigration(3, new Dictionary<int, Action<JsonObject>>
        {
            [1] = _ => order.Add(1),
            [2] = _ => order.Add(2)
        });

        migration.Migrate(Parse("""{ "schemaVersion": 2 }"""));

        Assert.Equal([2], order);
    }

    [Fact]
    public void MigratingTwiceChangesNothingTheSecondTime()
    {
        var calls = 0;
        var migration = new SettingsMigration(2, new Dictionary<int, Action<JsonObject>> { [1] = _ => calls++ });
        var file = Parse("""{ "schemaVersion": 1 }""");

        var first = migration.Migrate(file);
        var second = migration.Migrate(file);

        Assert.Equal(MigrationOutcome.Migrated, first);
        Assert.Equal(MigrationOutcome.Current, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void MissingStepMakesTheFileInvalid()
    {
        var migration = new SettingsMigration(3, new Dictionary<int, Action<JsonObject>> { [2] = _ => { } });

        Assert.Equal(MigrationOutcome.Invalid, migration.Migrate(Parse("""{ "schemaVersion": 1 }""")));
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(FormatException))]
    [InlineData(typeof(KeyNotFoundException))]
    [InlineData(typeof(InvalidCastException))]
    [InlineData(typeof(ArgumentException))]
    public void StepThatCannotReadTheShapeMakesTheFileInvalid(Type exceptionType)
    {
        var migration = new SettingsMigration(2, new Dictionary<int, Action<JsonObject>>
        {
            [1] = _ => throw (Exception)Activator.CreateInstance(exceptionType)!
        });

        Assert.Equal(MigrationOutcome.Invalid, migration.Migrate(Parse("""{ "schemaVersion": 1 }""")));
    }

    [Fact]
    public void BadConstructionArgumentsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SettingsMigration(0, new Dictionary<int, Action<JsonObject>>()));
        Assert.Throws<ArgumentNullException>(() => new SettingsMigration(1, null!));
        Assert.Throws<ArgumentNullException>(() => new SettingsMigration().Migrate(null!));
    }
}
