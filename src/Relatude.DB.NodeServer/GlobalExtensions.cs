using Relatude.DB.NodeServer;
public static class GlobalExtensions {
    public static IEndpointRouteBuilder UseRelatudeDB(this WebApplication app, string? urlPath = "/relatude.db", 
        string? dataFolderPath = null, string? tempFolderPath = null, ISettingsLoader? settingsIO = null) {
        return RelatudeDBServer.UseWAFDB(app, urlPath, dataFolderPath, tempFolderPath, settingsIO);
    }  
    public static Task<IEndpointRouteBuilder> UseRelatudeDBAsync(this WebApplication app, string? urlPath = "/relatude.db",
        string? dataFolderPath = null, string? tempFolderPath = null, ISettingsLoader? settingsIO = null) {
        return RelatudeDBServer.UseWAFDBAsync(app, urlPath, dataFolderPath, tempFolderPath, settingsIO);
    }
}
