using System.Text;

namespace Relatude.DB.Datamodels;
public class IncludeBranch {
    public IncludeBranch(Guid propertyId, int? top) {
        PropertyId = propertyId;
        Top = top;
    }
    public Guid PropertyId { get; }
    public int? Top { get; private set; }
    public readonly HashSet<int> EvaluatedIds = [];
    List<IncludeBranch>? _children;
    public IncludeBranch ReuseOrCreateChildBranch(Guid propertyId, int? top) {
        if (_children == null) _children = new();
        foreach (var c in _children) {
            if (c.PropertyId == propertyId) {
                if (c.Top == null || c.Top < top) c.Top = top; // take the highest top value
                return c;
            }
        }
        var b = new IncludeBranch(propertyId, top);
        _children.Add(b);
        return b;
    }
    public bool HasChildren() => _children != null;
    public IEnumerable<IncludeBranch> Children {
        get {
            if (_children == null) return [];
            return _children;
        }
    }
    public string ToFriendlyString(Datamodel dm) {
        StringBuilder stringBuilder = new();
        buildString(dm, stringBuilder, 0);
        return stringBuilder.ToString();
    }
    void buildString(Datamodel dm, StringBuilder sb, int level) {
        sb.Append(new string('-', level));
        sb.Append(dm.Properties[PropertyId].CodeName);
        sb.Append(Environment.NewLine);
        foreach (var c in Children) c.buildString(dm, sb, level + 1);
    }
    public IEnumerable<string> GetPaths(string? path = null) {
        path = path == null ? string.Empty : path + '.';
        path += PropertyId.ToString();
        if (Top != null) path += "|" + Top;
        if (_children != null) { // reached the end of one branch
            foreach (var c in _children) {
                foreach (var p in c.GetPaths(path)) {
                    yield return p;
                }
            }
        } else {
            yield return path;
        }
    }
    public static IncludeBranch ParseOnePath(string text) {
        var levels = text.Split('.');
        IncludeBranch? root = null;
        IncludeBranch? current = null;
        foreach (var l in levels) {
            var parts = l.Split('|');
            var propertyId = Guid.Parse(parts[0]);
            int? top = parts.Length > 1 ? int.Parse(parts[1]) : null;
            if (current == null) root = current = new(propertyId, top);
            else current = current.ReuseOrCreateChildBranch(propertyId, top);
        }
        if (root == null) throw new ArgumentException("Invalid include path. ");
        return root;
    }
    public void AddBranch(IncludeBranch branch) {
        if (_children == null) { // no children yet
            _children = new() { branch }; // create children list and add branch
        } else {
            // search for existing branch with same propertyId:
            IncludeBranch? existing = _children.Where(c => c.PropertyId == branch.PropertyId).FirstOrDefault();
            if (existing == null) { // no existing branch with same propertyId
                _children.Add(branch);
            } else { // found existing branch with same propertyId, so merge them
                if (existing.Top == null || existing.Top < branch.Top) existing.Top = branch.Top; // take the highest top value
                foreach (var c in branch.Children) existing.AddBranch(c); // merge children too
            }
        }
    }

    public void Reset() {
        EvaluatedIds.Clear();
        if (_children != null) foreach (var c in _children) c.Reset();
    }
}
