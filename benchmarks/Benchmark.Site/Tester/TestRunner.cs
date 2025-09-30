using Benchmark.Base;
using Benchmark.Base.Models;
using Benchmark.Tester;
using System.Diagnostics;
namespace Benchmark;
internal class TestRunner {
    public static TestReport Run(ITester tester, TestOptions options, TestData testData) {
        var dataPath = Path.Combine(options.DataFileRootDefault, tester.Name);
        tester.Initalize(dataPath);
        var report = new TestReport(tester.Name);
        if (options.RecreateDatabase) {
            tester.DeleteDataFiles();
        }
        tester.Open();
        if (options.RecreateDatabase) {
            tester.CreateSchema();
            var sw = Stopwatch.StartNew();
            tester.InsertUsers(testData.Users);
            sw.Stop();
            report.Results.Add(new(nameof(tester.InsertUsers), sw.Elapsed, testData.Users.Length));
            sw.Restart();
            tester.InsertCompanies(testData.Companies);
            sw.Stop();
            report.Results.Add(new(nameof(tester.InsertCompanies), sw.Elapsed, testData.Companies.Length));
            sw.Restart();
            tester.InsertDocuments(testData.Documents);
            sw.Stop();
            report.Results.Add(new(nameof(tester.InsertDocuments), sw.Elapsed, testData.Documents.Length));
            sw.Restart();
            tester.RelateDocumentsToUsers(testData.DocsToUsers);
            sw.Stop();
            report.Results.Add(new(nameof(tester.RelateDocumentsToUsers), sw.Elapsed, testData.DocsToUsers.Count));
            sw.Restart();
            tester.RelateUsersToCompanies(testData.UsersToCompany);
            sw.Stop();
            report.Results.Add(new(nameof(tester.RelateUsersToCompanies), sw.Elapsed, testData.UsersToCompany.Count));
        }
        tester.Close();
        report.TotalFileSize = getDirSize(dataPath);
        return report;
    }
    static long getDirSize(string path) {
        var dir = new DirectoryInfo(path);
        if (!dir.Exists) return 0;
        return getDirSize(dir);
    }
    static long getDirSize(DirectoryInfo dir) {
        long size = 0;
        foreach (var file in dir.GetFiles()) size += file.Length;
        foreach (var subDir in dir.GetDirectories()) getDirSize(subDir);
        return size;
    }
}
