using Relatude.DB.Datamodels;

namespace Relatude.DB.Nodes;
public interface IValueMapper {
    INodeData CreateNodeDataFromObject(object node, RelatedCollection? relatedCollection);
    bool TryGetIdGuidAndCreateIfPossible(object node, out Guid id);
    bool TryGetIdUInt(object node, out int id);
    bool TryGetIdGuid(object node, out Guid id);
    object NodeDataToObject(INodeData nodeData, NodeStore store);
}
