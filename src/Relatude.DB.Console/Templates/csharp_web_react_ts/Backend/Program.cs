using __NAMESPACE__.Middleware;
using Relatude.DB.FileConversion;
using Relatude.DB.NodeServer;

var builder = WebApplication.CreateBuilder(args);

// Relatude.DB is configured by relatude.db.json in this folder. Model classes live in the
// __NAMESPACE__.Models namespace (see Models/README.md) and become node types when the app starts.
builder.AddRelatudeDB(options => {
    options.FileConverters.Add(new SkiaImageConverter());   // image resizing and cropping
    options.FileConverters.Add(new FFMpegVideoConverter()); // video previews
});

var app = builder.Build();

app.UseRelatudeDB();                       // opens the database; admin UI and its API on /relatude.db
app.UseMiddleware<RelatudeDBMiddleware>(); // serves files stored in FileValue properties by their URL
app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();                      // the built client in wwwroot (see Client/vite.config.ts)

// API endpoints. The client calls them same-origin under /api. Inject RelatudeDBContext to reach the
// database: ctx.Database.Query<T>(), Get<T>(id), Insert(node), Update(node), Delete(id).
app.MapGet("/api/hello", (RelatudeDBContext ctx) => new {
    Message = "Hello from __NAME__!",
    Utc = DateTime.UtcNow,
    NodesInDatabase = ctx.Database.Count(),
});

// Client side routes resolve to the SPA in production; /api paths still return 404 when unknown.
app.MapFallbackToFile("{*path:regex(^(?!api/).*$)}", "index.html");

app.Run();
