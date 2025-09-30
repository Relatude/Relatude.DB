using Benchmark;
using Benchmark.Base;
using Benchmark.Base.ContentGeneration;
using Benchmark.LiteDB;
using Benchmark.MSSql;
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
string deCamelCase(string camel) {
    if (string.IsNullOrEmpty(camel)) return camel;
    var sb = new StringBuilder();
    for (int i = 0; i < camel.Length; i++) {
        var c = camel[i];
        if (char.IsUpper(c) && i > 0) {
            sb.Append(' ');
            sb.Append(char.ToLower(c));
        } else {
            sb.Append(c);
        }
    }
    return sb.ToString();
}
string test(HttpContext ctx) {
    var options = new TestOptions();

    var testData = Generator.Generate(options);

    ITester[] testers = [
        new MsSqlDBTester(),
        new LiteDBTester(),
        new SQLiteDBTester(),
        new RelatudeDBTester(),
        ];

    List<TestReport> reports = [];

    foreach (var tester in testers) {
        reports.Add(TestRunner.Run(tester, options, testData));
    }

    var testNames = reports.SelectMany(r => r.Results).Select(r => r.TestName).Distinct();
    var sb = new StringBuilder();
    sb.AppendLine("<!DOCTYPE html>");
    sb.AppendLine("<html lang=\"nb-no\" dir=\"ltr\" >");
    sb.AppendLine("<body>");
    sb.AppendLine("<h1>Operations per sec</h1>");
    sb.AppendLine("<table style=\"width:100%\">");
    sb.AppendLine("<tr>");
    sb.AppendLine("<td></td>");
    foreach (var report in reports) {
        sb.AppendLine("<td style=\"text-align:right\">" + report.Name.ToUpper() + "</td>");
    }
    sb.AppendLine("</tr>");
    foreach (var testName in testNames) {
        sb.AppendLine("<tr>");
        sb.AppendLine("<td >" + deCamelCase(testName) + "</td> ");
        foreach (var report in reports) {
            var result = report.Results.Where(r => r.TestName == testName).First();
            //sb.AppendLine("<td style=\"text-align:right\">" + Math.Round(result.OperationsPerSecond).To1000N() + "</td>");
            sb.AppendLine("<td style=\"text-align:right\">");
            sb.AppendLine(Math.Round(result.OperationsPerSecond).To1000N());
            //sb.AppendLine(" - ");
            //sb.AppendLine(result.Operations.To1000N());
            //sb.AppendLine(" - ");
            //sb.AppendLine(result.Duration.ToString());
            sb.AppendLine("</td>");
        }
        sb.AppendLine("</tr>");
    }
    sb.AppendLine("<tr>");
    sb.AppendLine("<td>Total</td> ");
    foreach (var report in reports) {
        sb.AppendLine("<td style=\"text-align:right\">" + report.Results.Sum(r => r.Duration.TotalMilliseconds).To1000N() + "ms</td>");
    }
    sb.AppendLine("</tr>");
    sb.AppendLine("<tr>");
    sb.AppendLine("<td>Size</td> ");
    foreach (var report in reports) {
        sb.AppendLine("<td style=\"text-align:right\">" + report.TotalFileSize.ToByteString() + "</td>");
    }
    sb.AppendLine("</tr>");
    sb.AppendLine("</table>");
    sb.AppendLine("</body>");
    sb.AppendLine("</html>");
    ctx.Response.Headers["Content-Type"] = "text/html; charset=utf-8";
    return sb.ToString();
}