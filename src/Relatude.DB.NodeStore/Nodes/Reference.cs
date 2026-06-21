using Relatude.DB.Datamodels;

namespace Relatude.DB.Nodes;

public interface IReference {
    void Initialize(NodeStore store, Guid parentId, Guid propertyId, INodeDataExternal? nodeData, bool? isSet);
}
public class Reference<T>() where T : notnull {

}