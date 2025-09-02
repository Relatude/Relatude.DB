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

app.MapGet("/", () => {
    if (!RelatudeDBServer.DefaultStoreIsOpenOrOpening()) return "Closed.";
    var store = RelatudeDBServer.Default;
    var noObjects = store.Query<object>().Count();
    return "Open. Total objects: " + noObjects.ToString("N0");
});

app.MapGet("/test", () => {
    var db = RelatudeDBServer.Default;
    var articles = db.Query<DemoArticle>().Execute();
    //var first10 = articles.Take(1).ToArray();
    //foreach (var article in first10) {
    //    foreach (var a in articles.Skip(1)) {
    //        db.SetRelation(article, a => a.Children, a);
    //    }
    //}
    var result = db.Query<DemoArticle>().Take(1).Execute().ToArray();
    return result;
   //var json = System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions {
   //     WriteIndented = true,
   //     DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
   //     Converters = {
   //         new Relatude.DB.Nodes.RelationJsonConverter()
   //     }
   // });
   // return Results.Text(json, "application/json"); 
   
});

app.UseRelatudeDB();

app.Run();
