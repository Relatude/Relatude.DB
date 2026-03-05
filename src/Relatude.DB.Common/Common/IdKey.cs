using System.Reflection.Metadata;

namespace Relatude.DB.Common;

public readonly struct IdKey {
    public IdKey(Guid guid, int integer) { Guid = guid; Int = integer; }
    public IdKey(Guid guid) => Guid = guid;
    public IdKey(int integer) => Int = integer;
    public Guid Guid { get; }
    public int Int { get; }
    public bool HasGuid => Guid != Guid.Empty;
    public bool HasInt => Int != 0;
    public override string ToString() {
        if (HasGuid && HasInt) {
            return Guid + " (" + Int + ")";
        } else if (HasGuid) {
            return Guid.ToString();
        } else if (HasInt) {
            return Int.ToString();
        } else {
            return string.Empty;
        }
    }
}