using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Relatude.DB.Common;
public class ActivityBranch(DataStoreActivity activity, ActivityBranch[] children) {
    public DataStoreActivity Activity { get; } = activity;
    public ActivityBranch[] Children { get; } = children;
    public override bool Equals(object? obj) {
        if (obj is not ActivityBranch other) return false;
        return Activity.Equals(other.Activity) && Children.SequenceEqual(other.Children);
    }
}
public class DataStoreStatus(DataStoreState state, DataStoreActivity[] activities) {
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DataStoreState State { get; } = state;
    public ActivityBranch[] ActivityTree { get; } = buildTree(activities);
    static ActivityBranch[] buildTree(DataStoreActivity[] activities) {
        var roots = activities.Where(a => a.IsRoot).ToArray();
        var orphans = activities.Where(a => !a.IsRoot).ToDictionary(a => a.Id);
        var rootBrances = roots.Select(r => buildBranch(r, orphans)).ToArray();
        if (orphans.Count > 0) {
            // should not happen, indicates a bug in activity tracking
            var orphanBranches = orphans.Values.Select(a => new ActivityBranch(a, []));
            rootBrances = [.. rootBrances, .. orphanBranches];
#if DEBUG
            throw new Exception("Orphaned activities found: " + string.Join(", ", orphans.Values.Select(a => a.ToString())));
#endif
        }
        return rootBrances;
    }
    static ActivityBranch buildBranch(DataStoreActivity activity, Dictionary<long, DataStoreActivity> orphans) {
        var children = orphans.Values.Where(a => a.ParentId == activity.Id).ToArray();
        foreach (var c in children) orphans.Remove(c.Id);
        var childTrees = children.Select(c => buildBranch(c, orphans)).ToArray();
        return new(activity, childTrees);
    }
    public override bool Equals(object? obj) {
        if (obj is not DataStoreStatus other) return false;
        return State == other.State && ActivityTree.SequenceEqual(other.ActivityTree);
    }
}
