
// TurboQuant.cs
// .NET 8 compatible
//
// A practical TurboQuant-style implementation:
//   - Random orthogonal rotation via signed Walsh-Hadamard transform
//   - Scalar quantization with Lloyd-Max codebook training
//   - Optional residual sign sketch (QJL-style) for improved dot products
//
// Usage:
//   var tq = TurboQuant.Create(dimension: 256, bits: 3, residualProjections: 64, seed: 1234);
//   var enc = tq.Encode(vector);
//   var recon = tq.Decode(enc);
//   float dot = tq.ApproxDot(encA, encB);

public sealed class TurboQuant {
    private readonly int _dimension;
    private readonly int _bits;
    private readonly int _levels;
    private readonly int _residualProjections;
    private readonly int _seed;

    // Rotation config: diagonal Rademacher signs + Walsh-Hadamard
    private readonly float[] _rotationSigns;

    // Scalar quantizer
    private readonly float[] _codebook;
    private readonly float[] _thresholds;

    // Residual sign sketch projection matrix (dense Gaussian)
    private readonly float[][]? _residualProjection;

    private TurboQuant(
        int dimension,
        int bits,
        int residualProjections,
        int seed,
        float[] rotationSigns,
        float[] codebook,
        float[] thresholds,
        float[][]? residualProjection) {
        _dimension = dimension;
        _bits = bits;
        _levels = 1 << bits;
        _residualProjections = residualProjections;
        _seed = seed;
        _rotationSigns = rotationSigns;
        _codebook = codebook;
        _thresholds = thresholds;
        _residualProjection = residualProjection;
    }

    public int Dimension => _dimension;
    public int Bits => _bits;
    public int Levels => _levels;
    public int ResidualProjections => _residualProjections;

    public static TurboQuant Create(
        int dimension,
        int bits = 3,
        int residualProjections = 0,
        int seed = 1234,
        int lloydMaxTrainingSamples = 200_000,
        int lloydMaxIterations = 30) {
        if (dimension <= 0 || (dimension & (dimension - 1)) != 0)
            throw new ArgumentException("Dimension must be a positive power of 2 for Walsh-Hadamard rotation.");
        if (bits <= 0 || bits > 8)
            throw new ArgumentException("bits must be between 1 and 8.");
        if (residualProjections < 0)
            throw new ArgumentException("residualProjections must be >= 0.");

        var rng = new Random(seed);

        var rotationSigns = new float[dimension];
        for (int i = 0; i < dimension; i++)
            rotationSigns[i] = rng.NextDouble() < 0.5 ? -1f : 1f;

        // Train scalar quantizer on coordinates from random unit vectors after rotation.
        // For random unit vectors, rotated coordinates have the same distribution.
        var training = SampleSphereCoordinates(dimension, lloydMaxTrainingSamples, seed + 1);
        var codebook = TrainLloydMax(training, 1 << bits, lloydMaxIterations);
        Array.Sort(codebook);

        var thresholds = new float[codebook.Length - 1];
        for (int i = 0; i < thresholds.Length; i++)
            thresholds[i] = 0.5f * (codebook[i] + codebook[i + 1]);

        float[][]? residualProjection = null;
        if (residualProjections > 0) {
            residualProjection = CreateGaussianProjection(
                rows: residualProjections,
                cols: dimension,
                seed: seed + 2);
        }

        return new TurboQuant(
            dimension,
            bits,
            residualProjections,
            seed,
            rotationSigns,
            codebook,
            thresholds,
            residualProjection);
    }

    public EncodedVector Encode(ReadOnlySpan<float> vector, int nodeId) {
        if (vector.Length != _dimension)
            throw new ArgumentException($"Expected vector length {_dimension}.");

        var norm = L2Norm(vector);
        if (norm == 0f) {
            return new EncodedVector(
                _dimension,
                _bits,
                0f,
                new byte[_dimension],
                _residualProjections > 0 ? new byte[(_residualProjections + 7) / 8] : null,
                nodeId
                );
        }

        // Normalize to unit vector
        var normalized = new float[_dimension];
        for (int i = 0; i < _dimension; i++)
            normalized[i] = vector[i] / norm;

        // Rotate
        var rotated = ApplyRandomHadamard(normalized);

        // Quantize rotated coordinates
        var indices = new byte[_dimension];
        var reconstructedRotated = new float[_dimension];

        for (int i = 0; i < _dimension; i++) {
            int idx = QuantizeIndex(rotated[i]);
            indices[i] = checked((byte)idx);
            reconstructedRotated[i] = _codebook[idx];
        }

        // Optional residual sketch
        byte[]? residualBits = null;
        if (_residualProjections > 0 && _residualProjection is not null) {
            var residual = new float[_dimension];
            for (int i = 0; i < _dimension; i++)
                residual[i] = rotated[i] - reconstructedRotated[i];

            residualBits = ProjectToSignBits(residual, _residualProjection);
        }

        return new EncodedVector(_dimension, _bits, norm, indices, residualBits, nodeId);
    }

    public float[] Decode(EncodedVector encoded) {
        ValidateEncoded(encoded);

        var rotatedRecon = new float[_dimension];
        for (int i = 0; i < _dimension; i++)
            rotatedRecon[i] = _codebook[encoded.Indices[i]];

        var unitRecon = ApplyInverseRandomHadamard(rotatedRecon);

        var output = new float[_dimension];
        for (int i = 0; i < _dimension; i++)
            output[i] = unitRecon[i] * encoded.Norm;

        return output;
    }

    public float ApproxDot(EncodedVector a, EncodedVector b) {
        ValidateEncoded(a);
        ValidateEncoded(b);

        // Stage 1: dot in rotated reconstructed space
        float baseDot = 0f;
        for (int i = 0; i < _dimension; i++) {
            baseDot += _codebook[a.Indices[i]] * _codebook[b.Indices[i]];
        }

        float result = a.Norm * b.Norm * baseDot;

        // Stage 2: optional residual sign-sketch correction
        if (_residualProjections > 0 &&
            a.ResidualBits is not null &&
            b.ResidualBits is not null) {
            int agreeMinusDisagree = 0;
            for (int i = 0; i < _residualProjections; i++) {
                bool sa = GetBit(a.ResidualBits, i);
                bool sb = GetBit(b.ResidualBits, i);
                agreeMinusDisagree += sa == sb ? 1 : -1;
            }

            // Scaled correction term. This is a practical estimator,
            // not a proof-tight reproduction of the paper.
            float correction = (float)agreeMinusDisagree / _residualProjections;
            result += a.Norm * b.Norm * correction / 16f;
        }

        return result;
    }

    public float ReconstructionMse(IEnumerable<KeyValuePair<int, float[]>> vectors) {
        double total = 0.0;
        long count = 0;

        foreach (var v in vectors) {
            var enc = Encode(v.Value, v.Key);
            var dec = Decode(enc);
            for (int i = 0; i < _dimension; i++) {
                double diff = v.Value[i] - dec[i];
                total += diff * diff;
                count++;
            }
        }

        return count == 0 ? 0f : (float)(total / count);
    }





    private void ValidateEncoded(EncodedVector encoded) {
        if (encoded.Dimension != _dimension)
            throw new ArgumentException("Encoded vector dimension mismatch.");
        if (encoded.Bits != _bits)
            throw new ArgumentException("Encoded vector bit-width mismatch.");
        if (encoded.Indices.Length != _dimension)
            throw new ArgumentException("Encoded vector indices length mismatch.");
        if (_residualProjections > 0) {
            if (encoded.ResidualBits is null)
                throw new ArgumentException("Residual bits are required for this TurboQuant instance.");
            int expectedBytes = (_residualProjections + 7) / 8;
            if (encoded.ResidualBits.Length != expectedBytes)
                throw new ArgumentException("Residual bit sketch length mismatch.");
        }
    }

    private int QuantizeIndex(float x) {
        int lo = 0;
        int hi = _thresholds.Length;

        while (lo < hi) {
            int mid = lo + ((hi - lo) >> 1);
            if (x <= _thresholds[mid])
                hi = mid;
            else
                lo = mid + 1;
        }

        return lo;
    }

    private float[] ApplyRandomHadamard(ReadOnlySpan<float> input) {
        var buffer = new float[_dimension];
        for (int i = 0; i < _dimension; i++)
            buffer[i] = input[i] * _rotationSigns[i];

        FastWalshHadamard(buffer);

        float scale = 1f / MathF.Sqrt(_dimension);
        for (int i = 0; i < _dimension; i++)
            buffer[i] *= scale;

        return buffer;
    }

    private float[] ApplyInverseRandomHadamard(ReadOnlySpan<float> input) {
        // Hadamard is self-inverse up to scaling; since we use orthonormal scaling,
        // inverse is the same transform with the same diagonal signs.
        var buffer = input.ToArray();
        FastWalshHadamard(buffer);

        float scale = 1f / MathF.Sqrt(_dimension);
        for (int i = 0; i < _dimension; i++)
            buffer[i] *= scale * _rotationSigns[i];

        return buffer;
    }

    private static void FastWalshHadamard(float[] data) {
        int n = data.Length;
        for (int len = 1; 2 * len <= n; len <<= 1) {
            for (int i = 0; i < n; i += (len << 1)) {
                for (int j = 0; j < len; j++) {
                    float u = data[i + j];
                    float v = data[i + j + len];
                    data[i + j] = u + v;
                    data[i + j + len] = u - v;
                }
            }
        }
    }

    private static float L2Norm(ReadOnlySpan<float> v) {
        double sum = 0;
        for (int i = 0; i < v.Length; i++)
            sum += v[i] * v[i];
        return (float)Math.Sqrt(sum);
    }

    private static float[] SampleSphereCoordinates(int dimension, int sampleCount, int seed) {
        var rng = new Random(seed);
        var result = new float[sampleCount];

        for (int s = 0; s < sampleCount; s++) {
            var vec = new float[dimension];
            double norm2 = 0;
            for (int i = 0; i < dimension; i++) {
                float g = NextGaussian(rng);
                vec[i] = g;
                norm2 += g * g;
            }

            float invNorm = 1f / (float)Math.Sqrt(norm2);
            for (int i = 0; i < dimension; i++)
                vec[i] *= invNorm;

            result[s] = vec[rng.Next(dimension)];
        }

        return result;
    }

    private static float[] TrainLloydMax(float[] samples, int levels, int iterations) {
        if (levels < 2)
            throw new ArgumentException("levels must be >= 2.");

        float min = samples.Min();
        float max = samples.Max();

        var centers = new float[levels];
        for (int i = 0; i < levels; i++) {
            float t = (float)i / (levels - 1);
            centers[i] = min + t * (max - min);
        }

        for (int iter = 0; iter < iterations; iter++) {
            var sums = new double[levels];
            var counts = new int[levels];

            var thresholds = new float[levels - 1];
            for (int i = 0; i < thresholds.Length; i++)
                thresholds[i] = 0.5f * (centers[i] + centers[i + 1]);

            foreach (var x in samples) {
                int idx = 0;
                while (idx < thresholds.Length && x > thresholds[idx])
                    idx++;

                sums[idx] += x;
                counts[idx]++;
            }

            for (int i = 0; i < levels; i++) {
                if (counts[i] > 0)
                    centers[i] = (float)(sums[i] / counts[i]);
            }
        }

        return centers;
    }

    private static float[][] CreateGaussianProjection(int rows, int cols, int seed) {
        var rng = new Random(seed);
        var proj = new float[rows][];

        float scale = 1f / MathF.Sqrt(rows);
        for (int r = 0; r < rows; r++) {
            proj[r] = new float[cols];
            for (int c = 0; c < cols; c++)
                proj[r][c] = NextGaussian(rng) * scale;
        }

        return proj;
    }

    private static byte[] ProjectToSignBits(ReadOnlySpan<float> vector, float[][] projection) {
        int rows = projection.Length;
        byte[] bits = new byte[(rows + 7) / 8];

        for (int r = 0; r < rows; r++) {
            float dot = 0f;
            var row = projection[r];
            for (int c = 0; c < vector.Length; c++)
                dot += row[c] * vector[c];

            bool sign = dot >= 0f;
            if (sign)
                bits[r >> 3] |= (byte)(1 << (r & 7));
        }

        return bits;
    }

    private static bool GetBit(byte[] bytes, int index) {
        return (bytes[index >> 3] & (1 << (index & 7))) != 0;
    }

    private static float NextGaussian(Random rng) {
        // Box-Muller
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
    }
}

public sealed class EncodedVector {
    public int Dimension { get; }
    public int Bits { get; }
    public float Norm { get; }
    public byte[] Indices { get; }
    public byte[]? ResidualBits { get; }
    public int NodeId { get; }

    public EncodedVector(int dimension, int bits, float norm, byte[] indices, byte[]? residualBits, int nodeId) {
        Dimension = dimension;
        Bits = bits;
        Norm = norm;
        Indices = indices;
        ResidualBits = residualBits;
        NodeId = nodeId;
    }

    public int ApproxCompressedBytes =>
        sizeof(float) + Indices.Length + (ResidualBits?.Length ?? 0);
}
