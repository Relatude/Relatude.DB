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
bool hasInserted = false;
app.MapGet("/Insert", async (RelatudeDBContext ctx) => {
    if (hasInserted) return "Already inserted.";
    hasInserted = true;
    var articleCount = 5;

    var files = Directory.GetFiles(@"C:\Users\ogulb\Pictures\", "*.mp4").ToArray();

    for (int i = 0; i < files.Length; i++) {
        var db = ctx.Database;
        //var art = new DemoArticle();
        var art = db.Create<IDemoArticle>();
        art.Title = "Ole";
        //var paraGraph = new DemoParagraph();
        var paraGraph = db.Create<IDemoParagraph>();
        paraGraph.Code = "dasdas";
        art.Paragraphs.Add(paraGraph);
        db.Insert(art);
        //var filePath = @"C:\Users\ogulb\OneDrive\Demo\Pictures\nemo.jpg";
        //filePath = @"C:\Users\ogulb\OneDrive\Demo\Big photos\Deichmanske.2020.143.jpg";
        //var videoFilePath = @"C:\Users\ogulb\OneDrive\Demo\m.mp4";
        var videoFilePath = files[i];
        //videoFilePath = @"C:\Users\ogulb\Downloads\Send Help.mkv";
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
app.MapGet("/Status", (RelatudeDBContext ctx, HttpResponse res) => {
    var db = ctx.Database;
    var running = db.Datastore.GetRunningConversions();
    var html = new StringBuilder();
    html.Append("<html><body style='background-color:#f0f000'>");
    html.Append("<table border='1'><tr><th>FileName</th><th>From Format</th><th>To Format</th><th>Status</th><th>Progress</th><th>Message</th></tr>");
    foreach (var conversion in running) {
        html.Append($"<tr><td>{conversion.FileInfo.FileName}</td><td>{conversion.FileInfo.FromFormat}</td><td>{conversion.FileInfo.ToFormat}</td><td>{conversion.ProgressInfo.Status}</td><td>{conversion.ProgressInfo.ProgressPercentage}%</td><td>{conversion.ProgressInfo.Message}</td></tr>");
    }
    html.Append("</table>");
    html.Append("</body></html>");
    res.Headers.ContentType = "text/html; charset=utf-8";
    return html.ToString();
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
            Quality = 90
        };
        var thumbnailUrl = $"files/{db.Datastore.GetUrl(item.File.PropertyPath!, thumbnailAdj)}";
        var isThumbnailReady = db.Datastore.IsFileReady(item.File.PropertyPath!, thumbnailAdj, true);

        var videoUrl = $"files/{db.Datastore.GetUrl(item.File.PropertyPath!, videoAdj)}";

        var isVideoReady = db.Datastore.IsFileReady(item.File.PropertyPath!, videoAdj, true);
        //bool isVideoReady = false;
        //if (db.Datastore.TryGetProgressInfo(item.File.PropertyPath!, videoAdj, true, out var info)) {
        //    isVideoReady = info.Status != FileConversionStatus.InProgress;
        //} else {
        //    isVideoReady = false;
        //}
        //isVideoReady = true;

        if (isVideoReady) {
            html.Append($"<video autoplay muted loop width='{videoAdj.Width}' height='{videoAdj.Height}' controls >");
            html.Append($"<source src='{videoUrl}' type='video/mp4'>");
            html.Append($"Your browser does not support the video tag. Here is a <a href='{videoUrl}'>link to the video</a> instead.");
            html.Append($"</video>");
        } else {
            html.Append($"<img src='{thumbnailUrl}'>");
        }


        for (var p = 0; p < 0; p += 20) {
            var adj = new FileAdjustmentImage() {
                CropMode = ImageCropMode.Fill,
                Width = 500,
                TimeOffsetPercentage = (double)(p),
                HueShift = p,
                BackgroundColor = "#FF0000",
                RequestedFormat = FileFormat.Jpeg,
                Quality = 90
            };
            if (i > 180) i = -180;
            if (!item.File.IsEmpty) {
                var fileUrl = $"files/{db.Datastore.GetUrl(item.File.PropertyPath!, adj, false)}";
                // thumbnail::
                html.Append($"<img src='{fileUrl}'>");
                //html.Append($"<p><a href='{fileUrl}'>Download File</a></p>");
            }
        }
        //foreach (var para in item.Paragraphs) {
        //    html.Append($"<h3>Paragraph: {para.Code}</h3>");
        //    if (!para.File.IsEmpty) {
        //        var fileUrl = $"files/{para.File.PropertyPath}";
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
app.MapGet("/files/{propPathAndAdj}", async (RelatudeDBContext ctx, HttpContext http, string propPathAndAdj) => {
    var db = ctx.Database;
    var fileInfo = await db.Datastore.GetFileStreamAndState(propPathAndAdj);
    if (fileInfo.IsTemporary) {
        http.Response.GetTypedHeaders().CacheControl = new CacheControlHeaderValue { NoCache = true };
    } else {
        http.Response.GetTypedHeaders().CacheControl = new CacheControlHeaderValue { Public = true, MaxAge = TimeSpan.FromDays(30) };
    }
    var contentType = FileFormatUtil.GetContentType(fileInfo.RequestedFormat);
    var stream = fileInfo.Stream;
    var totalLength = stream.CanSeek ? stream.Length : (long?)null;
    var rangeHeader = http.Request.Headers.Range.ToString();
    if (stream.CanSeek && !string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes=")) {
        var range = rangeHeader["bytes=".Length..].Split('-');
        var start = long.TryParse(range[0], out var s) ? s : 0;
        var end = range.Length > 1 && long.TryParse(range[1], out var e) ? e : totalLength!.Value - 1;
        end = Math.Min(end, totalLength!.Value - 1);
        var length = end - start + 1;
        stream.Seek(start, SeekOrigin.Begin);
        http.Response.StatusCode = 206;
        http.Response.Headers.ContentRange = $"bytes {start}-{end}/{totalLength}";
        http.Response.Headers.AcceptRanges = "bytes";
        http.Response.ContentLength = length;
        http.Response.ContentType = contentType;
        await stream.CopyToAsync(http.Response.Body, (int)Math.Min(length, 81920));
        return Results.Empty;
    }
    if (totalLength.HasValue)
        http.Response.Headers.AcceptRanges = "bytes";
    return Results.Stream(stream, contentType);
});

app.UseRelatudeDB();

app.Run();
