using Benchmark;
using Benchmark.Base;
using Benchmark.Base.ContentGeneration;
using Benchmark.LiteDB;
using Benchmark.Relatude.DB;
using Benchmark.Site.Tester;
using Benchmark.SQLite;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/start", () => {
    Status.Current.Running = true;
    var dataSetMultiplier = 10000;
    var timeMultiplier = 1;
    var options = new TestOptions();
    options.FlushDiskOnEveryOperation = false;
    options.UserCount = 1 * dataSetMultiplier;
    options.CompanyCount = (int)Math.Round(0.5 * dataSetMultiplier);
    options.DocumentCount = 1 * dataSetMultiplier;
    options.Duration = TimeSpan.FromMilliseconds(1000 * timeMultiplier);
    //options.SelectedTests = [nameof(ITester.UpdateUserAge)];
    var testData = Generator.Generate(options);

    ITester[] testers = [
        //new MsSqlDBTester(),
        //new RavenDBEmbeddedTester(),
        new LiteDBTester(),
        new SQLiteDBTester(),
        new RelatudeDBTester( RelatudeDiskFlushMode.DiskFlush),
        new RelatudeDBTester( RelatudeDiskFlushMode.StreamFlush),
        new RelatudeDBTester( RelatudeDiskFlushMode.AutoFlush),
        new RelatudeDBTester( RelatudeDiskFlushMode.NoFlush),
        ];

    Status.Current.Initialize(testers.Select(t => t.Name).ToArray(), TestRunner.GetTestNames(), options);
    foreach (var tester in testers) {
        TestRunner.Run(tester, options, testData, Status.Current);
    }
    Status.Current.Running = false;
});

app.MapGet("/status", async (HttpContext ctx) => {
    ctx.Response.Headers.ContentType = "text/event-stream";
    var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    var lastStatusWasRunning = true;
    var update = true;
    while (!ctx.RequestAborted.IsCancellationRequested) {
        if (update) {
            var json = JsonSerializer.Serialize(Status.Current, options);
            await ctx.Response.WriteAsync($"data: " + json + "\n\n");
            await ctx.Response.Body.FlushAsync();
        }
        update = Status.Current.Running || lastStatusWasRunning;
        lastStatusWasRunning = Status.Current.Running;
        await Task.Delay(100);
    }


});
app.Run();
