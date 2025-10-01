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
        var sw = new Stopwatch();
        tester.Open();
        if (options.RecreateDatabase) {

            tester.CreateSchema();

            sw.Restart();
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

        sw.Restart();
        for (int i = 0; i < 100; i++) {
            var users = tester.GetAllUsers();
        }
        sw.Stop();
        report.Results.Add(new(nameof(tester.GetAllUsers), sw.Elapsed, 100 * (70 - 20)));

        sw.Restart();
        foreach (var user in testData.Users) {
            tester.GetUserById(user.Id);
        }
        sw.Stop();
        report.Results.Add(new(nameof(tester.GetUserById), sw.Elapsed, testData.Users.Length));

        sw.Restart();
        for (int i = 0; i < 1000; i++) {
            for (int age = 20; age <= 70; age++) {
                tester.CountUsersOfAge(age);
            }
        }
        sw.Stop();
        report.Results.Add(new(nameof(tester.CountUsersOfAge), sw.Elapsed, 100 * (70-20)));

        sw.Restart();
        var rnd=  new Random(12345);
        for (int i = 0; i <100; i++) {
            foreach (var user in testData.Users.Take(1000)) {
                tester.UpdateUserAge(user.Id, user.Age + rnd.Next(-10,10));
            }
        }   
        sw.Stop();
        report.Results.Add(new(nameof(tester.UpdateUserAge), sw.Elapsed, 10*1000));

        sw.Restart();
        foreach (var user in testData.Users.Take(100)) {
            tester.UpdateUserAge(user.Id, user.Age + 1);
            tester.GetUserAtAge(user.Age);
        }
        sw.Stop();
        report.Results.Add(new(nameof(tester.UpdateUserAge)+ "AndGetUserAtAge", sw.Elapsed, 100));

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
