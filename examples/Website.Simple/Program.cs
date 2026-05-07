using Relatude.DB.Demo.Models;
using Relatude.DB.NodeServer;
using Relatude.DB.Query;
using System.Text;

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
app.MapGet("/Insert", async (RelatudeDBContext ctx) => {
    var db = ctx.Database;
    

    var art = new DemoArticle();
    art.Title = "Ole";




    var paraGraph = new DemoParagraph();
    paraGraph.Code = "dasdas";
    art.Paragraphs.Add(paraGraph);

    

    db.Insert(art);

    art = db.Get<DemoArticle>(art.Id);

    var filePath = @"C:\Users\ogulb\OneDrive\Demo\Pictures\bar.png";
    await db.Datastore.FileUploadAsync(art.File.PropertyPath, filePath);




    //var id = db.FileUploadAsync();

    //art = db.Get<DemoArticle>(art.Id);


    //art.Id = Guid.NewGuid();
    //foreach(var p in art.Paragraphs) {
    //    foreach (var sub in p.SubParagraphs) {
    //        db.FileUploadAsync(sub.File, filePath);

    //        db.UpdateProperty(sub, p.SubParagraphs, s=>s.File);
    //    }
    //}

    

    //&db.Insert(art);


});
app.MapGet("/Search", (RelatudeDBContext ctx) => {
    var db = ctx.Database;
    var results = db.Query<DemoArticle>().WhereSearch("Ole").Execute().ToArray();
    return results;
});
app.MapGet("/List", (RelatudeDBContext ctx) => {
    var db = ctx.Database;
    var results = db.Query<DemoArticle>().Execute().ToArray();    
    return results;
});


app.UseRelatudeDB();

app.Run();
