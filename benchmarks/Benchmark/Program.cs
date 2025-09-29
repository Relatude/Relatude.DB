using Benchmark.Base.ContentGeneration;
using Benchmark.Base.Models;
using Benchmark.Base.Operations;
using Benchmark.Relatude.DB;
using Benchmark.SQLite;
using Benchmark.Tester;

var options = new TestOptions();

var testData = Generator.Generate(options);

ITester[] testers = [new SQLiteDBTester(), new RelatudeDBTester()];

List<TestReport> reports = [];

foreach (var tester in testers) {
    reports.Add(TestRunner.Run(tester, options, testData));
}

var testNames = reports.SelectMany(r => r.Results).Distinct();

foreach (var testName in testNames) {
    Console.Write(testName);
    Console.Write(": ");
    foreach (var tester in testers) {

    }
}
