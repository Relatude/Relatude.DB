using Relatude.DB.Datastores.Indexes.BTreeIndex;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ZoneTree.Collections.BTree;

namespace KvBenchmarks;

internal class Test {
    public static void Run() {
        var temp = Path.GetTempPath();
        var dbPath = Path.Combine(temp, "KvBenchmarks");
        if (File.Exists(dbPath)) File.Delete(dbPath);

        var engine = new BPlusTreeStorageEngine(dbPath, new() {
            ValueCacheEntries = 0, 
            PageCacheBytes = 1024 * 1024 * 1024 // 1 GB page cache
        });

        var index1 = engine.OpenOrCreateIntHashIndex<Guid>("index1");
        var index2 = new Dictionary<int, Guid>();

        var sw = new System.Diagnostics.Stopwatch();
        engine.BeginTransaction();
        sw.Start();
        for (int i = 0; i < 10000000; i++) {
            var guid = Guid.NewGuid();
            index1.Set(i, guid);
        }
        sw.Stop();
        engine.CommitTransaction(10, true);
        Console.WriteLine("Time taken to insert 10,000,000 items into BPlusTreeStorageEngine: " + sw.ElapsedMilliseconds + " ms");
        sw.Restart();
        for (int i = 0; i < 10000000; i++) {
            var guid = Guid.NewGuid();
            index2.Add(i, guid);
        }
        sw.Stop();
        Console.WriteLine("Time taken to insert 10,000,000 items into Dictionary: " + sw.ElapsedMilliseconds + " ms");

        // lookup test:

        sw.Restart();
        for (int i = 0; i < 10000000; i++) {
            var guid = index1.TryGetValue(i, out var value) ? value : Guid.Empty;
        }
        sw.Stop();
        Console.WriteLine("Time taken to lookup 10,000,000 items from BPlusTreeStorageEngine: " + sw.ElapsedMilliseconds + " ms");

        sw.Restart();
        for (int i = 0; i < 10000000; i++) {
            var guid = index2.TryGetValue(i, out var value) ? value : Guid.Empty;
        }
        sw.Stop();
        Console.WriteLine("Time taken to lookup 10,000,000 items from Dictionary: " + sw.ElapsedMilliseconds + " ms");



    }
}
