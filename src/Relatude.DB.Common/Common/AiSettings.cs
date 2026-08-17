namespace Relatude.DB.Common;

public enum AIProviderCacheType {
    None = 0,
    Native = 1,
    Memory = 2,
    Sqlite = 3,
}
public enum AIIndexType {
    Memory = 0,
    IVS = 1,
    HNSW = 2,
}