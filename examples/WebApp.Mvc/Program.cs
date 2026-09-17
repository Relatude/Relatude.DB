using WebAppMvc.Middleware;
using Relatude.DB.FileConversion;

var builder = WebApplication.CreateBuilder(args);

// Relatude.DB is configured by relatude.db.json in this folder. Model classes live in the
// WebAppMvc.Models namespace (see Models/README.md) and become node types when the app starts.
builder.AddRelatudeDB(options => {
    options.FileConverters.Add(new SkiaImageConverter());   // image resizing and cropping
    options.FileConverters.Add(new FFMpegVideoConverter()); // video previews
});
builder.Services.AddControllersWithViews();

var app = builder.Build();

app.UseRelatudeDB();                       // opens the database; admin UI and its API on /relatude.db
app.UseMiddleware<RelatudeDBMiddleware>(); // serves files stored in FileValue properties by their URL
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Home/Error");
app.UseHttpsRedirection();
app.UseStaticFiles();                      // wwwroot: css, images, scripts

// Pages are controller actions rendering Razor views: /products lists, /products/details/<id> shows one.
// Controllers take RelatudeDBContext in their constructor; ctx.Database is the database.
app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
