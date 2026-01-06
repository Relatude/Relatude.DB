namespace Relatude.DB.Datamodels;
public interface INodeDataComplex : INodeData {
    int CreatedBy { get; }
    int ChangedBy { get; }

    int CollectionId { get; }
    int CultureId { get; }
    int RevisionId { get; }
}
public interface INodeDataComplexContainer : INodeData {
    int ReadAccess { get; }
    int EditViewAccess { get; }
    int PublishAccess { get; }
    INodeDataComplex[] Versions { get; }
}
public class NodeComplexMeta {
    public int ReadAccess { get; }
    public int EditViewAccess { get; }
    public int PublishAccess { get; }
    public int CreatedBy { get; }
    public int ChangedBy { get; }
    public int CultureId { get; }
    public int CollectionId { get; }
    public DateTime PublishedUtc { get; }
    public DateTime RetainedUtc { get; }
    public DateTime ReleasedUtc { get; }
}
