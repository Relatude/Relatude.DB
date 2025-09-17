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

app.MapGet("/", (RelatudeDBContext ctx) => {
    var noObjects = ctx.Database.Query<object>().Count();
    return "Open. Total objects: " + noObjects.ToString("N0");
});

app.MapGet("/Test", (string query, RelatudeDBContext ctx) => {
    return ctx.Database.AI.GetCompletionAsync(query);
});


app.UseRelatudeDB();

app.Run();
