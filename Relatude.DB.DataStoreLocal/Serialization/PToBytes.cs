using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Query.Data;
using Relatude.DB.Transactions;
using System.Text;
using Relatude.DB.DataStores.Transactions;

namespace Relatude.DB.Serialization;
internal static class PToBytes {
    public static void ActionBase(PrimitiveActionBase action, Datamodel def, Stream stream, out long nodeSegmentRelativeOffset, out int nodeSegmentLength) {
        StreamExtenstions.WriteOneByte(stream, (byte)action.ActionTarget);
        if (action is PrimitiveNodeAction na) nodeAction(na, def, stream, out nodeSegmentRelativeOffset, out nodeSegmentLength);
        else if (action is PrimitiveRelationAction ra) relationAction(ra, def, stream, out nodeSegmentRelativeOffset, out nodeSegmentLength);
        else throw new NotImplementedException();        
    }
    static void nodeAction(PrimitiveNodeAction action, Datamodel def, Stream stream, out long nodeSegmentRelativeOffset, out int nodeSegmentLength) {
        StreamExtenstions.WriteOneByte(stream, (byte)action.Operation);
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
}

