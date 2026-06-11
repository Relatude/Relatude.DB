using System.Text.Json;
using Relatude.DB.ContentApi.Api;
using Relatude.DB.ContentApi.Demo;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Nodes;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o => {
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

// FOR LOCAL DEVELOPMENT ONLY (vite dev server on another port) - NEVER ALLOW ALL CORS IN PRODUCTION:
builder.Services.AddCors(options => options.AddPolicy("AllowAll",
    policy => policy.AllowAnyHeader().AllowAnyMethod().SetIsOriginAllowed(_ => true)));

builder.Services.AddSingleton(_ => openStore(builder.Environment));

var app = builder.Build();

app.UseCors("AllowAll");

// Map store/validation errors to clean JSON problem responses.
app.Use(async (context, next) => {
    try {
        await next(context);
    } catch (BadHttpRequestException ex) {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    } catch (Exception ex) when (context.Request.Path.StartsWithSegments("/api")) {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapContentApi();
app.MapFallbackToFile("index.html"); // SPA client side routing

// Open the database and seed demo content before accepting requests.
Seeder.SeedIfEmpty(app.Services.GetRequiredService<NodeStore>());

app.Run();

static NodeStore openStore(IWebHostEnvironment env) {
    var dataModel = new Datamodel();
    dataModel.Add<Article>();
    dataModel.Add<Author>();
    dataModel.Add<Category>();
    dataModel.Add<Tag>();
    dataModel.Add<ArticleTags>();

    var dataFolder = Path.Combine(env.ContentRootPath, "data");
    Directory.CreateDirectory(dataFolder);

    var settings = new SettingsLocal {
        EnableTextIndexByDefault = true,
    };
    var dataStore = new DataStoreLocal(dataModel, settings, new IOProviderDisk(dataFolder));
    dataStore.Open();
    return new NodeStore(dataStore);
}
