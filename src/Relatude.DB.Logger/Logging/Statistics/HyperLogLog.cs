using Relatude.DB.Common;

namespace Relatude.DB.Logging.Statistics;
class LookUpPool {
    static readonly Stack<byte[]> _pool = new();
    public static byte[] Get() {
        lock (_pool) {
            if (_pool.Count > 0) {
                return _pool.Pop();
            } else {
                return new byte[1024];
            }
        }
    }
    public static void Return(byte[] bytes) {
        lock (_pool) {
            _pool.Push(bytes);
        }
    }
}
public class HyperLogLog {
    readonly private double stdError, mapSize, alpha_m, k;
    readonly private int kComplement;
    readonly private int[] Lookup;
    private const double pow_2_32 = 4294967297;
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
        Lookup = new int[(int)mapSize];
    }
    public byte[] Serialize() {
        var mem = new MemoryStream();
        var bw = new BinaryWriter(mem);
        bw.Write(mapSize);
        bw.Write(alpha_m);
        bw.Write(k);
        bw.Write(kComplement);
        bw.Write(Lookup.Length);
        foreach (var i in Lookup) {
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
        double c = 0, E;

        for (var i = 0; i < mapSize; i++)
            c += 1d / Math.Pow(2, Lookup[i]);

        E = alpha_m * mapSize * mapSize / c;

        // Make corrections & smoothen things. 
        if (E <= 5 / 2 * mapSize) {
            double V = 0;
            for (var i = 0; i < mapSize; i++)
                if (Lookup[i] == 0) V++;
            if (V > 0)
                E = mapSize * Math.Log(mapSize / V);
        } else
            if (E > 1 / 30 * pow_2_32)
            E = -pow_2_32 * Math.Log(1 - E / pow_2_32);
        // Made corrections & smoothen things, or not. 

        return (int)E;
    }
    public void Add(string val) {
        uint hashCode = getHashCode(val);
        int j = (int)(hashCode >> kComplement);
        Lookup[j] = Math.Max(Lookup[j], getRank(hashCode, kComplement));
    }
}

//public class HyperLogLog2 {
//    CardinalityEstimator _lib;
//    public HyperLogLog2() {
//        _lib = new CardinalityEstimator(16);
//    }
//    public HyperLogLog2(byte[] state) {
//        MemoryStream ms = new MemoryStream(state);
//        BinaryFormatter bf = new BinaryFormatter();
//#pragma warning disable SYSLIB0011 // BinaryFormatter.Deserialize is obsolete
//        _lib = (CardinalityEstimator)bf.Deserialize(ms);
//#pragma warning restore SYSLIB0011
//    }
//    public byte[] Serialize() {
//        MemoryStream ms = new MemoryStream();
//        BinaryFormatter bf = new BinaryFormatter();
//#pragma warning disable SYSLIB0011 // BinaryFormatter.Serialize is obsolete
//        bf.Serialize(ms, _lib);
//#pragma warning restore SYSLIB0011
//        return ms.ToArray();
//    }
//    public int EstimateCount() => (int)_lib.Count();
//    public void Add(string val) {
//        _lib.Add(val);
//    }
//}