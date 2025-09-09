using Relatude.DB.IO;
using System.Buffers;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Relatude.DB.DataStores.Indexes.VectorIndex;
public class FlatDiskVectorIndex : IVectorIndex {
    int _dimensions;
    Stream _stream;
    Dictionary<int, int> _fileIndex = []; // record segment by nodeId
    List<int> _vacant = [];
    byte[] _buffer;
    float[] _vector;
    int _recordSegmentLength;
    public FlatDiskVectorIndex(Stream stream, int dimensions) {
        _dimensions = dimensions;
        _stream = stream;
        _recordSegmentLength = dimensions * 4 + 4;
        _buffer = new byte[_recordSegmentLength];
        _vector = new float[dimensions];
    }
    long offsetFromSegment(int segment) => (long)segment * _recordSegmentLength;
    int segmentFromOffset(long pos) => (int)(pos / _recordSegmentLength);
    void writeRecord(int nodeId, float[] vector, int segment) {
        // avoiding object instantiation and minimum memory allocation
        BitConverter.GetBytes(nodeId).CopyTo(_buffer, 0);
        Buffer.BlockCopy(vector, 0, _buffer, 4, _dimensions * 4);
        _stream.Seek(offsetFromSegment(segment), SeekOrigin.Begin);
        _stream.Write(_buffer, 0, _recordSegmentLength);
    }
    public void Set(int nodeId, float[] vectorsForEachParagraph) {
        if (vectorsForEachParagraph.Length != _dimensions) throw new ArgumentException("Vector dimensions do not match index dimensions");
        if (_fileIndex.TryGetValue(nodeId, out var segment)) {
            writeRecord(nodeId, vectorsForEachParagraph, segment);
        } else {
            segment = segmentFromOffset(_stream.Length);
            writeRecord(nodeId, vectorsForEachParagraph, segment);
            _fileIndex[nodeId] = segment;
        }
    }
    public void Clear(int nodeId) {
        if (_fileIndex.TryGetValue(nodeId, out var offset)) {
            _fileIndex.Remove(nodeId);
            _vacant.Add(offset);
        }
    }

    public List<VectorHit> Search(float[] u, int skip, int take, float minSimilarity) {
        var k = Math.Max(0, skip) + Math.Max(0, take);
        var pq = k > 0 ? new PriorityQueue<VectorHit, float>() : null;

        // Ensure buffered, large sequential reads
        Stream s = _stream is BufferedStream ? _stream : new BufferedStream(_stream, 1 << 20);
        s.Seek(0, SeekOrigin.Begin);

        // Batch read records
        int recordLen = _recordSegmentLength;               // 4 (nodeId) + 4*d (vector)
        int bufSize = Math.Clamp(recordLen * 1024, 1 << 20, 8 << 20); // 1–8MB
        byte[] buf = ArrayPool<byte>.Shared.Rent(bufSize);

        try {
            var uSpan = u.AsSpan();

            while (true) {
                int bytesRead = s.Read(buf, 0, bufSize);
                if (bytesRead <= 0) break;

                int pos = 0;
                // Process complete records inside the buffer
                while (pos + recordLen <= bytesRead) {
                    int nodeId = BitConverter.ToInt32(buf, pos);

                    // Optional: if you add a tombstone flag, check it here and skip the dictionary.
                    if (_fileIndex.ContainsKey(nodeId)) {
                        var vecBytes = buf.AsSpan(pos + 4, _dimensions * 4);
                        var v = MemoryMarshal.Cast<byte, float>(vecBytes);

                        float sim = DotSimd(uSpan, v);
                        if (sim >= minSimilarity) {
                            if (pq is null) {
                                // No paging requested; fall back to collecting all
                                // (or you can still use PQ with k = int.MaxValue)
                            } else {
                                if (pq.Count < k) pq.Enqueue(new VectorHit(nodeId, sim), sim);
                                else if (pq.Peek().Similarity < sim) {
                                    pq.Dequeue();
                                    pq.Enqueue(new VectorHit(nodeId, sim), sim);
                                }
                            }
                        }
                    }

                    pos += recordLen;
                }

                // If we ended on a partial record, seek back so the next read continues properly
                if (pos < bytesRead) {
                    long unread = bytesRead - pos;
                    s.Seek(-unread, SeekOrigin.Current);
                }

                if (bytesRead < bufSize) break; // EOF
            }
        } finally {
            ArrayPool<byte>.Shared.Return(buf);
        }

        // Materialize results (top-k), order desc, then apply skip/take
        if (pq is not null) {
            var tmp = new List<VectorHit>(pq.Count);
            while (pq.Count > 0) tmp.Add(pq.Dequeue());
            tmp.Sort((a, b) => b.Similarity.CompareTo(a.Similarity));
            return tmp.Skip(skip).Take(take).ToList();
        }

        // If you chose to collect-all above:
        // return hits.OrderByDescending(h => h.Similarity).Skip(skip).Take(take).ToList();
        return new();
    }

    static float DotSimd(ReadOnlySpan<float> a, ReadOnlySpan<float> b) {
        float sum = 0f;
        int i = 0;

        if (Vector.IsHardwareAccelerated) {
            int w = Vector<float>.Count;
            for (; i <= a.Length - w; i += w) {
                var va = new Vector<float>(a.Slice(i, w));
                var vb = new Vector<float>(b.Slice(i, w));
                sum += Vector.Dot(va, vb);
            }
        }
        for (; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }


    public List<VectorHit> Search0(float[] u, int skip, int take, float minVectorDistance) {
        var hits = new List<VectorHit>();
        _stream.Seek(0, SeekOrigin.Begin);
        while (_stream.Position < _stream.Length) {
            _stream.Read(_buffer, 0, _buffer.Length);
            var nodeId = BitConverter.ToInt32(_buffer, 0);
            if (_fileIndex.ContainsKey(nodeId)) {
                Buffer.BlockCopy(_buffer, 4, _vector, 0, _dimensions * 4);
                float similarity = 0;
                for (var i = 0; i < _dimensions; i++) similarity += u[i] * _vector[i];
                if (similarity >= minVectorDistance) hits.Add(new(nodeId, similarity));
            }
        }
        return hits.OrderByDescending(h => h.Similarity).Skip(skip).Take(take).ToList();
    }
    public void CompressMemory() {
        throw new NotImplementedException();
    }

    public void ReadState(IReadStream stream) {
        throw new NotImplementedException();
    }

    public void SaveState(IAppendStream stream) {
        throw new NotImplementedException();
    }
}

