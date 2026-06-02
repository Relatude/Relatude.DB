using Microsoft.Net.Http.Headers;
using Relatude.DB.Common;
using Relatude.DB.Demo.Models;
using Relatude.DB.FileConversion;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.DB.NodeServer;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
builder.AddRelatudeDB(options => {
    options.FileConverters.Add(new SkiaImageConverter());
    options.FileConverters.Add(new FFMpegVideoConverter());
    options.FileHandlerRootUrl = "/files";

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
bool hasInserted = false;
app.MapGet("/Insert", async (RelatudeDBContext ctx) => {
    //if (hasInserted) return "Already inserted.";
    hasInserted = true;
    //var files = Directory.GetFiles(@"C:\Users\ogulb\Pictures\", "*.mp4").ToArray();

    var files1 = Directory.GetFiles(@"C:\Users\ogulb\OneDrive\Demo\Videos", "*.*").ToArray();
    var files2 = Directory.GetFiles(@"C:\Users\ogulb\OneDrive\Demo\Pictures", "*.jpg").ToArray();
    var files = files1.Concat(files2).ToArray();
    //ar files = Directory.GetFiles(@"C:\Users\ogulb\OneDrive\Demo\Videos", "sample.mkv").ToArray();
    //var files = Directory.GetFiles(@"C:\Users\ogulb\OneDrive\Demo\Pictures", "nemo.jpg").ToArray();
    for (int i = 0; i < files.Length; i++) {
        var db = ctx.Database;
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
    int i = 0;
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
                    Width = 240,
                    Height = 200,
                    Saturation = 0,
                    RequestedFormat = FileFormat.Png,
                    Sharpness = 0,
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

app.MapGet("/getstatus", (RelatudeDBContext ctx, HttpResponse res) => {
    var db = ctx.Database;
    var running = db.Datastore.GetRunningConversions();
    return Results.Json(running);
});

app.MapPost("/cancel", async (RelatudeDBContext ctx, Guid id, bool permanently) => {
    var db = ctx.Database;
    await db.Datastore.CancelConversion(id, permanently);
});


app.MapPost("/StartUpload", async (RelatudeDBContext ctx, string fileName) => {
    var db = ctx.Database;
    var article = db.CreateAndInsert<DemoArticle>(a => { a.Title = "Uploaded file"; });
    var uploadId = await db.InitiatePartialUploadAsync(article.File, fileName);
    return Results.Json(uploadId);
});
app.MapPost("/UploadPart", async (RelatudeDBContext ctx, Guid uploadId, HttpRequest req) => {
    var db = ctx.Database;
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    var part = ms.ToArray();
    await db.AppendPartialUploadAsync(uploadId, part, part.Length);
});
app.MapPost("/CompleteUpload", async (RelatudeDBContext ctx, Guid uploadId) => {
    var db = ctx.Database;
    await db.FinalizePartialUploadAsync(uploadId);
});



app.UseDefaultFiles();
app.UseStaticFiles();

//app.UseRelatudeDB();

app.StartRelatudeDB();
app.MapRelatudeDBAdmin();
app.MapRelatudeDBClient();

app.Run();
