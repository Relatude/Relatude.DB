using Relatude.DB.DataStores.Sets;

namespace Relatude.DB.DataStores.Indexes;

/// <summary>
/// Base class for string-array indexes backed by an <see cref="IValueIndexEngine"/>.
/// See <see cref="PersistedValueArrayIndexBase{T}"/> for the mirror/write-through mechanics.
/// </summary>
public abstract class PersistedStringArrayIndexBase : PersistedValueArrayIndexBase<string>, IStringArrayIndex {
    protected PersistedStringArrayIndexBase(IIndexEngine engine, bool justCreated, SetRegister sets, string uniqueKey, string friendlyName)
        : base(engine, justCreated, sets, uniqueKey, friendlyName) {
    }
}
