using WAF.NodeServer;
public static class GlobalExtensions {
    public static IEndpointRouteBuilder UseWAFDB(this WebApplication app, string? urlPath = "/waf", 
        string? dataFolderPath = null, string? tempFolderPath = null, ISettingsLoader? settingsIO = null) {
        return WAFServer.UseWAFDB(app, urlPath, dataFolderPath, tempFolderPath, settingsIO);
    }
    public static Task<IEndpointRouteBuilder> UseWAFDBAsync(this WebApplication app, string? urlPath = "/waf",
    string? dataFolderPath = null, string? tempFolderPath = null, ISettingsLoader? settingsIO = null) {
        return WAFServer.UseWAFDBAsync(app, urlPath, dataFolderPath, tempFolderPath, settingsIO);
    }
    public static IEndpointRouteBuilder UseRelatudeDB(this WebApplication app, string? urlPath = "/waf", 
        string? dataFolderPath = null, string? tempFolderPath = null, ISettingsLoader? settingsIO = null) {
        return WAFServer.UseWAFDB(app, urlPath, dataFolderPath, tempFolderPath, settingsIO);
    }  
    public static Task<IEndpointRouteBuilder> UseRelatudeDBAsync(this WebApplication app, string? urlPath = "/waf",
        string? dataFolderPath = null, string? tempFolderPath = null, ISettingsLoader? settingsIO = null) {
        return WAFServer.UseWAFDBAsync(app, urlPath, dataFolderPath, tempFolderPath, settingsIO);
    }

}
