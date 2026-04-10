using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Stores;
using Relatude.DB.Demo.Models;
using Relatude.DB.NodeServer;
using System.Diagnostics;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.AddRelatudeDB();

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
app.MapGet("/test", (RelatudeDBContext ctx) => {
    var db = ctx.Database;
    var art1 = new DemoArticle {
        Title = "Test Article",
        Content = "This is a test article.",
        Size = 123,
        DisplayName = "Test Article Display Name",
        Address = "asdasdast",
    };

    var art2 = new DemoArticle {
        Title = "Another Article",
        Content = "This is another test article.",
        Size = 456,
        DisplayName = "Another Article Display Name",
        Address = "asdasd",
    };
    db.Insert([art1, art2]);
    db.UpdateAddress(art1, "new-address-for-art1");
    db.UpdateAddress(art2, "new-address-for-art2");
    var sb= new StringBuilder();
    foreach (var a in db.Query<DemoArticle>().Execute()) {
        sb.AppendLine($"Article: {a.Title}, Address: {a.Address}, DisplayName: {a.DisplayName}");
    }
    return sb.ToString();

});
bool firstUpload = true;
app.MapGet("/testUpload", async (RelatudeDBContext ctx) => {
    //if (!firstUpload) return "Already uploaded. Restart the app to upload again.";
    firstUpload = false;
    var db = ctx.Database;
    var files = Directory.GetFiles("C:\\UploadTest");
    var sw = Stopwatch.StartNew();
    //foreach (var file in files) {
    await Parallel.ForEachAsync(files, async (file, cancellationToken) => {
        var a = db.CreateAndInsert<DemoArticle>(a => {
            a.Title = Path.GetFileNameWithoutExtension(file);
            a.Content = "Lorem ipsum dolor sit amet, consectetur adipiscing elit. Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua.";
        });
        await db.FileUploadAsync(a, a => a.File, file);
    });
    sw.Stop();
    Console.WriteLine("Uploaded: " + files.Length + " files in " + sw.ElapsedMilliseconds + " ms.");
    return "Uploaded: " + files.Length + " files in " + sw.ElapsedMilliseconds + " ms.";
});
app.MapGet("/testDelete", async (RelatudeDBContext ctx) => {
    var db = ctx.Database;
    var sw = Stopwatch.StartNew();
    Parallel.ForEach(db.Query<DemoArticle>().Execute(), async (a) => {
        db.DeleteIfExists(a.Id);
    });
    db.Maintenance(Relatude.DB.DataStores.MaintenanceAction.TruncateLog);
    sw.Stop();
    Console.WriteLine("Deleted in " + sw.ElapsedMilliseconds + " ms.");
    return "Deleted in " + sw.ElapsedMilliseconds + " ms.";
});
bool firstDownload = true;
app.MapGet("/testDownload", async (RelatudeDBContext ctx) => {
    //if (!firstDownload) return "Done";
    firstDownload = false;
    var db = ctx.Database;
    var tempOutput = "C:\\DownloadTest";
    //if (Directory.Exists(tempOutput)) Directory.Delete(tempOutput, true);
    //Directory.CreateDirectory(tempOutput);
    StringBuilder sb = new StringBuilder();
    var sw = Stopwatch.StartNew();
    await Parallel.ForEachAsync(db.Query<DemoArticle>().Execute(), async (a, cancellationToken) => {
        //foreach (var a in db.Query<DemoArticle>().Execute()) {
        //sb.Append(a.File.FileId);
        if (!a.File.IsEmpty) {
            var bytes = await db.FileDownloadAsync(a, a => a.File);
            Console.WriteLine("Bytes: " + bytes.Length);
            //File.WriteAllBytes(Path.Combine(tempOutput, a.File.Name), bytes);
        }
    });
    sw.Stop();
    Console.WriteLine("Downloaded: " + db.Query<DemoArticle>().Count() + " files in " + sw.ElapsedMilliseconds + " ms. Saved to folder: " + tempOutput);
    sb.AppendLine($"Downloaded: {db.Query<DemoArticle>().Count()} files in {sw.ElapsedMilliseconds} ms. Saved to folder: {tempOutput}");
    return sb.ToString();
});


app.UseRelatudeDB();

app.Run();
