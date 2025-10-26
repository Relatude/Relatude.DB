using Relatude.DB.Demo.Models;
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
    //var noObjects = ctx.Database.Query<DemoArticle>().Where(a => a.Content!=("dsadasd" +"ss") && a.CreatedAt < DateTime.UtcNow.AddDays(-100)).Execute().Count();
    var noObjects = ctx.Database.Query<DemoArticle>().Where(a => a.ArticleType == 0).Execute().Count();
    return "Open. Total objects: " + noObjects.ToString("N0");
});

app.UseRelatudeDB();

app.Run();
