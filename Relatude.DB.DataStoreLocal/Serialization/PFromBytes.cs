using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Query.Data;
using Relatude.DB.Transactions;
using System.Text;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Transactions;

namespace Relatude.DB.Serialization {
    internal static class PFromBytes {

        public static PrimitiveActionBase ActionBase(Datamodel datamodel, Stream stream, out long nodeSegmentRelativeOffset, out int nodeSegmenLength) {
            var target = (PrimitiveActionTarget)stream.ReadOneByte();
            if (target == PrimitiveActionTarget.Node) return nodeAction(datamodel, stream, out nodeSegmentRelativeOffset, out nodeSegmenLength);
            nodeSegmentRelativeOffset = 0; nodeSegmenLength = 0; // not relevant for relations
            if (target == PrimitiveActionTarget.Relation) return relationAction(stream);
            throw new NotImplementedException();
        }
        static PrimitiveNodeAction nodeAction(Datamodel datamodel, Stream stream, out long nodeSegmentRelativeOffset, out int nodeSegmentLength) {
            var operation = (PrimitiveOperation)stream.ReadOneByte();
            nodeSegmentRelativeOffset = stream.Position;
            var nodeData = FromBytes.NodeData(datamodel, stream);
            long length = stream.Position - nodeSegmentRelativeOffset;
            if (length > int.MaxValue) throw new Exception("Node data exceeds max size of 4GB");
            nodeSegmentLength = (int)length;
            return new PrimitiveNodeAction(operation, nodeData);
        }
        static PrimitiveRelationAction relationAction(Stream stream) {
            var operation = (PrimitiveOperation)stream.ReadOneByte();
            var relationId = stream.ReadGuid();
            var source = (int)stream.ReadUInt();
            var target = (int)stream.ReadUInt();
            var dtUtc = stream.ReadDateTime();
            return new PrimitiveRelationAction(operation, relationId, source, target, dtUtc);
        }

    }
}
