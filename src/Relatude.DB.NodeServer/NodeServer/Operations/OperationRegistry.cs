using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.NodeServer.Operations;

/// <summary>
/// Every operation the server's callers may ask for, by domain and name (both compared without case), so the admin
/// UI's and the CMS's may share a name. <c>AddRelatudeDB()</c> adds one as a singleton, which the admin UI and the CMS
/// register into. Thread-safe.
/// </summary>
public sealed class OperationRegistry {
    readonly ConcurrentDictionary<(string Domain, string Name), Operation> _operations = new(new KeyComparer());

    /// <summary>Adds the operation, or replaces the one of its domain and name.</summary>
    public void Register(Operation operation) => _operations[(operation.Domain, operation.Name)] = operation;

    public bool TryGet(string domain, string name, [MaybeNullWhen(false)] out Operation operation) => _operations.TryGetValue((domain, name), out operation);

    /// <summary>The names in a domain, in ordinal order.</summary>
    public string[] Names(string domain) =>
        [.. _operations.Keys.Where(k => string.Equals(k.Domain, domain, StringComparison.OrdinalIgnoreCase)).Select(k => k.Name).Order(StringComparer.Ordinal)];

    public IReadOnlyList<Operation> All => [.. _operations.Values];

    sealed class KeyComparer : IEqualityComparer<(string Domain, string Name)> {
        public bool Equals((string Domain, string Name) x, (string Domain, string Name) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Domain, y.Domain) && StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name);
        public int GetHashCode((string Domain, string Name) key) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Domain), StringComparer.OrdinalIgnoreCase.GetHashCode(key.Name));
    }
}
