using Benchmark;
using Benchmark.Base;
using Benchmark.Base.ContentGeneration;
using Benchmark.LiteDB;
using Benchmark.MSSql;
using Benchmark.RavenDB;
using Benchmark.Relatude.DB;
using Benchmark.Site.Tester;
using Benchmark.SQLite;
using Benchmark.Tester;
using Relatude.DB.Common;
using Relatude.DB.Query.ExpressionToString.ZSpitz.Extensions;
using System.Text;
using System.Text.Json;
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/start", () => {
    var multiplier = 10;
    var options = new TestOptions();
    options.FlushDiskOnEveryOperation = false;
    options.UserCount = 1000 * multiplier;
    options.CompanyCount = 100 * multiplier;
    options.DocumentCount = 1000 * multiplier;
    options.Duration = TimeSpan.FromMilliseconds(1000);
    //options.SelectedTests = [nameof(ITester.UpdateUserAge)];
    var testData = Generator.Generate(options);
    Status.Current.Running = true;

    ITester[] testers = [
        //new MsSqlDBTester(),
        //new RavenDBEmbeddedTester(),
        new LiteDBTester(),
        new SQLiteDBTester(),
        new RelatudeDBTester(),
        ];

    Status.Current.Initialize(testers.Select(t => t.Name).ToArray(), TestRunner.GetTestNames(), options);
    foreach (var tester in testers) {
        TestRunner.Run(tester, options, testData, Status.Current);
    }
    Status.Current.Running = false;
});

app.MapGet("/status", async (HttpContext ctx) => {
    ctx.Response.Headers.ContentType = "text/event-stream";
    while (!ctx.RequestAborted.IsCancellationRequested) {
        var json = JsonSerializer.Serialize(Status.Current, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await ctx.Response.WriteAsync($"data: " + json + "\n\n");
        await ctx.Response.Body.FlushAsync();
        await Task.Delay(200);
    }
});
app.Run();
