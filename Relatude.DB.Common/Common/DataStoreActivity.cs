namespace Relatude.DB.Common;
public enum DataStoreActivityCategory {
    None,
    Opening,
    Closing,
    Querying,
    Executing,
    Flushing,
    Copying,
    Rewriting,
    SavingState,
}
public class DataStoreActivity(DataStoreActivityCategory category, string? description, int? percentageProgress) {
    public DataStoreActivityCategory Category { get; } = category;
    public string? Description { get; } = description;
    public int? PercentageProgress { get; } = percentageProgress;
    public override string ToString() {
        return $"{Category} - {Description} - {PercentageProgress}";
    }
    public static DataStoreActivity None => new(DataStoreActivityCategory.None, null, null);
}
