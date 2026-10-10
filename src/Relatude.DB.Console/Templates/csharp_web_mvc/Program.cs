using __NAMESPACE__.Middleware;

var builder = WebApplication.CreateBuilder(args);

// Relatude.DB is configured by relatude.settings/relatude.db.json. Model classes live in the
// __NAMESPACE__.Models namespace (see Models/README.md) and become node types when the app starts.
builder.AddRelatudeDB();
builder.Services.AddControllersWithViews();

var app = builder.Build();

app.UseRelatudeDB();                       // opens the database; admin UI and its API on /relatude.db
app.UseMiddleware<RelatudeDBMiddleware>(); // serves files stored in FileValue properties by their URL,
                                           // ahead of the static files below: database content wins
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Home/Error");
app.UseHttpsRedirection();
app.UseStaticFiles();                      // wwwroot: css, images, scripts
app.UseRouting();                          // by hand, and after the static files above: UseRelatudeDB
                                           // registers endpoints, and WebApplication would otherwise
                                           // insert routing at the very start of the pipeline, where
                                           // a matched route stops UseStaticFiles serving at all

// Pages are controller actions rendering Razor views: /products lists, /products/details/<id> shows one.
// Controllers take RelatudeDBContext in their constructor; ctx.Database is the database.
app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
