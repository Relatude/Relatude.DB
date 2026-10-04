using System.Diagnostics;
using Relatude.DB.Common;
using Relatude.DB.NodeServer;

namespace Relatude.Server;

/// <summary>
/// A default database that fails to open at startup because another process still holds one of its files makes the
/// server restart itself a few minutes later, a few times (ServerOptions.StartupRestartAttempts). The lock is faked
/// here by the url manager factory, which runs inside the open of every database: it throws the same exception a
/// file held past the retry budget ends in.
/// </summary>
[TestClass]
public class ServerStartupRestartTests {

    string _root = string.Empty;

    [TestInitialize]
    public void CreateRoot() {
        _root = Path.Combine(Path.GetTempPath(), "relatude.startup-restart." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void DeleteRoot() {
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>Starts a server whose named database fails its first <paramref name="failures"/> opens with <paramref name="error"/>.</summary>
    TestServerHost start(int failures, Func<Exception>? error = null, int databases = 1, string failing = "Database 0",
        TimeSpan? delay = null, int attempts = 2) {
        var left = failures;
        return TestServerHost.Start(_root, databases,
            // opened in the background, as a site that does not hold its start up for the database does
            configure: s => { foreach (var c in s.ContainerSettings!) c.WaitUntilOpen = false; },
            options: o => {
                o.AutoOpenRetryTimeout = TimeSpan.Zero; // one attempt per open, the restart is what is tested
                o.StartupRestartDelay = delay ?? TimeSpan.FromMilliseconds(50);
                o.StartupRestartAttempts = attempts;
                o.CreateUrlManager = settings => {
                    if (settings.Name == failing && Interlocked.Decrement(ref left) >= 0) {
                        throw error?.Invoke() ?? new FileLockedException("\"db.log\" is held by another process.", null);
                    }
                    return null;
                };
            });
    }

    static async Task<bool> until(Func<bool> condition, int seconds = 20) {
        var sw = Stopwatch.StartNew();
        while (!condition()) {
            if (sw.Elapsed.TotalSeconds > seconds) return false;
            await Task.Delay(20);
        }
        return true;
    }

    static bool hasFailed(TestServerHost host) {
        var c = host.Server.DefaultContainer;
        return c != null && c.StartUpException != null && !c.IsOpening;
    }

    [TestMethod]
    public async Task ALockedFile_RestartsTheServerUntilTheDefaultDatabaseOpens() {
        var host = start(failures: 2);
        try {
            Assert.IsTrue(await until(() => host.Server.DefaultStoreIsOpen()), "the second restart should have opened it");
            Assert.AreEqual(2, host.Server.RestartCount);
            Assert.IsNull(host.Server.ScheduledStartupRestart);
            Assert.IsNull(host.Server.DefaultContainer!.StartUpException);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ALockedFile_GivesUpAfterTheAttempts() {
        var host = start(failures: 100);
        try {
            Assert.IsTrue(await until(() => host.Server.RestartCount == 2 && hasFailed(host) && host.Server.ScheduledStartupRestart == null));
            await Task.Delay(800); // well past the delay of a third one
            Assert.AreEqual(2, host.Server.RestartCount, "two restarts and no more");
            Assert.IsFalse(host.Server.DefaultStoreIsOpen());
            Assert.IsNull(host.Server.ScheduledStartupRestart);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ALockedFile_IsAnnouncedWithTheTimeOfTheRestart() {
        var host = start(failures: 100, delay: TimeSpan.FromHours(1));
        try {
            Assert.IsTrue(await until(() => host.Server.ScheduledStartupRestart != null));
            var scheduled = host.Server.ScheduledStartupRestart!;
            Assert.AreEqual(1, scheduled.Attempt);
            Assert.AreEqual(2, scheduled.Attempts);
            Assert.IsTrue(scheduled.DueUtc > DateTime.UtcNow.AddMinutes(59));
            Assert.AreEqual(0, host.Server.RestartCount);

            // a restart left waiting must not reopen anything behind a host that is stopping
            host.Server.BeginShutdown();
            Assert.IsNull(host.Server.ScheduledStartupRestart);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ADatabaseOpenedByHand_CancelsTheRestart() {
        var host = start(failures: 1, delay: TimeSpan.FromMilliseconds(1500));
        try {
            Assert.IsTrue(await until(() => host.Server.ScheduledStartupRestart != null));
            host.Server.DefaultContainer!.Open(); // the lock has cleared, and someone pressed Start
            await Task.Delay(2500);
            Assert.AreEqual(0, host.Server.RestartCount, "the database is open, there is nothing to restart for");
            Assert.IsTrue(host.Server.DefaultStoreIsOpen());
            Assert.IsNull(host.Server.ScheduledStartupRestart);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task AFailureOtherThanALock_DoesNotRestart() {
        var host = start(failures: 100, error: () => new InvalidOperationException("The datamodel is broken."));
        try {
            Assert.IsTrue(await until(() => hasFailed(host)));
            await Task.Delay(500);
            Assert.AreEqual(0, host.Server.RestartCount, "waiting does not mend a broken datamodel");
            Assert.IsNull(host.Server.ScheduledStartupRestart);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ALockOnAnotherDatabase_DoesNotRestart() {
        var host = start(failures: 100, databases: 2, failing: "Database 1");
        try {
            var other = host.Server.GetContainers().Single(c => c.Settings.Name == "Database 1");
            Assert.IsTrue(await until(() => other.StartUpException != null && !other.IsOpening));
            Assert.IsTrue(await until(() => host.Server.DefaultStoreIsOpen()));
            await Task.Delay(500);
            Assert.AreEqual(0, host.Server.RestartCount, "only the default database restarts the server");
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ALockAfterTheDefaultDatabaseHasOpened_DoesNotRestart() {
        // opens at startup, then the lock arrives with a restart from the admin UI: not a startup any more
        var left = 0;
        var host = TestServerHost.Start(_root,
            configure: s => { foreach (var c in s.ContainerSettings!) c.WaitUntilOpen = false; },
            options: o => {
                o.AutoOpenRetryTimeout = TimeSpan.Zero;
                o.StartupRestartDelay = TimeSpan.FromMilliseconds(50);
                o.CreateUrlManager = _ => {
                    if (Interlocked.Decrement(ref left) >= 0) throw new FileLockedException("\"db.log\" is held by another process.", null);
                    return null;
                };
            });
        try {
            Assert.IsTrue(await until(() => host.Server.DefaultStoreIsOpen()));
            Interlocked.Exchange(ref left, 100);
            Assert.IsTrue(await host.Server.SoftRestartAsync());
            Assert.IsTrue(await until(() => hasFailed(host)));
            await Task.Delay(500);
            Assert.AreEqual(1, host.Server.RestartCount, "only the restart asked for");
            Assert.IsNull(host.Server.ScheduledStartupRestart);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task NoAttempts_TurnsItOff() {
        var host = start(failures: 100, attempts: 0);
        try {
            Assert.IsTrue(await until(() => hasFailed(host)));
            await Task.Delay(500);
            Assert.AreEqual(0, host.Server.RestartCount);
            Assert.IsNull(host.Server.ScheduledStartupRestart);
        } finally {
            await host.DisposeAsync();
        }
    }
}
