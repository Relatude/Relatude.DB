using Relatude.DB.Common;
namespace Relatude.DB.DataStores;
public sealed partial class DataStoreLocal : IDataStore {
    long _activityIdCounter = 0; // used to generate unique activity IDs
    Dictionary<long, DataStoreActivity> _currentActiveties = [];
    public DataStoreStatus GetStatus() {
        DataStoreActivity[] activities;
        lock (_currentActiveties) {
            if (State == DataStoreState.Closed || State == DataStoreState.Error) return new(State, []);
            activities = [.. _currentActiveties.Values.Select(c => c.Copy())];
        }
        return new(State, activities);
    }
    public long RegisterActvity(DataStoreActivityCategory category, string? description = null, int? percentageProgress = null) {
        lock (_currentActiveties) {
            _activityIdCounter++;
            var id = _activityIdCounter;
            var activity = DataStoreActivity.Create(id, category, description, percentageProgress);
            _currentActiveties[id] = activity;
            return id;
        }
    }
    public long RegisterChildActvity(long parentId, DataStoreActivityCategory category, string? description = null, int? percentageProgress = null) {
        lock (_currentActiveties) {
            _activityIdCounter++;
            var id = _activityIdCounter;
            var activity = DataStoreActivity.CreateChild(id, parentId, category, description, percentageProgress);
            _currentActiveties[id] = activity;
            return id;
        }
    }
    public void UpdateActivity(long activityId, string? description = null, int? percentageProgress = null) {
        lock (_currentActiveties) {
            if (_currentActiveties.TryGetValue(activityId, out var activity)) {
                activity.Description = description;
                activity.PercentageProgress = percentageProgress;
            }
        }
    }
    public void UpdateActivityProgress(long activityId, int? percentageProgress = null) {
        lock (_currentActiveties) {
            if (_currentActiveties.TryGetValue(activityId, out var activity)) {
                activity.PercentageProgress = percentageProgress;
            }
        }
    }
    public void DeRegisterActivity(long activityId) {
        lock (_currentActiveties) {
            _currentActiveties.Remove(activityId);
        }
    }
}
