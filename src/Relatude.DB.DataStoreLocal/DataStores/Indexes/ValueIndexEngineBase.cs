using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Sets;

namespace Relatude.DB.DataStores.Indexes;

/// <summary>
/// Base class for <see cref="IValueIndexEngine"/> implementations (SQLite, the native KV store).
/// The cross-cutting orchestration (transaction guard, first-commit protocol, queue lifecycle,
/// WAL-id/timestamp/reset rules) lives in <see cref="IndexEngineBase"/>; this class owns the
/// value/array index opening: it applies the add/remove optimization wrappers and registers their
/// queues so the base can flush them at every commit boundary.
///
/// <para>To add a new value backend: derive from this class and implement the abstract members.
/// Read each member's doc for its exact contract; you should not need to know anything else about
/// how RelatudeDB drives the engine.</para>
/// </summary>
public abstract class ValueIndexEngineBase : IndexEngineBase, IValueIndexEngine {

    public IValueIndex<T> OpenValueIndex<T>(SetRegister sets, string id, string friendlyName, PropertyType type) where T : notnull {
        var index = CreateValueIndex<T>(sets, id, friendlyName, type, out var justCreated);
        RegisterManagedIndex(id, index, justCreated);
        // The engine (not IndexFactory) applies the add/remove optimization wrapper so that it can
        // flush the wrapper's queued remove at every commit and rollback boundary; memory indexes
        // are wrapped by IndexFactory and keep their fully lazy queue.
        var optimized = new OptimizedValueIndex<T>(index);
        RegisterQueue("v:" + id, optimized.Queue);
        return optimized;
    }

    public IStringArrayIndex OpenStringArrayIndex(SetRegister sets, string id, string friendlyName, PropertyType type) {
        // No add/remove optimization wrapper exists for array indexes (the memory variant is
        // not wrapped either), so writes go straight through and there is no queue to manage here.
        var index = CreateStringArrayIndex(sets, id, friendlyName, type, out var justCreated);
        RegisterManagedIndex(id, index, justCreated);
        return index;
    }

    public IGuidArrayIndex OpenGuidArrayIndex(SetRegister sets, string id, string friendlyName, PropertyType type) {
        // No add/remove optimization wrapper exists for array indexes (the memory variant is
        // not wrapped either), so writes go straight through and there is no queue to manage here.
        var index = CreateGuidArrayIndex(sets, id, friendlyName, type, out var justCreated);
        RegisterManagedIndex(id, index, justCreated);
        return index;
    }

    public IIntArrayIndex OpenIntArrayIndex(SetRegister sets, string id, string friendlyName, PropertyType type) {
        // No add/remove optimization wrapper exists for array indexes (the memory variant is
        // not wrapped either), so writes go straight through and there is no queue to manage here.
        var index = CreateIntArrayIndex(sets, id, friendlyName, type, out var justCreated);
        RegisterManagedIndex(id, index, justCreated);
        return index;
    }

    /// <summary>Create (or open) the backend value index for <paramref name="id"/>.
    /// Set <paramref name="justCreated"/> true only when the underlying storage did not exist and
    /// was created now — this drives the first-commit protocol.</summary>
    protected abstract IValueIndex<T> CreateValueIndex<T>(SetRegister sets, string id, string friendlyName, PropertyType type, out bool justCreated) where T : notnull;

    /// <summary>Create (or open) the backend string-array index for <paramref name="id"/>. Set
    /// <paramref name="justCreated"/> as in <see cref="CreateValueIndex{T}"/>.</summary>
    protected abstract IStringArrayIndex CreateStringArrayIndex(SetRegister sets, string id, string friendlyName, PropertyType type, out bool justCreated);

    /// <summary>Create (or open) the backend guid-array index for <paramref name="id"/>. Set
    /// <paramref name="justCreated"/> as in <see cref="CreateValueIndex{T}"/>.</summary>
    protected abstract IGuidArrayIndex CreateGuidArrayIndex(SetRegister sets, string id, string friendlyName, PropertyType type, out bool justCreated);

    /// <summary>Create (or open) the backend int-array index for <paramref name="id"/>. Set
    /// <paramref name="justCreated"/> as in <see cref="CreateValueIndex{T}"/>.</summary>
    protected abstract IIntArrayIndex CreateIntArrayIndex(SetRegister sets, string id, string friendlyName, PropertyType type, out bool justCreated);

    // Derived query caches (e.g. the facet-set sidecar): no-ops unless the backend maintains any.
    public virtual void SaveIndexCaches(bool force) { }
    public virtual void ResetIndexCaches() { }
}
