using Relatude.DB.DataStores.Sets;

namespace Relatude.DB.DataStores.Indexes;

/// <summary>
/// Base class for int-array indexes backed by an <see cref="IPersistedIndexStore"/>.
/// See <see cref="PersistedValueArrayIndexBase{T}"/> for the mirror/write-through mechanics.
/// </summary>
public abstract class PersistedIntArrayIndexBase : PersistedValueArrayIndexBase<int>, IIntArrayIndex {
    protected PersistedIntArrayIndexBase(IPersistedIndexStore store, bool justCreated, SetRegister sets, string uniqueKey, string friendlyName)
        : base(store, justCreated, sets, uniqueKey, friendlyName) {
    }
}
