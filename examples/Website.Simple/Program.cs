using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Demo.Models;
using Relatude.DB.FileConversion;
using Relatude.DB.GraphQL;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.DB.NodeServer;
using Relatude.DB.Query;
using Relatude.DB.Transactions;
using System.Text;
using Website.Simple;
using Website.Simple.Data;
using Website.Simple.Models;

var builder = WebApplication.CreateBuilder(args);
builder.AddRelatudeDB(options => {
    options.FileConverters.Add(new SkiaImageConverter());
    options.FileConverters.Add(new FFMpegVideoConverter());
    options.OnDatamodelInit = (dm, container) => {
        dm.Add<DemoArticle>();
        dm.Add<Color>();
        dm.Add<Product>();
        dm.Add<Brand>();
        dm.Add<SitePage>();
        dm.Add<PageTree>(); // relation classes are not pulled in by Add<T>, so the tree relation is added explicitly
    };
    //options.CreateUrlManager = settings => new PageUrlManager();
    options.OnStoreInit = db => {
        db.RegisterTransactionPlugin(new DemoArticlePlugin());
    };
    options.OnStoreOpenBackground = db => {
        Website.Simple.Data.ShopSeeder.SeedIfEmpty(db, 1000000, 1000); // populates the facet search example (see wwwroot/search.html)
        //Website.Simple.Data.PageSeeder.SeedIfEmpty(db); // populates the dynamic URL example (see the /pages* endpoints)
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
    + $@"<p><a href='/graphql.html'>GraphQL playground</a></p>"
    + $@"<p><a href='/testimonials'>Testimonials (model defined in Models/Json/testimonial.json)</a></p>"
    + $@"<p><a href='/campaigns'>Campaigns (model compiled at startup from Models/CSharp/Campaign.cs)</a></p>"
    + $@"<p><a href='/datamodel-sources'>Datamodel sources (where every type came from)</a></p>"
    + $@"<h2>Dynamic URL example (TreeUrlManager)</h2>"
    + $@"<p><a href='/pages/urls'>All pages with their computed URLs</a></p>"
    + $@"<p><a href='/tv/sony-x90/info'>A page: /tv/sony-x90/info (its ""info"" slug is shared with /mobile/pixel/info)</a></p>"
    + $@"<p><a href='/pages/rename-demo'>Rename the TV section (one write; links inside HTML keep working)</a></p>"
    + $@"<p><a href='/pages/resolve?url=https%3A%2F%2Fwww.site-two.local%2Fcontact-us'>Resolve a URL on the other domain</a></p>"
    + "</body></html>";
    return Results.Content(html, "text/html; charset=utf-8");
});

app.MapGet("/ChangeColor", (RelatudeDBContext ctx) => {
    var color = ctx.Database.First<Color>();
    if (color.Name.Contains("-")) {
        color.Name = color.Name.Split("-").First().Trim();
    } else {
        color.Name += " - " + DateTime.Now.ToString("HH:mm:ss");
    }
    ctx.Database.Update(color);
    return color.Name;
});


app.MapGet("/Insert", async (RelatudeDBContext ctx) => {






    var db = ctx.Database;

    var dbNorsk = db.Context.Culture("no-nb").Admin().Create();
    var dbEngelsk = db.Context.Culture("en-us").Create();



    //if (hasInserted) return "Already inserted.";
    //var files = Directory.GetFiles(@"C:\Users\ogulb\Pictures\", "*.mp4").ToArray();

    var files1 = Directory.GetFiles(@"C:\Users\ogulb\OneDrive\Demo\Videos", "*.*").ToArray();
    var files2 = Directory.GetFiles(@"C:\Users\ogulb\OneDrive\Demo\Pictures", "*.jpg").ToArray();
    var files = files1.Concat(files2).ToArray();
    //ar files = Directory.GetFiles(@"C:\Users\ogulb\OneDrive\Demo\Videos", "sample.mkv").ToArray();
    //var files = Directory.GetFiles(@"C:\Users\ogulb\OneDrive\Demo\Pictures", "nemo.jpg").ToArray();
    for (int i = 0; i < files.Length; i++) {
        var art = new DemoArticle();
        art.Title = Path.GetFileName(files[i]);
        db.Insert(art);
        art = db.Get(art);
        await db.FileUploadAsync(art.File, files[i]);
    }
    return "Uploaded " + files.Length + " files.";
});
app.MapGet("/Search", (RelatudeDBContext ctx) => {
    var db = ctx.Database;
    var results = db.Query<DemoArticle>().WhereSearch("Ole").Execute().ToArray();
    return results;
});
app.MapGet("/Streams", (RelatudeDBContext ctx) => {
    return IOProviderDisk.GetAllOpenStreams();
});
app.MapGet("/T1est", (Database db) => {
    return db.Query<DemoArticle>().WhereSearch("dddd").Select(a => new { a.Title, a.File.Name }).Execute().ToArray();
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
                var isThumbnailReady = db.Datastore.IsFileReady(article.File.PropertyPath!, thumbnailAdj, true);
                var isVideoReady = db.Datastore.IsFileReady(article.File.PropertyPath!, videoAdj, true);
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
                    Width = 440,
                    Height = 400,
                    Saturation = 0,
                    RequestedFormat = FileFormat.Jpeg,
                    Sharpness = 0,
                    Temporary = false,
                    Quality = 90
                };
                //var imageUrl = $"{db.GetUrl(article.File)}";
                var imageUrl = $"{db.Datastore.GetUrl(article.File.PropertyPath!, imageAdj)}";
                html.Append($"<img src='{imageUrl}'>");
            }
            //var metaUrl = $"{db.Datastore.GetUrl(article.File.PropertyPath!, new FileAdjustmentMeta())}";
            //html.Append($"<p><a href='{metaUrl}'>Conversion status and metadata</a></p>");
            //html.Append($"<p>{article.File.Width}x{article.File.Height}</p>");
        }
    }
    html.Append("</body></html>");
    res.Headers.ContentType = "text/html; charset=utf-8";
    return html.ToString();
});

app.MapGet("/getstatus", (RelatudeDBContext ctx, HttpResponse res) => {
    var db = ctx.Database;
    var running = db.Datastore.GetConversions();
    return Results.Json(running);
});

app.MapPost("/cancel", async (RelatudeDBContext ctx, Guid id, bool permanently) => {
    var db = ctx.Database;
    await db.Datastore.CancelConversion(id, permanently);
});

app.MapGet("test", async (RelatudeDBContext ctx) => {
    var db = ctx.Database;

    var art2 = db.CreateAndInsert<IDemoArticle>(a => { a.Title = "Uploaded file"; });

    db.Upsert(art2);



    return art2;

    ////var article1 = new DemoArticle() { Title = "Test" };
    ////var article2 = new DemoArticle() { Title = "Test2" };
    ////db.Insert(article1);
    ////db.Insert(article2);

    //var article1 = db.CreateAndInsert<IDemoArticle>(a => { a.Title = "Test1"; });
    //var article2 = db.CreateAndInsert<DemoArticle>(b => { b.Title = "Test2"; });

    //article1.Site.Set(article2.Id);

    //db.Update(article1);

    //var ab = db.Query(article1).Preload(a => a.Site).Single();//.ToString();

    //return ab;

});
app.MapGet("t", (RelatudeDBContext ctx) => {
    var db = ctx.Database;
    var sb = new StringBuilder();
    var articles = db.Query<DemoArticle>().Execute();
    var iterations = 0;
    for (int i = 0; i < iterations; i++) {
        foreach (var article in articles) {
            if (article.File.IsEmpty) continue;
            db.GetUrl(article);
            db.GetUrl(article.File);
            db.GetUrl(article.File, new FileAdjustmentImage() { FocusX = 100 });
        }
    }
    foreach (var article in articles) {
        if (article.File.IsEmpty) continue;
        sb.AppendLine(db.GetUrl(article));
        sb.AppendLine(db.GetUrl(article.File));
        sb.AppendLine(db.GetUrl(article.File, new FileAdjustmentImage() { FocusX = 100 }));
    }
    return sb.ToString();
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

app.MapGet("/query", (RelatudeDBContext ctx) => {
    var db = ctx.Database;
    var query = "DemoArticle";
    return db.EvaluateForJsonAsync(query, []);
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
    var facetQuery = query.Include(p => p.Colors!).Facets() // Include so the page of products lists its colors
        //                                                    .AddValueFacet(p => p.Category)
        //                                                    .AddValueFacet(p => p.Brand)
        //                                                    .AddValueFacet(p => p.Colors!) // relation facet: buckets are the related Color nodes
        //                                                    .AddValueFacet(p => p.Sizes) // enum array facet: buckets carry the int values, displayed with the enum names
        //                                                    .AddRangeFacet(p => p.Price) // no bounds given: buckets are generated from the values in the current result set
        //.SetFacetOptions(p => p.Price, rangeCount: 3) // sort by value for ranges
        //                                              .AddValueFacet(p => p.InStock)
        //                                              .AddValueFacet(p => p.Tags)
        //                                              .SetFacetOptions(p => p.Tags, maxValues: 8, sortByCount: true)
        .Page(req.Page, pageSize);
    // Queries are immutable: every Set... returns a new query, so the result must be kept.
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

// File based datamodel sources (see the DatamodelSources section in relatude.db.json):
// Testimonial's model is defined in Models/Json/testimonial.json; the class in Models/Testimonial.cs
// is a plain POCO without attributes, and the typed API works as usual:
app.MapGet("/testimonials", (RelatudeDBContext ctx) => {
    var db = ctx.Database;
    if (db.Query<Testimonial>().Count() == 0) {
        db.Insert(new Testimonial() { Author = "Ada", Quote = "The graph model made our product data simple again.", Rating = 5 });
        db.Insert(new Testimonial() { Author = "Linus", Quote = "Fast facet search out of the box.", Rating = 4 });
    }
    return Results.Json(db.Query<Testimonial>().Execute());
});

// Campaign is compiled at startup from Models/CSharp/Campaign.cs (the file is not part of the
// project build), so the type only exists at runtime and is reached by name:
app.MapGet("/campaigns", (RelatudeDBContext ctx) => {
    var db = ctx.Database;


    dynamic campaign = db.Create("Campaign");
    campaign.Name = "Summer sale";
    campaign.Pitch = "Everything must go.";
    campaign.DiscountPercent = 25.0;
    campaign.ValidTo = DateTime.UtcNow.AddDays(30);
    db.Insert(campaign);




    return Results.Json(db.QueryType("Campaign").Execute());
});

// ---------------------------------------------------------------------------------------------
// Dynamic URL example. The page tree itself is served by RelatudeDBMiddleware (a request like
// /tv/sony-x90/info resolves through the TreeUrlManager configured above); the endpoints below
// show what the url manager does underneath.

// every page with its computed URL - nothing here is stored, it all derives from slugs + tree:
app.MapGet("/pages/urls", (RelatudeDBContext ctx) => {
    var db = ctx.Database;
    return Results.Json(db.Query<SitePage>().Execute().Select(p => new {
        p.Title,
        Segment = p.Slug,
        Url = db.GetUrl(p),
        AbsoluteUrl = db.GetUrl(p, absolute: true),
    }));
});

// renaming a section is ONE write: every descendant URL changes on the next read, and the id
// based rdb: tokens stored inside HTML bodies keep working - nothing is reparsed or rewritten:
app.MapGet("/pages/rename-demo", (RelatudeDBContext ctx) => {
    var db = ctx.Database;
    var tv = db.Get<SitePage>(PageSeeder.TvSectionId);
    var newSlug = tv.Slug == "tv" ? "television" : "tv";
    db.UpdateAddress(new NodeKey(tv.Id), newSlug);

    var bodyPropId = db.Datastore.Datamodel.NodeTypesByFullName[typeof(SitePage).FullName!]
        .AllProperties.Values.First(p => p.CodeName == nameof(SitePage.Body)).Id;
    db.Datastore.TryGet(PageSeeder.SiteOneRootId, out var rawRoot);
    rawRoot!.TryGetValue(bodyPropId, out var storedBody);

    return Results.Json(new {
        renamedTvSectionTo = newSlug,
        sonyInfoUrlIsNow = db.GetUrl(db.Get<SitePage>(PageSeeder.SonyInfoId)),
        rootBodyAsServed = db.Get<SitePage>(PageSeeder.SiteOneRootId).Body, // public URLs, already pointing at the new path
        rootBodyAsStored = storedBody, // rdb: id tokens - untouched by the rename
    });
});

// domain routing: the same path resolves to different nodes per host, decided by the url manager:
app.MapGet("/pages/resolve", (RelatudeDBContext ctx, string url) => {
    var db = ctx.Database;
    if (!db.TryParseUrl(url, out var keys)) return Results.NotFound(new { url, match = false });
    var page = db.Get<SitePage>(keys.NodeKey);
    return Results.Json(new { url, match = true, page.Title, CanonicalUrl = db.GetUrl(page, absolute: true) });
});

// address validation, as an editor UI would call it before saving:
app.MapGet("/pages/check-address", (RelatudeDBContext ctx, Guid pageId, string slug) => {
    var db = ctx.Database;
    return Results.Json(new { pageId, slug, resultsInUniqueUrl = db.WillAddressResultInUniqueUrl(new NodeKey(pageId), slug) });
});

// Where every type and relation in the datamodel came from: each datamodel source with its id,
// and for file based sources also the file each type was defined in:
app.MapGet("/datamodel-sources", (RelatudeDBContext ctx) => {
    var dm = ctx.Database.Datastore.Datamodel;
    return Results.Json(dm.Sources.Select(s => new {
        s.Id, s.Name, Type = s.Type.ToString(), s.Namespace, s.Filepath, s.Reference,
        NodeTypes = dm.NodeTypes.Values.Where(t => t.DatamodelSourceId == s.Id)
            .Select(t => new { Type = t.FullName, File = t.DatamodelSourceFilename }),
        Relations = dm.Relations.Values.Where(r => r.DatamodelSourceId == s.Id)
            .Select(r => new { Relation = r.FullName(), File = r.DatamodelSourceFilename }),
    }));
});

app.UseDefaultFiles();
app.UseStaticFiles();


app.UseMiddleware<RelatudeDBMiddleware>();

//app.UseRelatudeDB();

app.StartRelatudeDB();
app.MapRelatudeDBAdmin();
app.MapRelatudeDBClient();

// GraphQL endpoint generated from the datamodel (see wwwroot/graphql.html for a GraphiQL playground).
// POST /graphql for queries, GET /graphql?sdl for the schema as SDL.
app.MapRelatudeDBGraphQL("/graphql", o => {
    o.StoreResolver = http => http.RequestServices.GetRequiredService<RelatudeDBContext>().Database.Datastore;
});

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

class DemoArticlePlugin : NodeTransactionPlugin<DemoArticle> {
    public override void OnBeforeNodeAction(NodeKey key, NodeOperation operation, Transaction transaction, NodeHelper<DemoArticle> helper) {
        //var node = helper.GetNode();
        //var address = node.File.IsEmpty ? node.Title : node.File.Name;
        //transaction.UpdateAddress(key, address);
    }
    public override void OnAfterFileUpload(FileValue fileValue, DemoArticle node) {
        Database.UpdateAddress(node, node.File.IsEmpty ? node.Title : node.File.Name);
    }
}

