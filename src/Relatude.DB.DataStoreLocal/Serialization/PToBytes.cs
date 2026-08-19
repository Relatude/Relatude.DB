using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Stores;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.Transactions;

namespace Relatude.DB.Serialization;
internal static class PToBytes {
    public static void ActionBase(PrimitiveActionBase action, Datamodel def, Stream stream, long logFormatVersion, long transactionTimestamp, NodeSegment previousVersion, out long nodeSegmentRelativeOffset, out int nodeSegmentLength) {
        StreamExtenstions.WriteOneByte(stream, (byte)action.ActionTarget);
        if (action is PrimitiveNodeAction na) nodeAction(na, def, stream, logFormatVersion, transactionTimestamp, previousVersion, out nodeSegmentRelativeOffset, out nodeSegmentLength);
        else if (action is PrimitiveRelationAction ra) relationAction(ra, def, stream, out nodeSegmentRelativeOffset, out nodeSegmentLength);
        else if (action is PrimitiveRelationReorderAction rra) relationReorderAction(rra, stream, out nodeSegmentRelativeOffset, out nodeSegmentLength);
        else throw new NotImplementedException();
    }
    static void nodeAction(PrimitiveNodeAction action, Datamodel def, Stream stream, long logFormatVersion, long transactionTimestamp, NodeSegment previousVersion, out long nodeSegmentRelativeOffset, out int nodeSegmentLength) {
        StreamExtenstions.WriteOneByte(stream, (byte)action.Operation);
        if (logFormatVersion >= WALFile._logVersioNumber) {
            // version-chain header, at a fixed offset right before the node data so it can be read
            // from any node segment position without parsing the surrounding transaction
            stream.WriteLong(transactionTimestamp);
            stream.WriteLong(previousVersion.AbsolutePosition); // node data position of the previous add of the same node in this file, 0 = none
            stream.WriteInt(previousVersion.Length);
        }
        nodeSegmentRelativeOffset = stream.Position;
        ToBytes.NodeData(action.Node, def, stream);
        long length = stream.Position - nodeSegmentRelativeOffset;
        if (length > int.MaxValue) throw new Exception("Node data exceeds max size of 4GB");
        nodeSegmentLength = (int)length;
    }
    static void relationAction(PrimitiveRelationAction action, Datamodel def, Stream stream, out long nodeSegmentRelativeOffset, out int nodeSegmentLength) {
        StreamExtenstions.WriteOneByte(stream, (byte)action.Operation);
        stream.WriteGuid(action.RelationId);
        stream.WriteUInt((uint)action.Source);
        stream.WriteUInt((uint)action.Target);
        stream.WriteDateTime(action.ChangeUtc);
        nodeSegmentRelativeOffset = 0; nodeSegmentLength = 0; // not relevant for relations
    }
    static void relationReorderAction(PrimitiveRelationReorderAction action, Stream stream, out long nodeSegmentRelativeOffset, out int nodeSegmentLength) {
        stream.WriteGuid(action.RelationId);
        stream.WriteUInt((uint)action.Owner);
        stream.WriteUInt((uint)action.Moved);
        StreamExtenstions.WriteOneByte(stream, action.FromTargetToSource ? (byte)1 : (byte)0);
        stream.WriteInt(action.FromIndex);
        stream.WriteInt(action.ToIndex);
        nodeSegmentRelativeOffset = 0; nodeSegmentLength = 0; // not relevant for relations
    }
}
