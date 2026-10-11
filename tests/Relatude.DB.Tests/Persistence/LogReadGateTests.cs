using Relatude.DB.DataStores.Stores;
using Relatude.DB.IO;

namespace Relatude.Persistence;

/// <summary>
/// The gate that keeps the log file in place while node bytes are read outside the store lock, and the
/// disk stream's reads at an offset, which run beside appends and flushes.
/// </summary>
[TestClass]
public class LogReadGateTests {
    static readonly TimeSpan wait = TimeSpan.FromSeconds(10);

    [TestMethod]
    public void ExclusiveWaitsForTheLeasesOut() {
        var gate = new LogReadGate();
        var lease = gate.EnterRead();
        var exclusive = Task.Run(() => {
            gate.EnterExclusive();
            gate.ExitExclusive();
        });
        Assert.IsFalse(exclusive.Wait(300), "the file was taken with a read in flight");
        lease.Dispose();
        Assert.IsTrue(exclusive.Wait(wait));
    }

    [TestMethod]
    public void AWaitingExclusiveDoesNotHoldUpNewLeases() {
        // unlike the store lock: a reader that already holds a lease and takes another (a nested read)
        // must not wait behind a writer that is itself waiting for that first lease
        var gate = new LogReadGate();
        using var first = gate.EnterRead();
        var exclusive = Task.Run(() => {
            gate.EnterExclusive();
            gate.ExitExclusive();
        });
        Thread.Sleep(100); // let it start waiting
        var nested = Task.Run(() => gate.EnterRead());
        Assert.IsTrue(nested.Wait(wait), "a lease waited behind a waiting exclusive taker");
        nested.Result.Dispose();
        first.Dispose();
        Assert.IsTrue(exclusive.Wait(wait));
    }

    [TestMethod]
    public void AHeldExclusiveHoldsUpNewLeases_ButNotItsOwnThread() {
        var gate = new LogReadGate();
        gate.EnterExclusive();
        var lease = Task.Run(() => gate.EnterRead());
        Assert.IsFalse(lease.Wait(300), "a lease was handed out while the file was taken");
        using (gate.EnterRead()) { } // the owner itself reads freely
        gate.EnterExclusive(); // and takes it again
        gate.ExitExclusive();
        gate.ExitExclusive();
        Assert.IsTrue(lease.Wait(wait));
        lease.Result.Dispose();
    }

    [TestMethod]
    public void TakingTheFileWhileReadingFromItThrowsInsteadOfHanging() {
        var gate = new LogReadGate();
        using var lease = gate.EnterRead();
        Assert.Throws<InvalidOperationException>(() => gate.EnterExclusive());
    }

    [TestMethod]
    public void ALeaseGivenBackOnAnotherThreadIsCountedRight() {
        var gate = new LogReadGate();
        var lease = gate.EnterRead();
        Task.Run(() => lease.Dispose()).Wait();
        lease.Dispose(); // twice: nothing
        Assert.AreEqual(0, gate.LeasesOut);
        gate.EnterExclusive(); // this thread holds nothing any more
        gate.ExitExclusive();
    }

    [TestMethod]
    public void CloseRefusesNewLeasesAndWaitsForTheOnesOut() {
        var gate = new LogReadGate();
        var lease = gate.EnterRead();
        var closing = Task.Run(() => gate.Close(10_000));
        Thread.Sleep(100);
        Assert.Throws<ObjectDisposedException>(() => Task.Run(() => gate.EnterRead()).GetAwaiter().GetResult());
        Assert.IsFalse(closing.IsCompleted);
        lease.Dispose();
        Assert.IsTrue(closing.Wait(wait));
        Assert.IsTrue(closing.Result);
        Assert.IsTrue(new LogReadGate().Close(0), "a gate with nothing out closes at once");
    }

    [TestMethod]
    public void CloseGivesUpAfterItsTimeout() {
        var gate = new LogReadGate();
        var lease = Task.Run(() => gate.EnterRead()).Result; // out on another thread, never given back here
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.IsFalse(gate.Close(200));
        Assert.IsTrue(sw.ElapsedMilliseconds < 5000);
        lease.Dispose();
    }

    // ---- the disk stream: reads at an offset beside appends and fsyncs ----

    [TestMethod]
    public void DiskStreamReadsRightBesideAppendsAndFlushes() {
        var folder = Path.Combine(Path.GetTempPath(), "relatude-streamreads-" + Guid.NewGuid().ToString("N"));
        try {
            var io = new IOProviderDisk(folder);
            using var stream = io.OpenAppend(["log.bin"]);
            // record i is 64 bytes, each one byte (i % 251) repeated, at position i * 64
            const int recordSize = 64;
            var written = 0;
            var stop = false;
            var errors = new List<string>();
            var writer = Task.Run(() => {
                for (var i = 0; i < 20_000; i++) {
                    var record = new byte[recordSize];
                    Array.Fill(record, (byte)(i % 251));
                    stream.Append(record);
                    Volatile.Write(ref written, i + 1); // readable from here: the stream pushes its buffer down for a read past what it flushed
                    if (i % 2000 == 0) stream.Flush(true);
                }
                stream.Flush(false);
            });
            var readers = Enumerable.Range(0, 4).Select(r => Task.Run(() => {
                var random = new Random(r);
                var buffer = new byte[recordSize];
                while (!Volatile.Read(ref stop)) {
                    var n = Volatile.Read(ref written);
                    if (n == 0) continue;
                    var i = random.Next(Math.Max(0, n - 50), n); // mostly the newest, still in the stream's buffer
                    if (random.Next(4) == 0) i = random.Next(n); // and some anywhere
                    stream.Get((long)i * recordSize, recordSize, buffer);
                    if (buffer.Any(b => b != (byte)(i % 251))) lock (errors) errors.Add("record " + i + " read wrong");
                }
            })).ToArray();
            Assert.IsTrue(writer.Wait(TimeSpan.FromSeconds(60)));
            Volatile.Write(ref stop, true);
            Task.WaitAll(readers, TimeSpan.FromSeconds(30));
            Assert.AreEqual(0, errors.Count, string.Join("; ", errors.Take(5)));
            Assert.AreEqual(20_000L * recordSize, stream.Length);
            // past the end is refused, as before
            Assert.Throws<EndOfStreamException>(() => stream.Get(stream.Length - 10, 20, new byte[20]));
            Assert.Throws<ArgumentOutOfRangeException>(() => stream.Get(stream.Length, 1, new byte[1]));
        } finally {
            try { Directory.Delete(folder, true); } catch { }
        }
    }
}
