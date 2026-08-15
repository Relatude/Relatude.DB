using Microsoft.Extensions.Configuration;
using Relatude.DB.NodeServer.Settings;

namespace Relatude.DB.Cli;

/// <summary>
/// The configuration sources an application host reads: appsettings.json and
/// appsettings.{Environment}.json from the content root, then environment variables. The RelatudeDB
/// section in them overrides relatude.db.json, the same way the server applies it. User secrets are
/// not read: they belong to the application, not to this tool.
/// </summary>
public static class AppConfig {
    public static IConfiguration Build(Target target) => new ConfigurationBuilder()
        .AddJsonFile(Path.Combine(target.Root, "appsettings.json"), optional: true)
        .AddJsonFile(Path.Combine(target.Root, "appsettings." + target.EnvironmentName + ".json"), optional: true)
        .AddEnvironmentVariables()
        .Build();

    public static void AddTo(ConfigurationManager configuration, Target target) {
        configuration.AddJsonFile(Path.Combine(target.Root, "appsettings.json"), optional: true);
        configuration.AddJsonFile(Path.Combine(target.Root, "appsettings." + target.EnvironmentName + ".json"), optional: true);
        configuration.AddEnvironmentVariables();
    }

    public static RelatudeDBServerSettings ApplyOverlay(RelatudeDBServerSettings settings, Target target) {
        var overlay = SettingsOverlay.Create(Build(target), SettingsOverlay.DefaultSectionName, Output.Detail, Output.Warn);
        return overlay == null ? settings : overlay.Apply(settings);
    }
}
