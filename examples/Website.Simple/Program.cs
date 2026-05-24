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

app.MapGet("/Insert", async (RelatudeDBContext ctx) => {
    var articleCount = 1;
    for (int i = 0; i < articleCount; i++) {
        var db = ctx.Database;
        //var art = new DemoArticle();
        var art = db.Create<IDemoArticle>();
        art.Title = "Ole";
        //var paraGraph = new DemoParagraph();
        var paraGraph = db.Create<IDemoParagraph>();
        paraGraph.Code = "dasdas";
        art.Paragraphs.Add(paraGraph);
        db.Insert(art);
        var filePath = @"C:\Users\ogulb\OneDrive\Demo\Pictures\nemo.jpg";
        //filePath = @"C:\Users\ogulb\OneDrive\Demo\Big photos\Deichmanske.2020.143.jpg";
        var videoFilePath = @"C:\Users\ogulb\OneDrive\Demo\sample-1.mkv";
                //var videoFilePath = @"C:\Users\ogulb\OneDrive\Demo\vid.mkv";
        await db.FileUploadAsync(art.File, videoFilePath);
        //var p = art.Paragraphs.First();
        //if (db.FileStoreSupportsMultipartUploads(p.File)) {
        //    using var stream = File.OpenRead(videoFilePath);
        //    var fileId = await db.InitiatePartialUploadAsync(p.File, Path.GetFileName(videoFilePath));
        //    var buffer = new byte[1024 * 1024];
        //    while (true) {
        //        var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
        //        if (bytesRead == 0) break; // End of file
        //        await db.AppendPartialUploadAsync(fileId, buffer, bytesRead);
        //        Console.WriteLine("File ID: " + fileId + ", bytes uploaded: " + bytesRead);
        //    }
        //    await db.FinalizePartialUploadAsync(fileId);
        //} else {
        //    await db.FileUploadAsync(p.File, videoFilePath);
        //}
        art = db.Get(art);
    }
    return "Inserted " + articleCount + " articles.";
});
app.MapGet("/Search", (RelatudeDBContext ctx) => {
    var db = ctx.Database;
    var results = db.Query<DemoArticle>().WhereSearch("Ole").Execute().ToArray();
    return results;
});
app.MapGet("/List", (RelatudeDBContext ctx, HttpResponse res) => {
    var db = ctx.Database;
    var results = db.Query<IDemoArticle>().Execute().ToArray();
    var html = new StringBuilder();
    html.Append("<html><body style='background-color:#f0f000'>");
    int i = 0;
    foreach (var item in results) {
        //html.Append($"<h2>{item.Title}</h2>");
        //html.Append($"<p>{item.Content}</p>");

        var videoAdj = new FileAdjustmentVideo() {
            Width = 2024, Height = 1024,
            TargetBitRateInMbps = 100,
            RequestedFormat = FileFormat.Mp4            
        };  

        var thumbnailAdj = new FileAdjustmentImage() {
            CropMode = ImageCropMode.Fill,
            Width = 2048,
            Height = 1024,
            HueShift = i++,
            BackgroundColor = "#FF0000",
            RequestedFormat = FileFormat.Jpeg,
            Sharpness = 0,
            Quality = 90
        };
        var videoUrl = $"Image/{db.Datastore.GetUrl(item.File.PropertyPath!, videoAdj, false)}";
        //var thumbnailUrl = $"Image/{db.Datastore.GetUrl(item.File.PropertyPath!, thumbnailAdj, false)}";

        // video tag with fallback to thumbnail image:
        html.Append($"<video autoplay muted loop width='{videoAdj.Width}' height='{videoAdj.Height}' controls >");
        html.Append($"<source src='{videoUrl}' type='video/mp4'>");
        html.Append($"Your browser does not support the video tag. Here is a <a href='{videoUrl}'>link to the video</a> instead.");
        html.Append($"</video>");


        //for (var p = 0; p < 100; p+=2) {
        //    var adj = new FileAdjustmentImage() {
        //        CropMode = ImageCropMode.Fill,
        //        Width = 500,
        //        TimeOffsetPercentage= (double)(p),
        //        HueShift = i++,
        //        BackgroundColor = "#FF0000",
        //        RequestedFormat = FileFormat.Jpeg,
        //        Sharpness = 0,
        //        Quality = 90
        //    };
        //    if (i > 180) i = -180;
        //    if (!item.File.IsEmpty) {
        //        var fileUrl = $"Image/{db.Datastore.GetUrl(item.File.PropertyPath!, adj, false)}";
        //        // thumbnail::
        //        html.Append($"<img src='{fileUrl}'>");
        //        //html.Append($"<p><a href='{fileUrl}'>Download File</a></p>");
        //    }
        //}
        //foreach (var para in item.Paragraphs) {
        //    html.Append($"<h3>Paragraph: {para.Code}</h3>");
        //    if (!para.File.IsEmpty) {
        //        var fileUrl = $"Image/{para.File.PropertyPath}";
        //        // thumbnail::
        //        html.Append($"<p><img src='{fileUrl}'></p>");
        //        html.Append($"<p><a href='{fileUrl}'>Download File</a></p>");
        //    }
        //}
    }
    html.Append("</body></html>");
    res.Headers.ContentType = "text/html; charset=utf-8";
    return html.ToString();
});
app.MapGet("/Image/{propPathAndAdj}", async (RelatudeDBContext ctx, HttpContext http, string propPathAndAdj) => {
    var db = ctx.Database;
    var streamAndState = await db.Datastore.GetFileAndConversionState(propPathAndAdj, 10);
    if (streamAndState.IsReady) {
        // Add header to allow 30 days caching
        http.Response.GetTypedHeaders().CacheControl = new CacheControlHeaderValue { Public = true, MaxAge = TimeSpan.FromDays(30) };
    } else {
        // Add header with no-cache or short cache duration
        http.Response.GetTypedHeaders().CacheControl = new CacheControlHeaderValue { NoCache = true };
    }
    return Results.Stream(streamAndState.Stream, FileFormatUtil.GetContentType(FileFormat.Mp4));
});

app.UseRelatudeDB();

app.Run();
