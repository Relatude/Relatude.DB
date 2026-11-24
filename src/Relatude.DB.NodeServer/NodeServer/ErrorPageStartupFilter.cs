namespace Relatude.DB.NodeServer;

/// <summary>
/// ASP.NET's error page in front of the whole application while <paramref name="show"/> says so; AddRelatudeDB adds it
/// for <see cref="RelatudeDBServerSettings.ShowErrorsInBrowser"/>.
/// </summary>
public sealed class ErrorPageStartupFilter(Func<bool> show) : IStartupFilter {
    // a startup filter, so the page also covers what the application adds before UseRelatudeDB
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app => {
        app.UseWhen(_ => show(), branch => branch.UseDeveloperExceptionPage());
        next(app);
    };

    internal static bool FromSettings() => RelatudeDBRuntime.IsInitialized && RelatudeDBRuntime.Server.Settings.ShowErrorsInBrowser;
}
