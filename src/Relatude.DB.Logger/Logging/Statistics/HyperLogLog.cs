using Relatude.DB.Common;

namespace Relatude.DB.Logging.Statistics;
/// <summary>
/// "HyperLogLog" implementation for cardinality estimation
/// Enables estimating the number of unique elements in a multiset
/// With a low memory footprint at the cost of some accuracy
/// Uses hash functions and probabilistic counting
///
/// The registers are sixteen thousand ints, and a statistic keeps one of these per interval - per
/// second, among others - where most intervals see a handful of values. So until there are more
/// than <see cref="sparseLimit"/> of them the hashes are kept as they are, and the registers are
/// made only when that many have arrived (or when the state is saved). Nothing about the answer
/// changes: with that few values the estimate is the small range (linear counting) one, which needs
/// only how many registers are touched, and that is counted from the hashes directly - so an
/// estimate reads the same before and after the registers are made, and before and after a save.
/// </summary>
public class HyperLogLog {
    const int sparseLimit = 1024;
    readonly private double stdError, mapSize, alpha_m, k;
    readonly private int kComplement;
    private int[]? Lookup;
    private HashSet<uint>? _sparse;
    private const double pow_2_32 = 4294967296; // 2^32
    public HyperLogLog(byte[] state) {
        var bytes = CompressionUtility.Decompress(state);
        var mem = new MemoryStream(bytes);
        var br = new BinaryReader(mem);
        mapSize = br.ReadDouble();
        alpha_m = br.ReadDouble();
        k = br.ReadDouble();
        kComplement = br.ReadInt32();
        var count = br.ReadInt32();
        Lookup = new int[count];
        for (int i = 0; i < count; i++) {
            Lookup[i] = br.ReadInt32();
        }
    }
    public HyperLogLog() {
        stdError = 0.01; // hard coded for now, 8-16k values about 1% accuracy
        mapSize = (double)1.04 / stdError;
        k = (long)Math.Ceiling(log2(mapSize * mapSize));
        kComplement = 32 - (int)k;
        mapSize = (long)Math.Pow(2, k);
        alpha_m = mapSize == 16 ? (double)0.673
              : mapSize == 32 ? (double)0.697
              : mapSize == 64 ? (double)0.709
              : (double)0.7213 / (double)(1 + 1.079 / mapSize);
        _sparse = new();
    }
    public byte[] Serialize() {
        var lookup = Lookup ?? registersOf(_sparse!);
        var mem = new MemoryStream();
        var bw = new BinaryWriter(mem);
        bw.Write(mapSize);
        bw.Write(alpha_m);
        bw.Write(k);
        bw.Write(kComplement);
        bw.Write(lookup.Length);
        foreach (var i in lookup) {
            bw.Write(i);
        }
        var bytes = mem.ToArray();
        var compressed = CompressionUtility.Compress(bytes);
        return compressed;
    }
    private static double log2(double x) {
        return Math.Log(x) / 0.69314718055994530941723212145818;//Ln2
    }
    private static int getRank(uint hash, int max) {
        int r = 1;
        uint one = 1;
        while ((hash & one) == 0 && r <= max) {
            ++r;
            hash >>= 1;
        }
        return r;
    }
    public static uint getHashCode(string text) {
        uint hash = 0;
        for (int i = 0, l = text.Length; i < l; i++) {
            hash += text[i];
            hash += hash << 10;
            hash ^= hash >> 6;
        }
        hash += hash << 3;
        hash ^= hash >> 6;
        hash += hash << 16;
        return hash;
    }
    public int EstimateCount() {
        if (_sparse != null) {
            // the small range correction the registers would take, from the registers the hashes touch
            var touched = new HashSet<int>();
            foreach (var hash in _sparse) touched.Add((int)(hash >> kComplement));
            if (touched.Count == 0) return 0;
            return (int)(mapSize * Math.Log(mapSize / (mapSize - touched.Count)));
        }
        var lookup = Lookup!;
        double c = 0, E;

        for (var i = 0; i < mapSize; i++)
            c += Math.ScaleB(1d, -lookup[i]); // 1 / 2^register, without a Math.Pow per register

        E = alpha_m * mapSize * mapSize / c;

        // Make corrections & smoothen things.
        if (E <= 2.5 * mapSize) { // small range correction
            double V = 0;
            for (var i = 0; i < mapSize; i++)
                if (lookup[i] == 0) V++;
            if (V > 0)
                E = mapSize * Math.Log(mapSize / V);
        } else if (E > pow_2_32 / 30) { // large range correction
            E = -pow_2_32 * Math.Log(1 - E / pow_2_32);
        }

        return (int)E;
    }
    public void Add(string val) {
        uint hashCode = getHashCode(val);
        if (_sparse != null) {
            _sparse.Add(hashCode);
            if (_sparse.Count <= sparseLimit) return;
            Lookup = registersOf(_sparse);
            _sparse = null;
            return;
        }
        addToRegisters(Lookup!, hashCode);
    }
    int[] registersOf(HashSet<uint> hashes) {
        var lookup = new int[(int)mapSize];
        foreach (var hash in hashes) addToRegisters(lookup, hash);
        return lookup;
    }
    void addToRegisters(int[] lookup, uint hashCode) {
        int j = (int)(hashCode >> kComplement);
        lookup[j] = Math.Max(lookup[j], getRank(hashCode, kComplement));
    }
}
