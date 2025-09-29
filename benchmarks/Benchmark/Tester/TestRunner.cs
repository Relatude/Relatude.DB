using Benchmark.Base.Models;
using Benchmark.Base.Operations;
using System.Diagnostics;
namespace Benchmark.Tester;
internal class TestRunner {
    public static TestReport Run(ITester tester, TestOptions options, TestData testData ) {
        var name = tester.GetType().Name;
        var dataPath = Path.Combine(options.DataFileRootDefault, name);
        tester.Initalize(dataPath);
        var report = new TestReport(name);
        if (options.RecreateDatabase) {
            tester.DeleteDataFiles();
        }
        tester.Open();
        if (options.RecreateDatabase) {
            tester.CreateSchema();
            var sw = Stopwatch.StartNew();
            tester.InsertUsers(testData.Users);
            sw.Stop();
            report.Results.Add(new("InsertUsers", sw.Elapsed, testData.Users.Length));
            sw.Restart();
            tester.InsertCompanies(testData.Companies);
            sw.Stop();
            report.Results.Add(new("InsertCompanies", sw.Elapsed, testData.Companies.Length));
            sw.Restart();
            tester.InsertDocuments(testData.Documents);
            sw.Stop();
            report.Results.Add(new("InsertDocuments", sw.Elapsed, testData.Documents.Length));
            sw.Restart();
            var docsToUsers = testData.Documents.Where(d => d.Author != null).Select(d => new Tuple<Guid, Guid>(d.Id, d.Author!.Id)).ToArray();
            tester.RelateDocumentsToUsers(docsToUsers);
            sw.Stop();
            report.Results.Add(new("RelateDocumentsToUsers", sw.Elapsed, docsToUsers.Length));
            sw.Restart();
            var usersToCompany = testData.Users.Where(u => u.Company != null).Select(u => new Tuple<Guid, Guid>(u.Id, u.Company!.Id)).ToArray();
            tester.RelateUsersToCompanies(usersToCompany);
            sw.Stop();
            report.Results.Add(new("RelateUsersToCompanies", sw.Elapsed, usersToCompany.Length));
        }
        tester.Close();
        report.TotalFileSize = getDirSize(new(dataPath));
        return report;
    }
    static long getDirSize(DirectoryInfo dir) {
        long size = 0;
        foreach (var file in dir.GetFiles()) size += file.Length;
        foreach (var subDir in dir.GetDirectories()) getDirSize(subDir);
        return size;
    }
}
