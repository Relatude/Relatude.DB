using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Http;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.UI;

namespace Relatude.Server;

/// <summary>
/// The batched half of the folder download (UIFileTransfer, <c>ui/download-batch</c>), which opens
/// files - and reads the small ones whole - several ahead of the one it is writing. Whatever order
/// the opens finish in, the frames must come out in the order the keys were asked for, each with the
/// right bytes, and a file that cannot be read must be reported in its place without stopping the
/// ones after it.
/// </summary>
[TestClass]
public class UIDownloadBatchTests {

    [TestMethod]
    public async Task FramesComeInTheOrderAskedWithTheRightBytesAndFailuresInTheirPlace() {
        var root = Path.Combine(Path.GetTempPath(), "relatude-download-batch-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "batch");
        Directory.CreateDirectory(folder);
        var host = TestServerHost.Start(root);
        // the admin API - and with it the UI and its file transfers - is mapped by the app's own
        // UseRelatudeDB, which goes through the static runtime; the test host maps it directly
        typeof(RelatudeDBServer).GetMethod("MapAdminAPI", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host.Server, [host.App]);
        FileStream? held = null;
        try {
            var random = new Random(7);
            var expected = new List<(string Key, byte[]? Bytes, string? Failure)>();
            byte[] write(string name, int size) {
                var bytes = new byte[size];
                random.NextBytes(bytes);
                File.WriteAllBytes(Path.Combine(folder, name), bytes);
                return bytes;
            }
            // small files read whole ahead of their turn, bigger ones streamed when it comes, an
            // empty one, a missing one and one held open for writing, mixed so the read-ahead window
            // is full of both kinds at once
            for (var i = 0; i < 25; i++) expected.Add(($"batch/small-{i:00}.bin", write($"small-{i:00}.bin", random.Next(0, 20_000)), null));
            expected.Insert(3, ("batch/big-1.bin", write("big-1.bin", 700_000), null));
            expected.Insert(10, ("batch/empty.bin", write("empty.bin", 0), null));
            expected.Insert(12, ("batch/missing.bin", null, "The file was not found. "));
            write("held.bin", 1000);
            held = new FileStream(Path.Combine(folder, "held.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            expected.Insert(15, ("batch/held.bin", null, "The file is in use. "));
            expected.Add(("batch/big-2.bin", write("big-2.bin", 300_000), null));

            var frames = await downloadBatch(host, RelatudeDBServer.ProjectRootIOId, expected.Select(e => e.Key).ToArray());

            Assert.AreEqual(expected.Count, frames.Count, "one frame per key asked for");
            for (var i = 0; i < expected.Count; i++) {
                var (key, bytes, failure) = expected[i];
                Assert.AreEqual(key, frames[i].Name, $"frame {i} is out of order");
                if (failure != null) {
                    Assert.IsFalse(frames[i].Ok, key + " should have been reported, not sent");
                    Assert.AreEqual(failure, Encoding.UTF8.GetString(frames[i].Bytes));
                } else {
                    Assert.IsTrue(frames[i].Ok, key + " failed: " + Encoding.UTF8.GetString(frames[i].Bytes));
                    CollectionAssert.AreEqual(bytes, frames[i].Bytes, key + " arrived with other bytes than it has");
                }
            }
        } finally {
            held?.Dispose();
            await host.DisposeAsync();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // UIFileTransfer is internal and its routes are mapped by the app's own UseRelatudeDB, so the
    // handler is called the way the route would call it, and the response read off a memory stream
    static async Task<List<(string Name, bool Ok, byte[] Bytes)>> downloadBatch(TestServerHost host, Guid ioId, string[] keys) {
        var transfer = typeof(UIServer).GetField("_transfer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host.Server.UI)!;
        var handler = transfer.GetType().GetMethod("downloadBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var payloadType = typeof(UIServer).Assembly.GetType("Relatude.DB.NodeServer.UI.DownloadBatchPayload")!;
        var http = new DefaultHttpContext();
        var body = new MemoryStream();
        http.Response.Body = body;
        await (Task<IResult>)handler.Invoke(transfer, [http, Activator.CreateInstance(payloadType, ioId, keys)])!;
        var bytes = body.ToArray();
        var frames = new List<(string, bool, byte[])>();
        var at = 0;
        while (at < bytes.Length) {
            var nameLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));
            at += 4;
            var name = Encoding.UTF8.GetString(bytes, at, nameLength);
            at += nameLength;
            var ok = bytes[at] == 1;
            at += 1;
            var length = (int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at));
            at += 8;
            frames.Add((name, ok, bytes[at..(at + length)]));
            at += length;
        }
        return frames;
    }
}
