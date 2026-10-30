namespace RGBcontroller.Core;

/// <summary>
/// File system locations used by RGBcontroller. The Service owns <see cref="DataRoot"/>;
/// user-session processes (Agent, UI) log to <see cref="UserLogDirectory"/> because they cannot
/// write files owned by the Service.
/// </summary>
public sealed record AppPaths(string DataRoot)
{
    public const string ProductFolderName = "RGBcontroller";

    public static AppPaths Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProductFolderName));

    public static string UserLogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolderName, "logs");

    public string ConfigDirectory => Path.Combine(DataRoot, "config");

    public string ConfigFile => Path.Combine(ConfigDirectory, "config.json");

    public string LogDirectory => Path.Combine(DataRoot, "logs");

    /// <summary>Isolated OpenRGB config folder (no plugins, separate from the user's own OpenRGB).</summary>
    public string OpenRgbConfigDirectory => Path.Combine(DataRoot, "openrgb");
}
