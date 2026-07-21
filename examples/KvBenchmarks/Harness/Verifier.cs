using System.Text;
using Relatude.DB.Datastores.Indexes.BTreeIndex;

namespace KvBenchmarks.Harness;

/// <summary>
/// Correctness check: replays an identical random op stream into the native engine (the
/// reference) and a candidate engine, and compares the result of every ISortedIndex query
/// after each commit (and occasionally mid-transaction). A benchmark of a wrong index is
/// worthless, so this runs before any timing.
/// </summary>
public static class Verifier
{
    private const int Rounds = 24;
    private const int MutationsPerRound = 120;
    private const int IdSpace = 400;

    public static string? Run<T>(Scenario<T> scenario, string engineName, string dir) where T : notnull
    {
        var rnd = new Random(1234 + scenario.Name.GetHashCode(StringComparison.Ordinal));
        T[] pool = scenario.VerifyPool(rnd);
        Array.Sort(pool, (a, b) => OrderedCodec.Compare(OrderedCodec.EncodeValue(a), OrderedCodec.EncodeValue(b)));

        using var reference = (IDisposable)Engines.CreateReference();
        using var candidate = (IDisposable)Engines.Create(engineName, dir);
        var refEngine = (IStorageEngine)reference;
        var candEngine = (IStorageEngine)candidate;
        var refIdx = refEngine.OpenOrCreateIndex<T>("bench");
        var candIdx = candEngine.OpenOrCreateIndex<T>("bench");

        long ts = 0;
        for (int round = 0; round < Rounds; round++)
        {
            refEngine.BeginTransaction();
            candEngine.BeginTransaction();
            for (int m = 0; m < MutationsPerRound; m++)
            {
                int id = rnd.Next(IdSpace);
                if (rnd.NextDouble() < 0.75)
                {
                    T v = pool[rnd.Next(pool.Length)];
                    refIdx.Set(id, v);
                    candIdx.Set(id, v);
                }
                else
                {
                    bool a = refIdx.Remove(id);
                    bool b = candIdx.Remove(id);
                    if (a != b) return $"round {round}: Remove({id}) returned {a} (reference) vs {b} ({engineName})";
                }
            }
            string? err;
            if (round % 3 == 0 && (err = Battery(refIdx, candIdx, pool, rnd, $"round {round} (in txn)")) != null)
                return err;
            ts++;
            refEngine.CommitTransaction(ts, durable: false);
            candEngine.CommitTransaction(ts, durable: round % 8 == 7);
            if ((err = Battery(refIdx, candIdx, pool, rnd, $"round {round}")) != null)
                return err;
        }
        return null;
    }

    private static string? Battery<T>(ISortedIndex<T> a, ISortedIndex<T> b, T[] pool, Random rnd, string where) where T : notnull
    {
        if (a.Count != b.Count) return $"{where}: Count {a.Count} vs {b.Count}";
        if (a.DistinctValueCount != b.DistinctValueCount) return $"{where}: DistinctValueCount {a.DistinctValueCount} vs {b.DistinctValueCount}";

        for (int i = 0; i < 20; i++)
        {
            int id = rnd.Next(IdSpace);
            bool fa = a.TryGetValue(id, out T va);
            bool fb = b.TryGetValue(id, out T vb);
            if (fa != fb) return $"{where}: TryGetValue({id}) found {fa} vs {fb}";
            if (fa && !EqualityComparer<T>.Default.Equals(va, vb)) return $"{where}: GetValue({id}) '{va}' vs '{vb}'";
            if (a.ContainsKey(id) != b.ContainsKey(id)) return $"{where}: ContainsKey({id}) mismatch";
        }

        for (int i = 0; i < 10; i++)
        {
            T v = pool[rnd.Next(pool.Length)];
            if (a.ContainsValue(v) != b.ContainsValue(v)) return $"{where}: ContainsValue({v}) mismatch";
            string? err = CompareSeq($"{where}: GetIds({v})", a.GetIds(v), b.GetIds(v));
            if (err != null) return err;
        }

        if (a.Count > 0)
        {
            if (!EqualityComparer<T>.Default.Equals(a.GetMinValue(), b.GetMinValue())) return $"{where}: GetMinValue mismatch";
            if (!EqualityComparer<T>.Default.Equals(a.GetMaxValue(), b.GetMaxValue())) return $"{where}: GetMaxValue mismatch";
        }

        for (int i = 0; i < 12; i++)
        {
            int x = rnd.Next(pool.Length), y = rnd.Next(pool.Length);
            // Two ordered picks most of the time, but keep some reversed (empty) ranges in the mix.
            (T from, T to) = i < 10 && x > y ? (pool[y], pool[x]) : (pool[x], pool[y]);
            bool incFrom = rnd.Next(2) == 0, incTo = rnd.Next(2) == 0, desc = rnd.Next(2) == 0;
            string ctx = $"{where}: range [{from}..{to}] incFrom={incFrom} incTo={incTo} desc={desc}";
            string? err = CompareSeq($"{ctx} GetIdsInRange",
                a.GetIdsInRange(from, to, incFrom, incTo, desc), b.GetIdsInRange(from, to, incFrom, incTo, desc));
            if (err != null) return err;
            err = CompareEntries($"{ctx} GetEntriesInRange",
                a.GetEntriesInRange(from, to, incFrom, incTo, desc), b.GetEntriesInRange(from, to, incFrom, incTo, desc));
            if (err != null) return err;
            int ca = a.CountIdsInRange(from, to, incFrom, incTo), cb = b.CountIdsInRange(from, to, incFrom, incTo);
            if (ca != cb) return $"{ctx} CountIdsInRange {ca} vs {cb}";
        }

        for (int i = 0; i < 6; i++)
        {
            T v = pool[rnd.Next(pool.Length)];
            bool inc = rnd.Next(2) == 0, desc = rnd.Next(2) == 0;
            string? err = CompareSeq($"{where}: GetIdsGreaterThan({v},{inc},{desc})",
                a.GetIdsGreaterThan(v, inc, desc), b.GetIdsGreaterThan(v, inc, desc));
            if (err != null) return err;
            err = CompareSeq($"{where}: GetIdsSmallerThan({v},{inc},{desc})",
                a.GetIdsSmallerThan(v, inc, desc), b.GetIdsSmallerThan(v, inc, desc));
            if (err != null) return err;
            if (a.CountIdsGreaterThan(v, inc) != b.CountIdsGreaterThan(v, inc)) return $"{where}: CountIdsGreaterThan({v},{inc}) mismatch";
            if (a.CountIdsSmallerThan(v, inc) != b.CountIdsSmallerThan(v, inc)) return $"{where}: CountIdsSmallerThan({v},{inc}) mismatch";
        }

        string? e = CompareSeq($"{where}: Keys", a.Keys, b.Keys);
        if (e != null) return e;
        e = CompareEntries($"{where}: Entries", a.Entries, b.Entries);
        if (e != null) return e;
        e = CompareSeq($"{where}: DistinctValues", a.DistinctValues, b.DistinctValues, EqualityComparer<T>.Default);
        return e;
    }

    private static string? CompareSeq<TItem>(string what, IEnumerable<TItem> expected, IEnumerable<TItem> actual, IEqualityComparer<TItem>? eq = null)
    {
        eq ??= EqualityComparer<TItem>.Default;
        using var ea = expected.GetEnumerator();
        using var eb = actual.GetEnumerator();
        int i = 0;
        while (true)
        {
            bool ha = ea.MoveNext(), hb = eb.MoveNext();
            if (ha != hb) return $"{what}: length differs at element {i} (reference {(ha ? "has more" : "ended")})" + Tail(expected, actual);
            if (!ha) return null;
            if (!eq.Equals(ea.Current, eb.Current))
                return $"{what}: element {i} is '{ea.Current}' (reference) vs '{eb.Current}'" + Tail(expected, actual);
            i++;
        }
    }

    private static string? CompareEntries<T>(string what, IEnumerable<KeyValuePair<int, T>> expected, IEnumerable<KeyValuePair<int, T>> actual) where T : notnull
    {
        var cmp = EqualityComparer<T>.Default;
        using var ea = expected.GetEnumerator();
        using var eb = actual.GetEnumerator();
        int i = 0;
        while (true)
        {
            bool ha = ea.MoveNext(), hb = eb.MoveNext();
            if (ha != hb) return $"{what}: length differs at element {i}";
            if (!ha) return null;
            if (ea.Current.Key != eb.Current.Key || !cmp.Equals(ea.Current.Value, eb.Current.Value))
                return $"{what}: element {i} is ({ea.Current.Key},'{ea.Current.Value}') vs ({eb.Current.Key},'{eb.Current.Value}')";
            i++;
        }
    }

    private static string Tail<TItem>(IEnumerable<TItem> expected, IEnumerable<TItem> actual)
    {
        static string Fmt(IEnumerable<TItem> seq)
        {
            var sb = new StringBuilder("[");
            int n = 0;
            foreach (var x in seq)
            {
                if (n++ > 0) sb.Append(", ");
                if (n > 12) { sb.Append('…'); break; }
                sb.Append(x);
            }
            return sb.Append(']').ToString();
        }
        return $"\n    reference: {Fmt(expected)}\n    candidate: {Fmt(actual)}";
    }
}
