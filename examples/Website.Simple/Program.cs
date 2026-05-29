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
    //var files = Directory.GetFiles(@"C:\Users\ogulb\OneDrive\Demo\Pictures", "*.jpg").Take(1).ToArray();
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



app.MapGet("/status", (RelatudeDBContext ctx, HttpResponse res) => {
    var db = ctx.Database;
    var running = db.Datastore.GetRunningConversions();
    var html = new StringBuilder();
    html.Append("""
        <!DOCTYPE html><html><head><meta charset='utf-8'>
        <title>Conversion Status</title>
        <style>
            *{box-sizing:border-box;margin:0;padding:0}
            body{font-family:'Segoe UI',sans-serif;background:#0f1117;color:#e2e8f0;min-height:100vh;padding:2rem}
            h1{font-size:1.6rem;font-weight:600;margin-bottom:1.5rem;color:#7dd3fc;letter-spacing:.04em}
            .badge-count{display:inline-block;background:#1e293b;border:1px solid #334155;border-radius:999px;padding:.15rem .75rem;font-size:.8rem;color:#94a3b8;margin-left:.6rem;vertical-align:middle}
            .empty{background:#1e293b;border:1px solid #334155;border-radius:.75rem;padding:2rem;text-align:center;color:#64748b;font-size:.95rem}
            table{width:100%;border-collapse:collapse;background:#1e293b;border-radius:.75rem;overflow:hidden;box-shadow:0 4px 24px #0006}
            thead tr{background:#0f172a}
            th{padding:.75rem 1rem;text-align:left;font-size:.75rem;text-transform:uppercase;letter-spacing:.08em;color:#64748b;border-bottom:1px solid #334155}
            td{padding:.75rem 1rem;font-size:.875rem;border-bottom:1px solid #1e293b;vertical-align:middle}
            tr:last-child td{border-bottom:none}
            tr:hover td{background:#263044}
            .bar-wrap{background:#0f172a;border-radius:999px;height:8px;min-width:120px;overflow:hidden}
            .bar{height:100%;border-radius:999px;background:linear-gradient(90deg,#38bdf8,#818cf8);transition:width .4s}
            .pct{font-size:.75rem;color:#94a3b8;margin-top:.25rem}
            .chip{display:inline-block;padding:.15rem .6rem;border-radius:.375rem;font-size:.75rem;font-weight:600}
            .chip-progress{background:#172554;color:#38bdf8}
            .chip-ready{background:#052e16;color:#4ade80}
            .chip-error{background:#450a0a;color:#f87171}
            .fmt{font-family:monospace;font-size:.8rem;color:#a5b4fc}
            .btn-cancel{background:#450a0a;color:#f87171;border:1px solid #7f1d1d;border-radius:.375rem;padding:.25rem .75rem;font-size:.75rem;font-weight:600;cursor:pointer}.btn-cancel:hover{background:#7f1d1d}
        </style>
        <body>
        <h1>⚙ File Conversions <span class='badge-count' id='cnt'></span></h1>
        """);
    if (!running.Any()) {
        html.Append("<div class='empty'>No active conversions right now.</div>");
    } else {
        html.Append("""
            <table><thead><tr>
            <th>File</th><th>From</th><th>To</th><th>Status</th><th>Progress</th><th>Message</th><th></th>
            </tr></thead><tbody>
            """);
        foreach (var c in running) {
            var chipClass = c.ProgressInfo.Status switch {
                FileConversionStatus.Ready => "chip-ready",
                FileConversionStatus.Error => "chip-error",
                _ => "chip-progress"
            };
            var pct = Math.Clamp(c.ProgressInfo.ProgressPercentage, 0, 100);
            html.Append($$$"""
                <tr>
                  <td>{{{System.Net.WebUtility.HtmlEncode(c.FileInfo.FileName)}}}</td>
                  <td><span class='fmt'>{{{c.FileInfo.FromFormat}}}</span></td>
                  <td><span class='fmt'>{{{c.FileInfo.ToFormat}}}</span></td>
                  <td><span class='chip {{{chipClass}}}'>{{{c.ProgressInfo.Status}}}</span></td>
                  <td>
                    <div class='bar-wrap'><div class='bar' style='width:{{{pct}}}%'></div></div>
                    <div class='pct'>{{{pct}}}%</div>
                  </td>
                  <td>{{{System.Net.WebUtility.HtmlEncode(c.ProgressInfo.Message ?? "")}}}</td>
                  <td><button class='btn-cancel' onclick="fetch('/CancelConversion?conversionKey={{{c.Key}}}',{method:'POST'}).then(()=>location.reload())">Cancel</button></td>
                </tr>
                """);
        }
        html.Append("</tbody></table>");
    }
    html.Append("""
        <script>
          document.getElementById('cnt').textContent = document.querySelectorAll('tbody tr').length + ' active';
          setTimeout(() => location.reload(), 500);
        </script>
        </body></html>
        """);
    res.Headers.ContentType = "text/html; charset=utf-8";
    return html.ToString();
});



//app.UseRelatudeDB();

app.StartRelatudeDB();
app.MapRelatudeDBAdmin();
app.MapRelatudeDBClient();

app.Run();
