using System.Buffers.Binary;

namespace Relatude.DB.DataStores.History;

// The rows of the history index: fixed sizes, in log order, so row n starts at n × size, newest first is reading
// backwards and the start of a period is found by halving. A row says where a change is and what sort it was, never
// a value: names and changed fields are read from the log, by the positions the row holds.

/// <summary>One version of one node changed by one save: 80 bytes.</summary>
internal struct ChangeRow {
    public const int Size = 80;
    public const byte NoRevision = 255; // RevAfter of a version taken away, RevBefore of one made
    /// <summary>The transaction's log timestamp.</summary>
    public long At;
    /// <summary>Where the node as the save left it is in the source (node bytes, as a segment); 0 when the save deleted it.</summary>
    public long AfterPos;
    public int AfterLen;
    /// <summary>Where the node as it was before the save is in the source; 0 when the save made it.</summary>
    public long BeforePos;
    public int BeforeLen;
    public Guid Node;
    /// <summary>The revision's id, Guid.Empty for a node without revisions.</summary>
    public Guid Version;
    public ushort Type;       // into the types table
    public ushort Kind;       // LogChangeKinds
    public int Who;           // into the writers table, 0 when the log does not say
    public ushort Culture;    // into the cultures table, 0 for none
    public ushort Collection; // into the collections table, 0 for none
    public byte RevAfter;     // RevisionType after the save, NoRevision when the version went
    public byte RevBefore;    // RevisionType before it, NoRevision when the version is new
    /// <summary>Where the save's bytes end in the source, the furthest of its two segments: what a cut of the log is compared with.</summary>
    public readonly long End => Math.Max(AfterLen > 0 ? AfterPos + AfterLen : 0, BeforeLen > 0 ? BeforePos + BeforeLen : 0);
    public readonly void Write(Span<byte> b) {
        BinaryPrimitives.WriteInt64LittleEndian(b, At);
        BinaryPrimitives.WriteInt64LittleEndian(b[8..], AfterPos);
        BinaryPrimitives.WriteInt32LittleEndian(b[16..], AfterLen);
        BinaryPrimitives.WriteInt64LittleEndian(b[20..], BeforePos);
        BinaryPrimitives.WriteInt32LittleEndian(b[28..], BeforeLen);
        Node.TryWriteBytes(b[32..]);
        Version.TryWriteBytes(b[48..]);
        BinaryPrimitives.WriteUInt16LittleEndian(b[64..], Type);
        BinaryPrimitives.WriteUInt16LittleEndian(b[66..], Kind);
        BinaryPrimitives.WriteInt32LittleEndian(b[68..], Who);
        BinaryPrimitives.WriteUInt16LittleEndian(b[72..], Culture);
        BinaryPrimitives.WriteUInt16LittleEndian(b[74..], Collection);
        b[76] = RevAfter;
        b[77] = RevBefore;
        b[78] = 0;
        b[79] = 0;
    }
    public static ChangeRow Read(ReadOnlySpan<byte> b) => new() {
        At = BinaryPrimitives.ReadInt64LittleEndian(b),
        AfterPos = BinaryPrimitives.ReadInt64LittleEndian(b[8..]),
        AfterLen = BinaryPrimitives.ReadInt32LittleEndian(b[16..]),
        BeforePos = BinaryPrimitives.ReadInt64LittleEndian(b[20..]),
        BeforeLen = BinaryPrimitives.ReadInt32LittleEndian(b[28..]),
        Node = new Guid(b.Slice(32, 16)),
        Version = new Guid(b.Slice(48, 16)),
        Type = BinaryPrimitives.ReadUInt16LittleEndian(b[64..]),
        Kind = BinaryPrimitives.ReadUInt16LittleEndian(b[66..]),
        Who = BinaryPrimitives.ReadInt32LittleEndian(b[68..]),
        Culture = BinaryPrimitives.ReadUInt16LittleEndian(b[72..]),
        Collection = BinaryPrimitives.ReadUInt16LittleEndian(b[74..]),
        RevAfter = b[76],
        RevBefore = b[77],
    };
}

/// <summary>One link made or taken away by one save: 64 bytes.</summary>
internal struct LinkRow {
    public const int Size = 64;
    public long At;
    /// <summary>Where the relation action is in the source.</summary>
    public long Pos;
    public ushort Relation;  // into the relations table
    public bool Added;
    public bool System;
    public int Who;
    public ushort SourceType;
    public ushort TargetType;
    public Guid Source;
    public Guid Target;
    public readonly void Write(Span<byte> b) {
        BinaryPrimitives.WriteInt64LittleEndian(b, At);
        BinaryPrimitives.WriteInt64LittleEndian(b[8..], Pos);
        BinaryPrimitives.WriteUInt16LittleEndian(b[16..], Relation);
        b[18] = (byte)(Added ? 1 : 2);
        b[19] = (byte)(System ? 1 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(b[20..], Who);
        BinaryPrimitives.WriteUInt16LittleEndian(b[24..], SourceType);
        BinaryPrimitives.WriteUInt16LittleEndian(b[26..], TargetType);
        Source.TryWriteBytes(b[28..]);
        Target.TryWriteBytes(b[44..]);
        b[60..64].Clear();
    }
    public static LinkRow Read(ReadOnlySpan<byte> b) => new() {
        At = BinaryPrimitives.ReadInt64LittleEndian(b),
        Pos = BinaryPrimitives.ReadInt64LittleEndian(b[8..]),
        Relation = BinaryPrimitives.ReadUInt16LittleEndian(b[16..]),
        Added = b[18] == 1,
        System = b[19] == 1,
        Who = BinaryPrimitives.ReadInt32LittleEndian(b[20..]),
        SourceType = BinaryPrimitives.ReadUInt16LittleEndian(b[24..]),
        TargetType = BinaryPrimitives.ReadUInt16LittleEndian(b[26..]),
        Source = new Guid(b.Slice(28, 16)),
        Target = new Guid(b.Slice(44, 16)),
    };
}
