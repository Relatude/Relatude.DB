using System.Diagnostics;
using Relatude.DB.Common;
using Relatude.DB.DataStores.Indexes.VectorIndex;

namespace Tests {
    [TestClass]
    public class VectorIndexes {

        int searchCount = 10;
        int dataCount = 1000;
        
        static List<Tuple<int, float[]>> set;
        //static FlatMemoryVectorIndex flat = new();
        static TurboQuantVectorIndex flat = new TurboQuantVectorIndex(1536);
        //static FlatDiskVectorIndex flat = new(File.Open("C:\\WAF_Temp\\flatdiskvectorindex.bin", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), 1536);

        [TestMethod]
        public void GenerateTestData() {
            set = getRandomEmbeddings(dataCount);
            foreach (var v in set) { flat.Set(v.Item1, v.Item2); }
        }
        [TestMethod]
        public void Search() {
            var r = new Random(dataCount);
            var v2 = getVector(r);
            List<VectorHit>? hits = null;
            var sw = Stopwatch.StartNew();
            for (uint i = 0; i < searchCount; i++) {
                hits = flat.Search(v2, 0, 10, 0);
            }
            sw.Stop();
            Console.WriteLine("Search time: " + sw.ElapsedMilliseconds.To1000N() + "ms");
            Console.WriteLine("Per search: " + (sw.Elapsed.TotalMilliseconds / searchCount).To1000N() + "ms");
            Console.WriteLine("Datasize: " + dataCount);
            Assert.IsNotNull(hits);
        }

        List<Tuple<int, float[]>> getRandomEmbeddings(int noNodes) {
            var set = new List<Tuple<int, float[]>>();
            var r = new Random(100);
            for (int nodeId = 0; nodeId < noNodes; nodeId++) {
                var vector = getVector(r);
                set.Add(new(nodeId, vector));
            }
            return set;
        }

        float[] getVector(Random r) {
            var v = new float[1536];
            for (int n = 0; n < v.Length; n++) {
                v[n] = (float)(float.MaxValue * 2.0 * (r.NextDouble() - 0.5));
            }
            return normalize(v);
        }

        static float[] normalize(float[] vector) {
            // Calculate the magnitude of the vector
            double magnitude = 0;
            for (int i = 0; i < vector.Length; i++) {
                magnitude += (double)vector[i] * vector[i];
            }
            magnitude = Math.Sqrt(magnitude);

            // If the magnitude is too large, throw an exception as further calculations will be inaccurate
            if (double.IsInfinity(magnitude) || double.IsNaN(magnitude)) throw new OverflowException("Magnitude is too large");

            // If the magnitude is close to zero, return the original vector to avoid division by zero:
            if (Math.Abs(magnitude) < 1e-10) return vector;

            // Normalize the vector
            float[] normalizedVector = new float[vector.Length];
            for (int i = 0; i < vector.Length; i++) {
                normalizedVector[i] = (float)(vector[i] / magnitude);
            }

            return normalizedVector;
        }

    }
}