using Relatude.DB.Demo.Models;
using Relatude.DB.NodeServer;

var builder = WebApplication.CreateBuilder(args);

builder.AddRelatudeDB();

// FOR VS CODE DEVELOPMENT ONLY - NEVER ALLOW ALL CORS:
builder.Services.AddCors(options => {
    options.AddPolicy(name: "AllowALL", builder => {
        builder.AllowAnyHeader().AllowAnyMethod().AllowCredentials().SetIsOriginAllowed(origin => true);
    });
});

var app = builder.Build();

app.UseCors("AllowALL"); // FOR VS CODE DEVELOPMENT ONLY - NEVER ALLOW ALL CORS

app.MapGet("/", (RelatudeDBContext ctx) => {
    var count = ctx.Database.Count(); //.Query<DemoArticle>().Count();
    var html = "<html><body>"
    + $@"<h1>Welcome to Relatude.DB</h1><p>Database has {count} objects.</p>"
    + $@"<p><a href='{ctx.Server.ApiUrlRoot}'>Admin UI</a></p>"
    + "</body></html>";
    return Results.Content(html, "text/html; charset=utf-8");
});

app.MapGet("/test", async (RelatudeDBContext ctx) => {
    var db = ctx.Database;
    var files = Directory.GetFiles("C:\\Users\\ogulb\\OneDrive\\Demo\\Pictures");
    foreach (var file in files) {
        var a = db.CreateAndInsert<DemoArticle>(a=> {
            a.Title = Path.GetFileNameWithoutExtension(file);
            a.Content = "Lorem ipsum dolor sit amet, consectetur adipiscing elit. Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua.";
        });
        await db.FileUploadAsync(a, a => a.File, file);
    }
    return "Uploaded: " + files.Length + " files.";
});


app.UseRelatudeDB();

app.Run();
