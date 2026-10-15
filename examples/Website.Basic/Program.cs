using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Demo.Models;
using Relatude.DB.FileConversion;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.DB.NodeServer;
using Relatude.DB.Query;
using System.Text;
using Website.Simple;
using Website.Simple.Data;
using Website.Simple.Models;

var builder = WebApplication.CreateBuilder(args);
builder.AddRelatudeDB(options => {
    options.OnDatamodelInit = (dm, container) => {
        dm.Add<DemoArticle>();
        dm.Add<Color>();
        dm.Add<Product>();
        dm.Add<Brand>();
        dm.Add<SitePage>();
        dm.Add<PageTree>(); // relation classes are not pulled in by Add<T>, so the tree relation is added explicitly
    };
    options.OnStoreInit = db => {    };
    options.OnStoreOpenBackground = db => {
        ShopSeeder.SeedIfEmpty(db, 50000, 1000); // populates the facet search example (see wwwroot/search.html)
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
    + $@"<p><a href='/search.html'>Facet search example</a></p>"
    + $@"<p><a href='/list'>List images and videos</a></p>"
    + $@"<p><a href='/upload.html'>Upload images and videos</a></p>"
    + "</body></html>";
    return Results.Content(html, "text/html; charset=utf-8");
});

app.MapPost("/CancelConversion", async (RelatudeDBContext ctx, Guid conversionKey, bool permanently) => {
    var db = ctx.Database;
    await db.Datastore.CancelConversion(conversionKey, permanently);
    return Results.Ok();
});

app.MapGet("/List", (RelatudeDBContext ctx, HttpResponse res) => {
    var db = ctx.Database;
    var articles = db.Query<DemoArticle>().Execute().ToArray();
    var html = new StringBuilder();
    html.Append("<html><body style='background-color:#f0f000'>");
    foreach (var article in articles) {
        if (!article.File.IsEmpty) {
            if (article.File.FileType == FileType.Video) {
                var videoAdj = new FileAdjustmentVideo() {
                    Width = 240, Height = 200,
                    TargetBitRateInMbps = 10,
                    RequestedFormat = FileFormat.Mp4,
                };
                var thumbnailAdj = new FileAdjustmentImage() {
                    CropMode = ImageCropMode.Fill,
                    Width = 240,
                    Height = 200,
                    Saturation = -100,
                    RequestedFormat = FileFormat.Jpeg,
                    Sharpness = 0,
                    TimeOffsetMs = 4000,
                    Quality = 90
                };
                var thumbnailUrl = $"{db.Datastore.GetUrl(article.File.PropertyPath!, thumbnailAdj)}";
                var isThumbnailReady = db.Datastore.IsFileReady(article.File.PropertyPath!, thumbnailAdj, true, firstInQueue: true);
                var isVideoReady = db.Datastore.IsFileReady(article.File.PropertyPath!, videoAdj, true, firstInQueue: true);
                if (isVideoReady) {
                    var videoUrl = $"{db.Datastore.GetUrl(article.File.PropertyPath!, videoAdj)}";
                    html.Append($"<video autoplay muted loop width='{videoAdj.Width}' height='{videoAdj.Height}' controls >");
                    html.Append($"<source src='{videoUrl}' type='video/mp4'>");
                    html.Append($"Your browser does not support the video tag. Here is a <a href='{videoUrl}'>link to the video</a> instead.");
                    html.Append($"</video>");
                } else {
                    html.Append($"<img src='{thumbnailUrl}'>");
                }
            } else if (article.File.FileType == FileType.Image) {
                var imageAdj = new FileAdjustmentImage() {
                    CropMode = ImageCropMode.Fill,
                    Width = 240,
                    Height = 200,
                    Saturation = 0,
                    RequestedFormat = FileFormat.Gif,
                    Sharpness = 0,
                    Temporary = false,
                    Quality = 90
                };
                var imageUrl = $"{db.Datastore.GetUrl(article.File.PropertyPath!, imageAdj)}";
                html.Append($"<img src='{imageUrl}'>");
            }
        }
    }
    html.Append("</body></html>");
    res.Headers.ContentType = "text/html; charset=utf-8";
    return html.ToString();
});

app.MapPost("/StartUpload", async (RelatudeDBContext ctx, string fileName) => {
    var db = ctx.Database;
    var article = db.CreateAndInsert<DemoArticle>(a => { a.Title = "Uploaded file"; });
    var uploadId = await db.InitiateMultipartUploadAsync(article.File, fileName);
    return Results.Json(uploadId);
});
app.MapPost("/UploadPart", async (RelatudeDBContext ctx, Guid uploadId, HttpRequest req) => {
    var db = ctx.Database;

    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    var part = ms.ToArray();
    await db.AppendMultipartUploadAsync(uploadId, part, part.Length);
});
app.MapPost("/CompleteUpload", async (RelatudeDBContext server, Guid uploadId) => {
    var db = server.Database;
    await db.FinalizeMultipartUploadAsync(uploadId);
});

// Facet search example used by wwwroot/search.html. The facets are computed from the free text
// search result, so counts and range buckets adapt to the query as you type.
app.MapPost("/shop/search", (RelatudeDBContext ctx, ShopSearchRequest req) => {
    var iterations = 0;
    var swQuery = System.Diagnostics.Stopwatch.StartNew();
    var db = ctx.Database;
    const int pageSize = 10;
    var query = db.Query<Product>();

    if (!string.IsNullOrWhiteSpace(req.Query)) query = query.WhereSearch(req.Query);
    var facetQuery = query.Include(p => p.Colors!).Facets().SetFacetOptions(p => p.Tags, maxValues: 8, sortByCount: true)
        .Page(req.Page, pageSize);
    foreach (var sel in req.Selections ?? []) {
        foreach (var v in sel.Values ?? []) facetQuery = facetQuery.SetFacetValue(sel.Property, v);
        foreach (var r in sel.Ranges ?? []) facetQuery = facetQuery.SetFacetRangeValue(sel.Property, r.From, r.To);
    }
    var res = facetQuery.Execute();
    for (var i = 0; i < iterations; i++) {
        res = facetQuery.Execute();
    }
    swQuery.Stop();
    return Results.Json(new {
        total = res.TotalCount,
        sourceCount = res.SourceCount,
        page = req.Page,
        pageSize,
        durationMs = swQuery.Elapsed.TotalMilliseconds,
        items = res.Select(p => new {
            p.Name, p.Description, p.Category, p.Price, p.InStock, p.Tags,
            Brand = p.Brand.TryGet(out var b) ? b.Name : "",
            Colors = p.Colors?.Select(c => c.Name) ?? [],
            Sizes = p.Sizes.Select(s => s.ToString()),
        }),
        facets = res.Facets.Select(f => new {
            property = f.CodeName,
            displayName = f.DisplayName,
            isRange = f.IsRangeFacet == true,
            values = f.Values.Select(v => new {
                // relation facet buckets are the related nodes; their id is what a selection posts back
                value = FacetJson.Str(v.Value is Color c ? c.Id : v.Value),
                value2 = FacetJson.Str(v.Value2),
                display = v.DisplayName,
                count = v.Count,
                selected = v.Selected,
            }),
        }),
    });
});

app.UseDefaultFiles();
app.UseStaticFiles();


app.UseMiddleware<RelatudeDBMiddleware>();

app.UseRelatudeDB();

app.Run();

record ShopSearchRequest(string? Query, int Page, List<ShopFacetSelection>? Selections);
record ShopFacetSelection(string Property, List<string>? Values, List<ShopFacetRange>? Ranges);
record ShopFacetRange(string From, string To);
static class FacetJson {
    // facet values are sent as invariant strings so the client can post them back unchanged
    public static string? Str(object? v) => v switch {
        null => null,
        double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };
}

