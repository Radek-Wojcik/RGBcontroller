using System.Text.Json.Nodes;
using RGBcontroller.Core.Config;

namespace RGBcontroller.Tests.Config;

public sealed class ConfigMigratorTests
{
    [Fact]
    public void Migrate_RunsStepsInOrderAndStampsVersion()
    {
        var log = new List<int>();
        var migrator = new ConfigMigrator([new Step(2, log), new Step(1, log), new Step(3, log)], targetVersion: 4);
        var root = new JsonObject { ["schemaVersion"] = 1 };

        migrator.Migrate(root, fromVersion: 1);

        Assert.Equal([1, 2, 3], log);
        Assert.Equal(4, root["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public void Migrate_StartsFromGivenVersion()
    {
        var log = new List<int>();
        var migrator = new ConfigMigrator([new Step(1, log), new Step(2, log)], targetVersion: 3);

        migrator.Migrate(new JsonObject(), fromVersion: 2);

        Assert.Equal([2], log);
    }

    [Fact]
    public void Migrate_MissingStep_Throws()
    {
        var migrator = new ConfigMigrator([new Step(1, [])], targetVersion: 3);

        Assert.Throws<InvalidOperationException>(() => migrator.Migrate(new JsonObject(), fromVersion: 1));
    }

    [Fact]
    public void Constructor_DuplicateStep_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ConfigMigrator([new Step(1, []), new Step(1, [])], targetVersion: 2));
    }

    [Fact]
    public void Default_TargetsCurrentSchema()
    {
        Assert.Equal(AppConfig.CurrentSchemaVersion, ConfigMigrator.Default.TargetVersion);
    }

    [Fact]
    public void Default_RenamesMotherboardGroupAndItsReferences()
    {
        var root = JsonNode.Parse("""
            { "schemaVersion": 2, "lighting": { "defaultGroup": "Board",
              "groups": [ { "name": "Board" }, { "name": "RAM" } ],
              "profiles": [ { "name": "P", "groups": [ { "group": "Board" }, { "group": "RAM" } ] } ] } }
            """)!.AsObject();

        ConfigMigrator.Default.Migrate(root, fromVersion: 2);

        Assert.Equal(3, root["schemaVersion"]!.GetValue<int>());
        var lighting = root["lighting"]!;
        Assert.Equal("Motherboard", lighting["defaultGroup"]!.GetValue<string>());
        Assert.Equal("Motherboard", lighting["groups"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("RAM", lighting["groups"]![1]!["name"]!.GetValue<string>());
        Assert.Equal("Motherboard", lighting["profiles"]![0]!["groups"]![0]!["group"]!.GetValue<string>());
    }

    private sealed class Step(int from, List<int> log) : IConfigMigration
    {
        public int FromVersion => from;

        public void Apply(JsonObject root) => log.Add(from);
    }
}
