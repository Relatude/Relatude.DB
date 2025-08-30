using WAF.NodeServer;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => {
    if (!WAFServer.DefaultStoreIsOpenOrOpening()) return "Closed.";
    var store = WAFServer.DefaultStore;
    var noObjects = store.Query<object>().Count();
    return "Open. Total objects: " + noObjects.ToString("N0");
});

app.UseRelatudeDB("waf");

app.Run();
