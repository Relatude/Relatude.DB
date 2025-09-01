namespace Relatude.DB.Common;
public class DataStoreStatus( DataStoreState state, DataStoreActivity activity) { 
    public DataStoreState State { get; } = state;
    public DataStoreActivity Activity { get; } = activity;
    //public string? ErrorMessage { get; } = null;
    //public string? ErrorDetails { get; } = null;
    //public string? LastErrorMessage { get; set; } = null;
    //public string? LastErrorDetails { get; set; } = null;
}
