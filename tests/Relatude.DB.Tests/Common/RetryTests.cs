using Relatude.DB.Common;
using System.Diagnostics;

namespace Relatude.Common;

/// <summary>
/// Every retry in the database shares one cadence, so that "how often do we retry?" has a single
/// answer. These tests pin that schedule: changing it here is a deliberate change to how the whole
/// system behaves while a host hands over from one process to the next.
/// </summary>
[TestClass]
public class RetryTests {

    [TestMethod]
    public void DelayAfter_DoublesFromAHundredMsThenHoldsAtTwoSeconds() {
        Assert.AreEqual(100, Retry.DelayAfter(1).TotalMilliseconds, "first retry");
        Assert.AreEqual(200, Retry.DelayAfter(2).TotalMilliseconds);
        Assert.AreEqual(400, Retry.DelayAfter(3).TotalMilliseconds);
        Assert.AreEqual(800, Retry.DelayAfter(4).TotalMilliseconds);
        Assert.AreEqual(1600, Retry.DelayAfter(5).TotalMilliseconds);
        Assert.AreEqual(2000, Retry.DelayAfter(6).TotalMilliseconds, "the cap");
        Assert.AreEqual(2000, Retry.DelayAfter(7).TotalMilliseconds);
        Assert.AreEqual(2000, Retry.DelayAfter(500).TotalMilliseconds, "and it stays there");
    }

    [TestMethod]
    public void DelayAfter_TotalsMatchWhatTheLogsReport() {
        // a wait that ends on attempt N has slept DelayAfter(1..N-1); these are the numbers that show
        // up in "opened on attempt 11 after 13.2 s"
        Assert.AreEqual(3100, cumulativeBefore(6), "3.1 s covers the whole ramp");
        Assert.AreEqual(13100, cumulativeBefore(11));
        Assert.AreEqual(17100, cumulativeBefore(13));
    }
    static double cumulativeBefore(int attempt) {
        var total = 0d;
        for (var i = 1; i < attempt; i++) total += Retry.DelayAfter(i).TotalMilliseconds;
        return total;
    }

    [TestMethod]
    public void DelayAfter_IsDefinedForNonsenseInput() {
        Assert.AreEqual(100, Retry.DelayAfter(0).TotalMilliseconds);
        Assert.AreEqual(100, Retry.DelayAfter(-5).TotalMilliseconds);
    }

    [TestMethod]
    public void Run_ReturnsAsSoonAsTheOperationSucceeds() {
        var calls = 0;
        var waited = false;
        var result = Retry.Run(() => {
            calls++;
            if (calls < 3) throw new InvalidOperationException("not yet");
            return "done";
        }, _ => true, TimeSpan.FromSeconds(10), onWaitEnded: (_, _) => waited = true);
        Assert.AreEqual("done", result);
        Assert.AreEqual(3, calls);
        Assert.IsTrue(waited, "a retried success must report that the wait ended");
    }

    [TestMethod]
    public void Run_ReportsTheStartOfTheWaitExactlyOnce() {
        var started = 0;
        var calls = 0;
        Retry.Run(() => {
            calls++;
            if (calls < 4) throw new InvalidOperationException("not yet");
            return true;
        }, _ => true, TimeSpan.FromSeconds(10), onWaitStarted: (_, _) => started++);
        Assert.AreEqual(1, started, "one line per wait, not one per attempt");
    }

    [TestMethod]
    public void Run_PassesThroughWhatIsNotWorthRetrying() {
        var calls = 0;
        Assert.ThrowsException<InvalidDataException>(() => Retry.Run<bool>(() => {
            calls++;
            throw new InvalidDataException("corrupt");
        }, err => err is not InvalidDataException, TimeSpan.FromSeconds(30)));
        Assert.AreEqual(1, calls, "it must fail on the attempt it happened, not sit out the budget");
    }

    [TestMethod]
    public void Run_RethrowsTheLastFailureWhenTheBudgetRunsOut() {
        var sw = Stopwatch.StartNew();
        var err = Assert.ThrowsException<InvalidOperationException>(() => Retry.Run<bool>(
            () => throw new InvalidOperationException("still busy"),
            _ => true, TimeSpan.FromMilliseconds(400)));
        Assert.AreEqual("still busy", err.Message, "the caller's own error survives by default");
        Assert.IsTrue(sw.Elapsed >= TimeSpan.FromMilliseconds(300), "it should have used its budget");
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(5), "and then stopped: " + sw.Elapsed);
    }

    [TestMethod]
    public void Run_LetsTheCallerReplaceTheGiveUpException() {
        // this is how FileOpenRetry reports a lock as a FileLockedException rather than a raw IOException
        var err = Assert.ThrowsException<TimeoutException>(() => Retry.Run<bool>(
            () => throw new InvalidOperationException("still busy"),
            _ => true, TimeSpan.FromMilliseconds(200),
            onExhausted: (last, attempts, elapsed) => new TimeoutException("gave up after " + attempts, last)));
        StringAssert.StartsWith(err.Message, "gave up after ");
        Assert.IsInstanceOfType(err.InnerException, typeof(InvalidOperationException), "the original cause is the useful half");
    }

    [TestMethod]
    public void Run_StopsRetryingWhenTheConditionTurnsOff() {
        // the server's auto-open uses this shape: a shutdown starting mid-wait ends the wait
        var calls = 0;
        var stopping = false;
        Assert.ThrowsException<InvalidOperationException>(() => Retry.Run<bool>(
            () => {
                calls++;
                if (calls >= 3) stopping = true;
                throw new InvalidOperationException("held");
            },
            _ => !stopping, TimeSpan.FromSeconds(30)));
        Assert.AreEqual(3, calls, "it must give up as soon as retrying stopped making sense");
    }

    [TestMethod]
    public void Run_NeverSleepsPastItsBudget() {
        var sw = Stopwatch.StartNew();
        Assert.ThrowsException<InvalidOperationException>(() => Retry.Run<bool>(
            () => throw new InvalidOperationException("busy"), _ => true, TimeSpan.FromMilliseconds(2500)));
        // without clamping the final sleep, the 2 s step would overshoot well past the budget
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(4), "overshot the budget: " + sw.Elapsed);
    }
}
