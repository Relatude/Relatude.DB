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
app.UseMiddleware<RelatudeDBMiddleware>(); // serves files stored in FileValue properties by their URL,
                                           // ahead of the static files below: database content wins
app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();                      // the built client in wwwroot (see Client/vite.config.ts)

// Routing goes here, by hand, after the static files above. UseRelatudeDB registers the admin
// endpoints, and unless the application calls UseRouting itself WebApplication inserts it at the very
// start of the pipeline instead. The catch-all MapFallbackToFile below would then match every
// non-/api request before UseStaticFiles is reached, and the static file middleware skips itself
// whenever an endpoint is already selected - serving index.html for the client's own js and css.
app.UseRouting();

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
