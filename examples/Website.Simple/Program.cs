using Microsoft.Net.Http.Headers;
using Relatude.DB.Common;
using Relatude.DB.Demo.Models;
using Relatude.DB.FileConversion;
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
    var files = Directory.GetFiles(@"C:\Users\ogulb\OneDrive\Demo\Videos", "*.*").ToArray();
    //var files = Directory.GetFiles(@"C:\Users\ogulb\OneDrive\Demo\Pictures", "*.jpg").ToArray();
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

app.MapPost("/CancelConversion", (RelatudeDBContext ctx, Guid conversionKey) => {
    var db = ctx.Database;
    db.Datastore.CancelConversion(conversionKey);
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
                    Width = 640, Height = 360,
                    TargetBitRateInMbps = 0.5,
                    RequestedFormat = FileFormat.Mp4,
                };
                var thumbnailAdj = new FileAdjustmentImage() {
                    CropMode = ImageCropMode.Fill,
                    Width = 640,
                    Height = 360,
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

app.MapPost("/cancel", (RelatudeDBContext ctx, Guid id) => {
    var db = ctx.Database;
    db.Datastore.CancelConversion(id);    
});

app.UseDefaultFiles();
app.UseStaticFiles();

//app.UseRelatudeDB();

app.StartRelatudeDB();
app.MapRelatudeDBAdmin();
app.MapRelatudeDBClient();

app.Run();
