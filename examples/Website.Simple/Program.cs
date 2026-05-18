using Relatude.DB.Common;
using Relatude.DB.Demo.Models;
using Relatude.DB.FileConversion.Images;
using Relatude.DB.FileConverter;
using Relatude.DB.NodeServer;
using Relatude.DB.Query;
using System.Runtime.CompilerServices;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.AddRelatudeDB(options => {
    options.FileConverters.Add(new DefaultImageConverter());
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
    Console.WriteLine("Inserting article...");
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
    var videoFilePath = @"C:\Users\ogulb\OneDrive\Demo\Pictures\Bugatti.jpg";
    //var videoFilePath = @"C:\Users\ogulb\OneDrive\Demo\vid.mkv";
    await db.FileUploadAsync(art.File, filePath);
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
    return art.File.IsEmpty ? "File upload failed" : "Inserted article with file";
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
    foreach (var item in results) {
        html.Append($"<h2>{item.Title}</h2>");
        html.Append($"<p>{item.Content}</p>");
        var adj = new FileAdjustmentImage() {
            CropMode = ImageCropMode.Fill,
            Width = 200,
            Height = 70,
            FocusX = 0, FocusY = 0,
            Zoom = 100,
            BackgroundColor = "#ffffff",
            RequestedFormat = FileFormat.Jpeg,
            Quality = 90
        };
        if (!item.File.IsEmpty) {
            var fileUrl = $"Image/{db.Datastore.GetUrl(item.File.PropertyPath!, adj, false)}";
            // thumbnail::
            html.Append($"<p><img src='{fileUrl}'></p>");
            html.Append($"<p><a href='{fileUrl}'>Download File</a></p>");
        }
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
app.MapGet("/Image/{propPath}", async (RelatudeDBContext ctx, string propPath) => {
    var db = ctx.Database;
    var stream = await db.Datastore.GetFile(propPath, 100); 
    return Results.Stream(stream, FileFormatUtil.GetContentTypeFromFormat(FileFormat.Jpeg), enableRangeProcessing: true);

});

app.UseRelatudeDB();

app.Run();
