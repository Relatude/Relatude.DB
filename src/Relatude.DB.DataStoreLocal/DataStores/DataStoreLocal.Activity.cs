using Relatude.DB.Common;
namespace Relatude.DB.DataStores;
public sealed partial class DataStoreLocal : IDataStore {

    // Activity tracking is approximate and does not account for multiple activities at the same time. 
    // Designed to always respond to the user
    // Rewriting is considered the main activity, and other activities are ignored during rewriting.
    // Querying is treated differently, as there can be multiple queries running at the same time.

    object _activityLock = new(); // lock for activity tracking

    DataStoreActivityCategory _activityCategory;
    string? _activityDescription;
    int? _activityPercentageProgress;
    int _runningQueryCounts;
    public DataStoreActivity GetActivity() {
        if (State == DataStoreState.Closed || State == DataStoreState.Error) {
            return DataStoreActivity.None;
        }
        int runningQueryCount = Interlocked.CompareExchange(ref _runningQueryCounts, 0, 0);
        lock (_activityLock) {
            DataStoreActivityCategory category;
            if (runningQueryCount > 0 && _activityCategory != DataStoreActivityCategory.Rewriting) {
                category = DataStoreActivityCategory.Querying;
                var desc = runningQueryCount > 1 ? $"{runningQueryCount} queries running" : "1 query running";
                return new(category, desc, null);
            } else {
                category = _activityCategory;
            }
            if (category == DataStoreActivityCategory.None) return DataStoreActivity.None;
            return new(category, _activityDescription, _activityPercentageProgress);
        }
    }
    public DataStoreStatus GetStatus() => new(State, GetActivity());
    void registerQueryActivity() => Interlocked.Increment(ref _runningQueryCounts); // must be fast as it is called on every query, no object lock
    void deRegisterQueryActivity() => Interlocked.Decrement(ref _runningQueryCounts); // must be fast as it is called on every query, no object lock
    void startRewriteActivity(string? description) {
        lock (_activityLock) {
            _activityCategory = DataStoreActivityCategory.Rewriting;
            _activityDescription = description;
            _activityPercentageProgress = 0;
        }
    }
    void updateCurrentActivity(DataStoreActivityCategory category, string? description, int? percentageProgress) {
        lock (_activityLock) {
            if (_activityCategory == DataStoreActivityCategory.Rewriting) return; // if rewriting, we leave it as "main" activity (as rewriting operation does not lock for other activities)
            _activityCategory = category;
            _activityDescription = description;
            _activityPercentageProgress = percentageProgress;
        }
    }
    void updateCurrentActivity(string? description, int? percentageProgress) {
        lock (_activityLock) {
            _activityDescription = description;
            _activityPercentageProgress = percentageProgress;
        }
    }
    void updateCurrentActivity(int? percentageProgress) {
        lock (_activityLock) {
            _activityPercentageProgress = percentageProgress;
        }
    }
    void endCurrentActivity() {
        lock (_activityLock) {
            if (_activityCategory == DataStoreActivityCategory.Rewriting) return; // if rewriting, we leave it as "main" activity (as rewriting operation does not lock for other activities)
            _activityCategory = DataStoreActivityCategory.None;
            _activityDescription = null;
            _activityPercentageProgress = null;
        }
    }
    void updateRewriteActivity(string description, int percentageProgress) {
        lock (_activityLock) {
            _activityCategory = DataStoreActivityCategory.Rewriting;
            _activityDescription = description;
            _activityPercentageProgress = percentageProgress;
        }
    }
    void endRewriteActivity() {
        lock (_activityLock) {
            _activityCategory = DataStoreActivityCategory.None;
            _activityDescription = null;
            _activityPercentageProgress = null;
        }
    }
}
