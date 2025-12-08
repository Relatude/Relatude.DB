using Benchmark.Base;
using Benchmark.Base.Models;
using Benchmark.Site.Tester;
namespace Benchmark;

internal class TestRunner {
    public static string[] GetTestNames() {
        return [
            nameof(ITester.Open),
            nameof(ITester.InsertUsers),
            nameof(ITester.InsertCompanies),
            nameof(ITester.InsertDocuments),
            nameof(ITester.RelateDocumentsToUsers),
            nameof(ITester.RelateUsersToCompanies),
            nameof(ITester.GetAllUsers),
            nameof(ITester.GetUserById),
            nameof(ITester.CountUsersOfAge),
            nameof(ITester.DeleteUsersOfAge),
            //nameof(ITester.UpdateUserAge),
            nameof(ITesterExtensions.UpdateAndGetUsers),
            nameof(ITester.Close)
        ];
    }
    public static void Run(ITester tester, TestOptions options, TestData testData, Status status) {

        var dataPath = Path.Combine(options.DataFileRootDefault, tester.Name);

        status.Start(tester.Name, nameof(tester.Open));
        tester.Initalize(dataPath, options);
        if (options.RecreateDatabase) tester.DeleteDataFiles();
        tester.Open();
        status.Complete(1);

        if (options.RecreateDatabase) {

            tester.CreateSchema();

            status.Start(tester.Name, nameof(tester.InsertUsers));
            tester.InsertUsers(testData.Users);
            status.Complete(testData.Users.Length);

            status.Start(tester.Name, nameof(tester.InsertCompanies));
            tester.InsertCompanies(testData.Companies);
            status.Complete(testData.Companies.Length);

            status.Start(tester.Name, nameof(tester.InsertDocuments));
            tester.InsertDocuments(testData.Documents);
            status.Complete(testData.Documents.Length);

            status.Start(tester.Name, nameof(tester.RelateDocumentsToUsers));
            tester.RelateDocumentsToUsers(testData.DocsToUsers);
            status.Complete(testData.DocsToUsers.Count);

            status.Start(tester.Name, nameof(tester.RelateUsersToCompanies));
            tester.RelateUsersToCompanies(testData.UsersToCompany);
            status.Complete(testData.UsersToCompany.Count);

        }

        var rnd = new Random(options.RandomSeed);

        int count = 0;
        status.Start(tester.Name, nameof(tester.GetAllUsers));
        while (status.KeepRunning()) {
            var users = tester.GetAllUsers();
            count += users.Length;
        }
        status.Complete(count);

        count = 0;
        var max = testData.Users.Length;
        status.Start(tester.Name, nameof(tester.GetUserById));
        while (status.KeepRunning()) {
            var user = testData.Users[rnd.Next(0, max)];
            user = tester.GetUserById(user.Id);
            count++;
        }
        status.Complete(count);

        count = 0;
        status.Start(tester.Name, nameof(tester.CountUsersOfAge));
        while (status.KeepRunning()) {
            for (int age = 20; age <= 70; age++) {
                tester.CountUsersOfAge(age);
                count++;
            }
        }
        status.Complete(count);

        count = 0;
        status.Start(tester.Name, nameof(tester.DeleteUsersOfAge));
        while (status.KeepRunning()) {
            var user = testData.Users[rnd.Next(0, max)];
            tester.DeleteUsersOfAge(rnd.Next(10, 100));
            count++;
        }
        status.Complete(count);

        //count = 0;
        //status.Start(tester.Name, nameof(tester.UpdateUserAge));
        //while (status.KeepRunning()) {
        //    var user = testData.Users[rnd.Next(0, max)];
        //    tester.UpdateUserAge(user.Id, user.Age + (rnd.Next(4) - 2));
        //    count++;
        //}
        //status.Complete(count);

        count = 0;
        status.Start(tester.Name, nameof(ITesterExtensions.UpdateAndGetUsers));
        while (status.KeepRunning()) {
            var user = testData.Users[rnd.Next(0, max)];
            tester.UpdateAndGetUsers(user.Id, user.Age);
            count++;
        }
        status.Complete(count);

        status.Start(tester.Name, nameof(tester.Close));
        tester.Close();
        status.Complete(1);

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
