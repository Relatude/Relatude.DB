using Benchmark;
using Benchmark.Base.ContentGeneration;
using Benchmark.Base.Operations;
using Benchmark.Relatude.DB;
using Benchmark.SQLite;
using Benchmark.Tester;
using Relatude.DB.Common;
using Relatude.DB.Query.ExpressionToString.ZSpitz.Extensions;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", test);

app.Run();

string test(HttpContext ctx) {
    var options = new TestOptions();

    var testData = Generator.Generate(options);

    ITester[] testers = [new SQLiteDBTester(), new RelatudeDBTester()];

    List<TestReport> reports = [];

    foreach (var tester in testers) {
        reports.Add(TestRunner.Run(tester, options, testData));
    }

    var testNames = reports.SelectMany(r => r.Results).Select(r=>r.TestName).Distinct();
    var sb = new StringBuilder();
    sb.AppendLine("<!DOCTYPE html>");
    sb.AppendLine("<html lang=\"nb-no\" dir=\"ltr\" >");
    sb.AppendLine("<body><h1>OPERATIONS PER SECOND, HIGHER IS BETTER</h1>");
    sb.AppendLine("<table style=\"width:100%\">");
    sb.AppendLine("<tr>");
    sb.AppendLine("<td>TEST</td>");
    foreach (var report in reports) {
        sb.AppendLine("<td>" + report.Name.ToUpper() + "</td>");
    }
    sb.AppendLine("</tr>");
    foreach (var testName in testNames) {
        sb.AppendLine("<tr>");
        sb.AppendLine("<td>" + testName + "</td> ");
        foreach (var report in reports) {
            var result = report.Results.Where(r => r.TestName == testName).First();
            sb.AppendLine("<td>" + Math.Round( result.OperationsPerSecond) + "</td>");
        }
        sb.AppendLine("</tr>");
    }
    sb.AppendLine("<tr>");
    sb.AppendLine("<td>Size</td> ");
    foreach (var report in reports) {
        sb.AppendLine("<td>" + report.TotalFileSize.ToByteString() + "</td>");
    }
    sb.AppendLine("</tr>");
    sb.AppendLine("</table>");
    sb.AppendLine("</body>");
    sb.AppendLine("</html>");
    ctx.Response.ContentType = "text/html";
    return sb.ToString();
}