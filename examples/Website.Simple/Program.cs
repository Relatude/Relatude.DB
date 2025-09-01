using Relatude.DB.NodeServer;

var builder = WebApplication.CreateBuilder(args);


//#if DEBUG
builder.Services.AddCors(options => {
    options.AddPolicy(name: "IgnoreCORSLocally", builder => {
        builder.WithOrigins("http://localhost:1234")
               .AllowAnyHeader()
               .AllowAnyMethod()
               .AllowCredentials();
    });
});
//#endif



var app = builder.Build();

app.MapGet("/", () => {
    if (!RelatudeDBServer.DefaultStoreIsOpenOrOpening()) return "Closed.";
    var store = RelatudeDBServer.DefaultStore;
    var noObjects = store.Query<object>().Count();
    return "Open. Total objects: " + noObjects.ToString("N0");
});

app.UseRelatudeDB("/relatude.db");

//#if DEBUG
app.UseCors("IgnoreCORSLocally");
//#endif


app.Run();
