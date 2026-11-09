using System.Text.Json.Nodes;

namespace RGBcontroller.Core.Config;

/// <summary>
/// Schema 2 to 3: the shipped group "Board" is called "Motherboard", because "Board" did not say what it lights.
/// Renames the group and every reference to it (profile effects, the default group).
/// </summary>
internal sealed class BoardGroupRenameMigration : IConfigMigration
{
    private const string OldName = "Board";

    public int FromVersion => 2;

    public void Apply(JsonObject root)
    {
        if (root["lighting"] is not JsonObject lighting)
        {
            return;
        }

        Rename(lighting, "defaultGroup");
        if (lighting["groups"] is JsonArray groups)
        {
            foreach (var group in groups.OfType<JsonObject>())
            {
                Rename(group, "name");
            }
        }

        if (lighting["profiles"] is JsonArray profiles)
        {
            foreach (var profile in profiles.OfType<JsonObject>())
            {
                if (profile["groups"] is JsonArray effects)
                {
                    foreach (var effect in effects.OfType<JsonObject>())
                    {
                        Rename(effect, "group");
                    }
                }
            }
        }
    }

    private static void Rename(JsonObject owner, string property)
    {
        if (owner[property] is JsonValue value
            && value.TryGetValue<string>(out var text)
            && string.Equals(text, OldName, StringComparison.OrdinalIgnoreCase))
        {
            owner[property] = DefaultLighting.MotherboardGroup;
        }
    }
}
