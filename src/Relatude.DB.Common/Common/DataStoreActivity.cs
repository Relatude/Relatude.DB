using System.Text.Json.Serialization;
namespace Relatude.DB.Common;
public enum DataStoreActivityCategory {
    Opening,
    Closing,
    Querying,
    Executing,
    Flushing,
    Copying,
    Rewriting,
    SavingState,
    RunningTask,
}
public class DataStoreActivity {
    private DataStoreActivity(long id, long parentId, DataStoreActivityCategory cat, string? desc, int? prg) {
        Id = id;
        ParentId = parentId;
        Category = cat;
        Description = desc;
        PercentageProgress = prg;
    }
    public long Id { get; }
    public long ParentId { get; }
    public bool IsRoot => ParentId == 0;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DataStoreActivityCategory Category { get; }
    public string? Description { get; set; }
    public int? PercentageProgress { get; set; }
    public override string ToString() {
        return $"{Category} - {Description} - {PercentageProgress}";
    }
    public static DataStoreActivity Create(long activityId, DataStoreActivityCategory category, string? description = null, int? percentageProgress = null) {
        return new(activityId, 0, category, description, percentageProgress);
    }
    public static DataStoreActivity CreateChild(long activityId, long parentActivityId, DataStoreActivityCategory category, string? description = null, int? percentageProgress = null) {
        return new(activityId, parentActivityId, category, description, percentageProgress);
    }
    public DataStoreActivity Copy() {
        return new DataStoreActivity(Id, ParentId, Category, Description, PercentageProgress);
    }
    public override bool Equals(object? obj) {
        if (obj is not DataStoreActivity other) return false;
        return Id == other.Id && ParentId == other.ParentId && Category == other.Category && Description == other.Description && PercentageProgress == other.PercentageProgress;
    }
    public override int GetHashCode() {
        throw new NotImplementedException();
    }
}
