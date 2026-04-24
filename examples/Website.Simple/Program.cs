using Relatude.DB.Demo.Models;
using Relatude.DB.Native.Models;
using Relatude.DB.NodeServer;
using System.Text;
using WebApplication1;

var builder = WebApplication.CreateBuilder(args);
var noLang = Guid.Parse("add9c8e7-8c8e-4c8e-8c8e-4c8e8c8e8c8e");
builder.AddRelatudeDB(
    options => {
        options.OnStoreInit = (db) => {
            db.RegisterRunner(new DemoTaskRunner(db));
        };
        options.OnStoreOpen = (db) => {
            db.EnsureCultures([new SystemCulture(noLang, "en")]);
            var qx = db.Datastore.DefaultQueryContext;
            qx = qx.Culture(noLang);
            db.Datastore.SetDefaultQueryContext(qx);
        };
    });

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
app.MapGet("/Get", (RelatudeDBContext ctx) => {
    var db = ctx.Database;
    var sb = new StringBuilder();
    var arts = db.Query<DemoArticle>().Execute();
    foreach (var art in arts) {
        var revs = db.GetRevisions<DemoArticle>(art.Id);
        foreach (var rev in revs) {
            sb.AppendLine(rev.Meta.CultureId.ToString());
        }
    }
    return sb.ToString();
});
app.MapGet("/Add", (RelatudeDBContext ctx) => {
    var db = ctx.Database;
    var art = db.CreateAndInsert<DemoArticle>(a => {
        a.Content = "TEst example";
    });
    db.EnableRevisions(art.Id);
    var revs = db.GetRevisions<DemoArticle>(art.Id);
    var sb = new StringBuilder();
    foreach (var rev in revs) {
        sb.AppendLine(rev.Meta.CultureId.ToString());
    }
    return sb.ToString();
});


app.UseRelatudeDB();

app.Run();
