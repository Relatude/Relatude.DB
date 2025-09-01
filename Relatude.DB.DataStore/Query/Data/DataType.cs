namespace Relatude.DB.Query.Data {
    public enum DataType :byte{
        Unknown = 0,
        ValueTypeData = 1,
        ValueCollectionData = 2,
        ObjectCollection = 3,
        ObjectData = 4,
        TableData = 5,
        IStoreNodeData = 6,
        IStoreNodeDataCollection = 7,
        FacetData = 8,
    }
}
