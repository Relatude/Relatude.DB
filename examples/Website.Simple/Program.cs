using Relatude.DB.Demo.Models;
using Relatude.DB.Nodes;
using Relatude.DB.NodeServer;

var builder = WebApplication.CreateBuilder(args);
builder.AddRelatudeDB();

builder.Services.AddCors(options => {
    options.AddPolicy(name: "AllowALL", builder => {
        builder.AllowAnyHeader().AllowAnyMethod().AllowCredentials().SetIsOriginAllowed(origin => true);
    });
});

var app = builder.Build();

app.UseCors("AllowALL");

app.MapGet("/", (NodeStore store) => {
    var noObjects = store.Query<object>().Count();
    return "Open. Total objects: " + noObjects.ToString("N0");
});

app.UseRelatudeDB();

app.Run();
