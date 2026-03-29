
//// TurboQuant.cs
//// .NET 8 compatible
////
//// A practical TurboQuant-style implementation:
////   - Random orthogonal rotation via signed Walsh-Hadamard transform
////   - Scalar quantization with Lloyd-Max codebook training
////   - Optional residual sign sketch (QJL-style) for improved dot products
////
//// Usage:
////   var tq = TurboQuant.Create(dimension: 256, bits: 3, residualProjections: 64, seed: 1234);
////   var enc = tq.Encode(vector);
////   var recon = tq.Decode(enc);
////   float dot = tq.ApproxDot(encA, encB);
//namespace Relatude.DB.DataStores.Indexes.VectorIndex1;

//public sealed class TurboQuant {
//    private readonly int _dimension;
//    private readonly int _bits;
//    private readonly int _residualProjections;

//    // Rotation config: diagonal Rademacher signs + Walsh-Hadamard
//    private readonly float[] _rotationSigns;

//    // Scalar quantizer
//    private readonly float[] _codebook;
//    private readonly float[] _thresholds;

//    // Residual sign sketch projection matrix (dense Gaussian)
//    private readonly float[][]? _residualProjection;

//    private TurboQuant(
//        int dimension,
//        int bits,
//        int residualProjections,
//        int seed,
//        float[] rotationSigns,
//        float[] codebook,
//        float[] thresholds,
//        float[][]? residualProjection) {
//        _dimension = dimension;
//        _bits = bits;
//        _residualProjections = residualProjections;
//        _rotationSigns = rotationSigns;
//        _codebook = codebook;
//        _thresholds = thresholds;
//        _residualProjection = residualProjection;
//    }

//    public static TurboQuant Create(
//        int dimension,
//        int bits = 3,
//        int residualProjections = 0,
//        int seed = 1234,
//        int lloydMaxTrainingSamples = 200_000,
//        int lloydMaxIterations = 30) {
//        if (dimension <= 0 || (dimension & (dimension - 1)) != 0)
//            throw new ArgumentException("Dimension must be a positive power of 2 for Walsh-Hadamard rotation.");
//        if (bits <= 0 || bits > 8)
//            throw new ArgumentException("bits must be between 1 and 8.");
//        if (residualProjections < 0)
//            throw new ArgumentException("residualProjections must be >= 0.");

//        var rng = new Random(seed);

//        var rotationSigns = new float[dimension];
//        for (int i = 0; i < dimension; i++)
//            rotationSigns[i] = rng.NextDouble() < 0.5 ? -1f : 1f;

//        // Train scalar quantizer on coordinates from random unit vectors after rotation.
//        // For random unit vectors, rotated coordinates have the same distribution.
//        var training = SampleSphereCoordinates(dimension, lloydMaxTrainingSamples, seed + 1);
//        var codebook = TrainLloydMax(training, 1 << bits, lloydMaxIterations);
//        Array.Sort(codebook);

//        var thresholds = new float[codebook.Length - 1];
//        for (int i = 0; i < thresholds.Length; i++)
//            thresholds[i] = 0.5f * (codebook[i] + codebook[i + 1]);

//        float[][]? residualProjection = null;
//        if (residualProjections > 0) {
//            residualProjection = CreateGaussianProjection(
//                rows: residualProjections,
//                cols: dimension,
//                seed: seed + 2);
//        }

//        return new TurboQuant(
//            dimension,
//            bits,
//            residualProjections,
//            seed,
//            rotationSigns,
//            codebook,
//            thresholds,
//            residualProjection);
//    }

//    public EncodedVector Encode(ReadOnlySpan<float> vector) {
//        if (vector.Length != _dimension)
//            throw new ArgumentException($"Expected vector length {_dimension}.");

//        var norm = L2Norm(vector);
//        if (norm == 0f) {
//            return new EncodedVector(
//                0f,
//                new byte[_dimension],
//                _residualProjections > 0 ? new byte[(_residualProjections + 7) / 8] : null
//                );
//        }

//        // Normalize to unit vector
//        var normalized = new float[_dimension];
//        for (int i = 0; i < _dimension; i++)
//            normalized[i] = vector[i] / norm;

//        // Rotate
//        var rotated = ApplyRandomHadamard(normalized);

//        // Quantize rotated coordinates
//        var indices = new byte[_dimension];
//        var reconstructedRotated = new float[_dimension];

//        for (int i = 0; i < _dimension; i++) {
//            int idx = QuantizeIndex(rotated[i]);
//            indices[i] = checked((byte)idx);
//            reconstructedRotated[i] = _codebook[idx];
//        }

//        // Optional residual sketch
//        byte[]? residualBits = null;
//        if (_residualProjections > 0 && _residualProjection is not null) {
//            var residual = new float[_dimension];
//            for (int i = 0; i < _dimension; i++)
//                residual[i] = rotated[i] - reconstructedRotated[i];

//            residualBits = ProjectToSignBits(residual, _residualProjection);
//        }

//        return new EncodedVector(norm, indices, residualBits);
//    }

//    public float[] Decode(EncodedVector encoded) {
//        //ValidateEncoded(encoded);

//        var rotatedRecon = new float[_dimension];
//        for (int i = 0; i < _dimension; i++)
//            rotatedRecon[i] = _codebook[encoded.Indices[i]];

//        var unitRecon = ApplyInverseRandomHadamard(rotatedRecon);

//        var output = new float[_dimension];
//        for (int i = 0; i < _dimension; i++)
//            output[i] = unitRecon[i] * encoded.Norm;

//        return output;
//    }

//    public float ApproxDot(EncodedVector a, EncodedVector b) {

//        // Stage 1: dot in rotated reconstructed space
//        float baseDot = 0f;
//        for (int i = 0; i < _dimension; i++) {
//            baseDot += _codebook[a.Indices[i]] * _codebook[b.Indices[i]];
//        }

//        float result = a.Norm * b.Norm * baseDot;

//        // Stage 2: optional residual sign-sketch correction
//        if (_residualProjections > 0 &&
//            a.ResidualBits is not null &&
//            b.ResidualBits is not null) {
//            int agreeMinusDisagree = 0;
//            for (int i = 0; i < _residualProjections; i++) {
//                bool sa = GetBit(a.ResidualBits, i);
//                bool sb = GetBit(b.ResidualBits, i);
//                agreeMinusDisagree += sa == sb ? 1 : -1;
//            }

//            // Scaled correction term. This is a practical estimator,
//            // not a proof-tight reproduction of the paper.
//            float correction = (float)agreeMinusDisagree / _residualProjections;
//            result += a.Norm * b.Norm * correction / 16f;
//        }

//        return result;
//    }

//    public float ReconstructionMse(IEnumerable<KeyValuePair<int, float[]>> vectors) {
//        double total = 0.0;
//        long count = 0;

//        foreach (var v in vectors) {
//            var enc = Encode(v.Value);
//            var dec = Decode(enc);
//            for (int i = 0; i < _dimension; i++) {
//                double diff = v.Value[i] - dec[i];
//                total += diff * diff;
//                count++;
//            }
//        }

//        return count == 0 ? 0f : (float)(total / count);
//    }
    
//    private int QuantizeIndex(float x) {
//        int lo = 0;
//        int hi = _thresholds.Length;

//        while (lo < hi) {
//            int mid = lo + ((hi - lo) >> 1);
//            if (x <= _thresholds[mid])
//                hi = mid;
//            else
//                lo = mid + 1;
//        }

//        return lo;
//    }

//    private float[] ApplyRandomHadamard(ReadOnlySpan<float> input) {
//        var buffer = new float[_dimension];
//        for (int i = 0; i < _dimension; i++)
//            buffer[i] = input[i] * _rotationSigns[i];

//        FastWalshHadamard(buffer);

//        float scale = 1f / MathF.Sqrt(_dimension);
//        for (int i = 0; i < _dimension; i++)
//            buffer[i] *= scale;

//        return buffer;
//    }

//    private float[] ApplyInverseRandomHadamard(ReadOnlySpan<float> input) {
//        // Hadamard is self-inverse up to scaling; since we use orthonormal scaling,
//        // inverse is the same transform with the same diagonal signs.
//        var buffer = input.ToArray();
//        FastWalshHadamard(buffer);

//        float scale = 1f / MathF.Sqrt(_dimension);
//        for (int i = 0; i < _dimension; i++)
//            buffer[i] *= scale * _rotationSigns[i];

//        return buffer;
//    }

//    private static void FastWalshHadamard(float[] data) {
//        int n = data.Length;
//        for (int len = 1; 2 * len <= n; len <<= 1) {
//            for (int i = 0; i < n; i += (len << 1)) {
//                for (int j = 0; j < len; j++) {
//                    float u = data[i + j];
//                    float v = data[i + j + len];
//                    data[i + j] = u + v;
//                    data[i + j + len] = u - v;
//                }
//            }
//        }
//    }

//    private static float L2Norm(ReadOnlySpan<float> v) {
//        double sum = 0;
//        for (int i = 0; i < v.Length; i++)
//            sum += v[i] * v[i];
//        return (float)Math.Sqrt(sum);
//    }

//    private static float[] SampleSphereCoordinates(int dimension, int sampleCount, int seed) {
//        var rng = new Random(seed);
//        var result = new float[sampleCount];

//        for (int s = 0; s < sampleCount; s++) {
//            var vec = new float[dimension];
//            double norm2 = 0;
//            for (int i = 0; i < dimension; i++) {
//                float g = NextGaussian(rng);
//                vec[i] = g;
//                norm2 += g * g;
//            }

//            float invNorm = 1f / (float)Math.Sqrt(norm2);
//            for (int i = 0; i < dimension; i++)
//                vec[i] *= invNorm;

//            result[s] = vec[rng.Next(dimension)];
//        }

//        return result;
//    }

//    private static float[] TrainLloydMax(float[] samples, int levels, int iterations) {
//        if (levels < 2)
//            throw new ArgumentException("levels must be >= 2.");

//        float min = samples.Min();
//        float max = samples.Max();

//        var centers = new float[levels];
//        for (int i = 0; i < levels; i++) {
//            float t = (float)i / (levels - 1);
//            centers[i] = min + t * (max - min);
//        }

//        for (int iter = 0; iter < iterations; iter++) {
//            var sums = new double[levels];
//            var counts = new int[levels];

//            var thresholds = new float[levels - 1];
//            for (int i = 0; i < thresholds.Length; i++)
//                thresholds[i] = 0.5f * (centers[i] + centers[i + 1]);

//            foreach (var x in samples) {
//                int idx = 0;
//                while (idx < thresholds.Length && x > thresholds[idx])
//                    idx++;

//                sums[idx] += x;
//                counts[idx]++;
//            }

//            for (int i = 0; i < levels; i++) {
//                if (counts[i] > 0)
//                    centers[i] = (float)(sums[i] / counts[i]);
//            }
//        }

//        return centers;
//    }

//    private static float[][] CreateGaussianProjection(int rows, int cols, int seed) {
//        var rng = new Random(seed);
//        var proj = new float[rows][];

//        float scale = 1f / MathF.Sqrt(rows);
//        for (int r = 0; r < rows; r++) {
//            proj[r] = new float[cols];
//            for (int c = 0; c < cols; c++)
//                proj[r][c] = NextGaussian(rng) * scale;
//        }

//        return proj;
//    }

//    private static byte[] ProjectToSignBits(ReadOnlySpan<float> vector, float[][] projection) {
//        int rows = projection.Length;
//        byte[] bits = new byte[(rows + 7) / 8];

//        for (int r = 0; r < rows; r++) {
//            float dot = 0f;
//            var row = projection[r];
//            for (int c = 0; c < vector.Length; c++)
//                dot += row[c] * vector[c];

//            bool sign = dot >= 0f;
//            if (sign)
//                bits[r >> 3] |= (byte)(1 << (r & 7));
//        }

//        return bits;
//    }

//    private static bool GetBit(byte[] bytes, int index) {
//        return (bytes[index >> 3] & (1 << (index & 7))) != 0;
//    }

//    private static float NextGaussian(Random rng) {
//        // Box-Muller
//        double u1 = 1.0 - rng.NextDouble();
//        double u2 = 1.0 - rng.NextDouble();
//        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
//    }

//    internal byte[] ToByteArray() {
//        // saving all variables needed to reconstruct the TurboQuant instance, including codebook and rotation config.
//        var ms = new MemoryStream();
//        using var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);

//        const int formatVersion = 1;
//        bw.Write(formatVersion);

//        bw.Write(_dimension);
//        bw.Write(_bits);
//        bw.Write(_residualProjections);

//        bw.Write(_rotationSigns.Length);
//        for (int i = 0; i < _rotationSigns.Length; i++)
//            bw.Write(_rotationSigns[i]);

//        bw.Write(_codebook.Length);
//        for (int i = 0; i < _codebook.Length; i++)
//            bw.Write(_codebook[i]);

//        bw.Write(_thresholds.Length);
//        for (int i = 0; i < _thresholds.Length; i++)
//            bw.Write(_thresholds[i]);

//        bool hasResidualProjection = _residualProjection is not null;
//        bw.Write(hasResidualProjection);
//        if (hasResidualProjection) {
//            bw.Write(_residualProjection!.Length);
//            for (int r = 0; r < _residualProjection.Length; r++) {
//                var row = _residualProjection[r];
//                bw.Write(row.Length);
//                for (int c = 0; c < row.Length; c++)
//                    bw.Write(row[c]);
//            }
//        }

//        bw.Flush();
//        return ms.ToArray();
//    }

//    internal static TurboQuant FromByteArray(byte[] bytes) {
//        if (bytes is null) throw new ArgumentNullException(nameof(bytes));

//        using var ms = new MemoryStream(bytes);
//        using var br = new BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: false);

//        const int expectedFormatVersion = 1;
//        int formatVersion = br.ReadInt32();
//        if (formatVersion != expectedFormatVersion)
//            throw new InvalidDataException($"Unsupported TurboQuant format version: {formatVersion}.");

//        int dimension = br.ReadInt32();
//        int bits = br.ReadInt32();
//        int residualProjections = br.ReadInt32();

//        int rotationLength = br.ReadInt32();
//        var rotationSigns = new float[rotationLength];
//        for (int i = 0; i < rotationLength; i++)
//            rotationSigns[i] = br.ReadSingle();

//        int codebookLength = br.ReadInt32();
//        var codebook = new float[codebookLength];
//        for (int i = 0; i < codebookLength; i++)
//            codebook[i] = br.ReadSingle();

//        int thresholdsLength = br.ReadInt32();
//        var thresholds = new float[thresholdsLength];
//        for (int i = 0; i < thresholdsLength; i++)
//            thresholds[i] = br.ReadSingle();

//        float[][]? residualProjection = null;
//        bool hasResidualProjection = br.ReadBoolean();
//        if (hasResidualProjection) {
//            int rowCount = br.ReadInt32();
//            residualProjection = new float[rowCount][];
//            for (int r = 0; r < rowCount; r++) {
//                int colCount = br.ReadInt32();
//                var row = new float[colCount];
//                for (int c = 0; c < colCount; c++)
//                    row[c] = br.ReadSingle();
//                residualProjection[r] = row;
//            }
//        }

//        if (ms.Position != ms.Length)
//            throw new InvalidDataException("TurboQuant payload contains trailing bytes.");

//        if (dimension <= 0)
//            throw new InvalidDataException("Invalid dimension in TurboQuant payload.");
//        if ((dimension & (dimension - 1)) != 0)
//            throw new InvalidDataException("Dimension must be a positive power of 2.");
//        if (bits <= 0 || bits > 8)
//            throw new InvalidDataException("Invalid quantization bits in TurboQuant payload.");
//        if (residualProjections < 0)
//            throw new InvalidDataException("Invalid residualProjections in TurboQuant payload.");
//        if (rotationSigns.Length != dimension)
//            throw new InvalidDataException("Rotation sign length does not match dimension.");
//        if (codebook.Length != (1 << bits))
//            throw new InvalidDataException("Codebook length does not match quantization bits.");
//        if (thresholds.Length != codebook.Length - 1)
//            throw new InvalidDataException("Threshold length does not match codebook length.");
//        if (residualProjections == 0 && residualProjection is not null)
//            throw new InvalidDataException("Residual projection data found, but residualProjections is 0.");
//        if (residualProjection is null && residualProjections > 0)
//            throw new InvalidDataException("Residual projections count indicates projection data, but none was found.");
//        if (residualProjection is not null) {
//            if (residualProjection.Length != residualProjections)
//                throw new InvalidDataException("Residual projection row count does not match residualProjections.");
//            for (int r = 0; r < residualProjection.Length; r++) {
//                if (residualProjection[r].Length != dimension)
//                    throw new InvalidDataException("Residual projection column count does not match dimension.");
//            }
//        }

//        return new TurboQuant(
//            dimension,
//            bits,
//            residualProjections,
//            seed: 0,
//            rotationSigns,
//            codebook,
//            thresholds,
//            residualProjection);
//    }
//}

//public sealed class EncodedVector {
//    public float Norm { get; }
//    public byte[] Indices { get; }
//    public byte[]? ResidualBits { get; }
//    public EncodedVector(float norm, byte[] indices, byte[]? residualBits) {
//        Norm = norm;
//        Indices = indices;
//        ResidualBits = residualBits;
//    }

//    public int ApproxCompressedBytes => sizeof(float) + Indices.Length + (ResidualBits?.Length ?? 0);

//    public byte[] ToByteArray() {
//        int residualLength = ResidualBits?.Length ?? 0;
//        byte[] data = new byte[4 + Indices.Length + residualLength];
//        BitConverter.GetBytes(Norm).CopyTo(data, 0);
//        Indices.CopyTo(data, 4);
//        if (ResidualBits != null)
//            ResidualBits.CopyTo(data, 4 + Indices.Length);
//        return data;
//    }
//    public static EncodedVector FromByteArray(byte[] data) {
//        float norm = BitConverter.ToSingle(data, 0);
//        int dimension = data.Length - 4; // assume no residual if length matches
//        byte[] indices = new byte[dimension];
//        Array.Copy(data, 4, indices, 0, dimension);
//        byte[]? residualBits = null;
//        if (data.Length > 4 + dimension) {
//            int residualLength = data.Length - 4 - dimension;
//            residualBits = new byte[residualLength];
//            Array.Copy(data, 4 + dimension, residualBits, 0, residualLength);
//        }
//        return new EncodedVector(norm, indices, residualBits);
//    }

//}
