using System.Text.Json.Nodes;

namespace RGBcontroller.Core.Config;

/// <summary>
/// Schema 1 to 2: the stage 1 <c>lighting</c> section (one static color plus two brightness values) is replaced by
/// layouts, groups, palettes and profiles. The old values have no meaningful mapping, so the section is dropped and
/// the shipped profiles apply.
/// </summary>
internal sealed class LightingV2Migration : IConfigMigration
{
    public int FromVersion => 1;

    public void Apply(JsonObject root) => root.Remove("lighting");
}
