using System.Text;

namespace Relatude.DB.DataStores.Indexes.TextIndexing;

/// <summary>
/// Writes one immutable segment file in a single sequential pass:
///
/// <code>
/// header:      magic (8) | version (4) | segmentId (8)
/// postings:    per term: varint addCount, (varint nodeIdDelta, hits byte)*, varint delCount, (varint nodeIdDelta)*
/// doc area:    varint opCount, per op sorted by id: varint nodeIdDelta, kind byte (1 set / 0 removed), varint wordCount when set
/// dictionary:  blocks of up to N terms, prefix-compressed against the previous term in the block;
///              per term: varint sharedPrefixChars, varint suffixByteLen, utf8 suffix, varint postingsByteLen;
///              the first term of each block also stores its postings offset (relative to the postings area),
///              later terms derive theirs from the previous term's offset + length
/// block index: per block: varint firstTermByteLen, utf8 firstTerm, varint termsInBlock, varint blockByteLen
/// footer:      postingsStart | docStart | dictStart | blockIndexStart (8 each) | termCount | blockCount (4 each) | magic (8)
/// </code>
///
/// The in-memory block index (one first-term per block) is the top lane of a skip structure over
/// the sorted term dictionary: lookups binary-search it and then decode a single block.
/// Terms must arrive in ordinal order, postings sorted by node id.
/// </summary>
internal static class SegmentWriter {
    public const long HeaderMagic = 0x314753585442_4452; // "RDB TXSG1"
    public const long FooterMagic = 0x44_4E45_47455358_54;  // "TXSEGEND"
    public const int Version = 1;
    public const int FooterLength = 8 * 4 + 4 * 2 + 8;

    public static void Write(string path, long segmentId, int termsPerBlock,
        IEnumerable<(string term, TermPostings postings)> sortedTerms,
        IReadOnlyList<(int id, int wordCountOrRemove)> sortedDocOps) {
        if (termsPerBlock < 1) termsPerBlock = 1; // 0 would make the block chunking loop forever
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
        writeFixedLong(fs, HeaderMagic);
        writeFixedInt(fs, Version);
        writeFixedLong(fs, segmentId);

        // postings area, collecting one dictionary entry per term as it goes
        var postingsStart = fs.Position;
        var entries = new List<(string term, long offset, int length)>();
        foreach (var (term, p) in sortedTerms) {
            var offset = fs.Position;
            VarInt.Write(fs, p.AddIds.Length);
            var prev = 0;
            for (var i = 0; i < p.AddIds.Length; i++) {
                VarInt.Write(fs, p.AddIds[i] - prev);
                prev = p.AddIds[i];
                fs.WriteByte(p.AddHits[i]);
            }
            VarInt.Write(fs, p.DelIds.Length);
            prev = 0;
            for (var i = 0; i < p.DelIds.Length; i++) {
                VarInt.Write(fs, p.DelIds[i] - prev);
                prev = p.DelIds[i];
            }
            entries.Add((term, offset, (int)(fs.Position - offset)));
        }

        // doc area
        var docStart = fs.Position;
        VarInt.Write(fs, sortedDocOps.Count);
        var prevId = 0;
        foreach (var (id, op) in sortedDocOps) {
            VarInt.Write(fs, id - prevId);
            prevId = id;
            if (op >= 0) {
                fs.WriteByte(1);
                VarInt.Write(fs, op);
            } else {
                fs.WriteByte(0);
            }
        }

        // dictionary blocks
        var dictStart = fs.Position;
        var blocks = new List<(string firstTerm, int termCount, int byteLength)>();
        for (var b = 0; b < entries.Count; b += termsPerBlock) {
            var count = Math.Min(termsPerBlock, entries.Count - b);
            var blockStart = fs.Position;
            var prevTerm = "";
            for (var i = 0; i < count; i++) {
                var (term, offset, length) = entries[b + i];
                var shared = i == 0 ? 0 : sharedPrefixLength(prevTerm, term);
                VarInt.Write(fs, shared);
                var suffix = Encoding.UTF8.GetBytes(term[shared..]);
                VarInt.Write(fs, suffix.Length);
                fs.Write(suffix);
                if (i == 0) VarInt.Write(fs, offset - postingsStart);
                VarInt.Write(fs, length);
                prevTerm = term;
            }
            blocks.Add((entries[b].term, count, (int)(fs.Position - blockStart)));
        }

        // block index
        var blockIndexStart = fs.Position;
        foreach (var (firstTerm, termCount, byteLength) in blocks) {
            var termBytes = Encoding.UTF8.GetBytes(firstTerm);
            VarInt.Write(fs, termBytes.Length);
            fs.Write(termBytes);
            VarInt.Write(fs, termCount);
            VarInt.Write(fs, byteLength);
        }

        // footer
        writeFixedLong(fs, postingsStart);
        writeFixedLong(fs, docStart);
        writeFixedLong(fs, dictStart);
        writeFixedLong(fs, blockIndexStart);
        writeFixedInt(fs, entries.Count);
        writeFixedInt(fs, blocks.Count);
        writeFixedLong(fs, FooterMagic);
        fs.Flush(true); // the manifest may only reference fully durable segment files
    }
    static int sharedPrefixLength(string a, string b) {
        var max = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < max && a[i] == b[i]) i++;
        return i;
    }
    static void writeFixedLong(Stream s, long v) {
        Span<byte> b = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(b, v);
        s.Write(b);
    }
    static void writeFixedInt(Stream s, int v) {
        Span<byte> b = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b, v);
        s.Write(b);
    }
}
