using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.NodeServer.Operations;

/// <summary>
/// Every operation the server's callers may ask for, by domain and name (both compared without case), so the admin
/// UI's and the CMS's may share a name. <c>AddRelatudeDB()</c> adds one as a singleton, which the admin UI and the CMS
/// register into. Kept in the order registered, a replacement keeping its place, so a list made from it (an agent's
/// tools) is the same every time. Thread-safe.
/// </summary>
public sealed class OperationRegistry {
    readonly object _sync = new();
    readonly Dictionary<(string Domain, string Name), int> _at = new(new KeyComparer());
    readonly List<Operation> _operations = [];
    Operation[]? _all;

    /// <summary>Adds the operation, or replaces the one of its domain and name.</summary>
    public void Register(Operation operation) {
        lock (_sync) {
            var key = (operation.Domain, operation.Name);
            if (_at.TryGetValue(key, out var at)) {
                _operations[at] = operation;
            } else {
                _at[key] = _operations.Count;
                _operations.Add(operation);
            }
            _all = null;
        }
    }

    public bool TryGet(string domain, string name, [MaybeNullWhen(false)] out Operation operation) {
        lock (_sync) {
            if (_at.TryGetValue((domain, name), out var at)) {
                operation = _operations[at];
                return true;
            }
        }
        operation = null;
        return false;
    }

    /// <summary>The names in a domain, in ordinal order; with a caller, only those it may call.</summary>
    public string[] Names(string domain, string? caller = null) =>
        [.. All.Where(o => string.Equals(o.Domain, domain, StringComparison.OrdinalIgnoreCase) && (caller == null || o.Allows(caller))).Select(o => o.Name).Order(StringComparer.Ordinal)];

    /// <summary>Every operation, in the order registered.</summary>
    public IReadOnlyList<Operation> All {
        get {
            lock (_sync) return _all ??= [.. _operations];
        }
    }

    sealed class KeyComparer : IEqualityComparer<(string Domain, string Name)> {
        public bool Equals((string Domain, string Name) x, (string Domain, string Name) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Domain, y.Domain) && StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name);
        public int GetHashCode((string Domain, string Name) key) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Domain), StringComparer.OrdinalIgnoreCase.GetHashCode(key.Name));
    }
}
