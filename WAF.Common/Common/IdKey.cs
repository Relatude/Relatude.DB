namespace WAF.Common;
public readonly struct IdKey {
    public IdKey(Guid guid, int integer) { Guid = guid; Int = integer; }
    public IdKey(Guid guid) => Guid = guid;
    public IdKey(int integer) => Int = integer;
    public Guid Guid { get; }
    public int Int { get; }
}