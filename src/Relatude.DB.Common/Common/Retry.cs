using System.Diagnostics;
namespace Relatude.DB.Common;

/// <summary>
/// The one place the retry cadence is defined. Everything in the database that waits and tries a
/// failed operation again goes through here, so that "how often do we retry?" has a single answer
/// instead of one per call site.
/// <para>The schedule doubles from <see cref="FirstDelayMs"/> and then holds at
/// <see cref="MaxDelayMs"/>: 100 ms, 200, 400, 800, 1600, then every 2 s. That shape is chosen for
/// what these retries are actually waiting for - a host handing over from one process to the next.
/// Most such waits are over in well under a second, which the fast start catches; the ones that are
/// not run for tens of seconds, where a tight poll would only burn syscalls. The cost of the cap is
/// that success is noticed up to <see cref="MaxDelayMs"/> late.</para>
/// <para>This class owns the cadence and the budget only. What counts as worth retrying, and what to
/// say about it, stay with the caller - a file lock and an HTTP 503 need the same rhythm but not the
/// same words.</para>
/// <para>Polling loops that wait for a condition rather than retry a failed operation - draining
/// requests, waiting for an open to finish - deliberately do not use this: they need to notice the
/// condition immediately, not on a 2 second beat.</para>
/// </summary>
public static class Retry {

    /// <summary>The first wait, doubled on each further attempt.</summary>
    public const int FirstDelayMs = 100;
    /// <summary>The ceiling the doubling stops at, and the steady-state poll interval.</summary>
    public const int MaxDelayMs = 2000;
    /// <summary>How many times the delay doubles before it reaches <see cref="MaxDelayMs"/>.</summary>
    const int doublings = 5;

    /// <summary>
    /// How long to wait after <paramref name="failedAttempts"/> failures (1 after the first failure).
    /// 100 ms, 200, 400, 800, 1600, then 2000 for every attempt after that.
    /// </summary>
    public static TimeSpan DelayAfter(int failedAttempts) {
        if (failedAttempts < 1) failedAttempts = 1;
        var doubled = FirstDelayMs * (1 << Math.Min(failedAttempts - 1, doublings));
        return TimeSpan.FromMilliseconds(Math.Min(MaxDelayMs, doubled));
    }

    /// <summary>The first wait when contending for an in-process lock, doubled on each further attempt.</summary>
    public const int FirstContentionDelayMs = 5;
    /// <summary>The ceiling the contention doubling stops at.</summary>
    public const int MaxContentionDelayMs = 1000;
    /// <summary>How many times a transaction retries a locked node before giving up: about 7.3 s in total.</summary>
    public const int ContentionAttempts = 14;
    const int contentionDoublings = 8;

    /// <summary>
    /// How long to wait after <paramref name="failedAttempts"/> failures when the thing being waited
    /// for is an in-process lock: 5 ms, 10, 20, 40, 80, 160, 320, 640, then 1 s.
    /// <para>Deliberately far faster off the mark than <see cref="DelayAfter"/>, because it is waiting
    /// on something quite different. A host handover takes seconds and cannot be hurried; a write lock
    /// held by another transaction on this machine is usually gone within a millisecond or two, and
    /// making that caller sit out a 100 ms first step would cost real throughput on contended
    /// writes.</para>
    /// </summary>
    public static TimeSpan DelayAfterContention(int failedAttempts) {
        if (failedAttempts < 1) failedAttempts = 1;
        var doubled = FirstContentionDelayMs * (1 << Math.Min(failedAttempts - 1, contentionDoublings));
        return TimeSpan.FromMilliseconds(Math.Min(MaxContentionDelayMs, doubled));
    }

    /// <summary>
    /// Runs <paramref name="action"/>, and while it fails with something
    /// <paramref name="isTransient"/> accepts, waits per <see cref="DelayAfter"/> and runs it again
    /// until <paramref name="timeout"/> is spent. Anything else is rethrown untouched, on the attempt
    /// it happened.
    /// </summary>
    /// <param name="onWaitStarted">Called once, on the first transient failure, with the attempt count
    /// and the error. A wait that is never reported is indistinguishable from a hang.</param>
    /// <param name="onWaitEnded">Called when a retried attempt finally succeeds, with the attempt count
    /// and how long the waiting took.</param>
    /// <param name="onExhausted">Builds the exception thrown when the budget runs out. Defaults to
    /// rethrowing the last failure as it was.</param>
    public static T Run<T>(
        Func<T> action,
        Func<Exception, bool> isTransient,
        TimeSpan timeout,
        Action<int, Exception>? onWaitStarted = null,
        Action<int, TimeSpan>? onWaitEnded = null,
        Func<Exception, int, TimeSpan, Exception>? onExhausted = null) {
        var sw = Stopwatch.StartNew();
        var attempts = 0;
        while (true) {
            attempts++;
            try {
                var result = action();
                if (attempts > 1) onWaitEnded?.Invoke(attempts, sw.Elapsed);
                return result;
            } catch (Exception err) when (isTransient(err)) {
                var remaining = timeout - sw.Elapsed;
                if (remaining <= TimeSpan.Zero) {
                    if (onExhausted == null) throw;
                    throw onExhausted(err, attempts, sw.Elapsed);
                }
                if (attempts == 1) onWaitStarted?.Invoke(attempts, err);
                var delay = DelayAfter(attempts);
                if (delay > remaining) delay = remaining;
                Thread.Sleep(delay);
            }
        }
    }

    /// <summary>
    /// Same as <see cref="Run{T}"/>, awaiting the operation and the wait between attempts instead of
    /// blocking the thread.
    /// </summary>
    public static async Task<T> RunAsync<T>(
        Func<Task<T>> action,
        Func<Exception, bool> isTransient,
        TimeSpan timeout,
        Action<int, Exception>? onWaitStarted = null,
        Action<int, TimeSpan>? onWaitEnded = null,
        Func<Exception, int, TimeSpan, Exception>? onExhausted = null,
        CancellationToken cancellationToken = default) {
        var sw = Stopwatch.StartNew();
        var attempts = 0;
        while (true) {
            attempts++;
            try {
                var result = await action();
                if (attempts > 1) onWaitEnded?.Invoke(attempts, sw.Elapsed);
                return result;
            } catch (Exception err) when (isTransient(err)) {
                var remaining = timeout - sw.Elapsed;
                if (remaining <= TimeSpan.Zero) {
                    if (onExhausted == null) throw;
                    throw onExhausted(err, attempts, sw.Elapsed);
                }
                if (attempts == 1) onWaitStarted?.Invoke(attempts, err);
                var delay = DelayAfter(attempts);
                if (delay > remaining) delay = remaining;
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    /// <summary>Same as <see cref="RunAsync{T}"/>, for an operation that returns nothing.</summary>
    public static Task RunAsync(
        Func<Task> action,
        Func<Exception, bool> isTransient,
        TimeSpan timeout,
        Action<int, Exception>? onWaitStarted = null,
        Action<int, TimeSpan>? onWaitEnded = null,
        Func<Exception, int, TimeSpan, Exception>? onExhausted = null,
        CancellationToken cancellationToken = default) {
        return RunAsync(async () => { await action(); return true; }, isTransient, timeout,
            onWaitStarted, onWaitEnded, onExhausted, cancellationToken);
    }

    /// <summary>Same as <see cref="Run{T}"/>, for an operation that returns nothing.</summary>
    public static void Run(
        Action action,
        Func<Exception, bool> isTransient,
        TimeSpan timeout,
        Action<int, Exception>? onWaitStarted = null,
        Action<int, TimeSpan>? onWaitEnded = null,
        Func<Exception, int, TimeSpan, Exception>? onExhausted = null) {
        Run(() => { action(); return true; }, isTransient, timeout, onWaitStarted, onWaitEnded, onExhausted);
    }
}
