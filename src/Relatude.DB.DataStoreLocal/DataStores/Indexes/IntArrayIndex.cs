using Relatude.DB.DataStores.Definitions;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Indexes;

public class IntArrayIndex : ValueArrayIndexBase<int>, IIntArrayIndex {
    internal IntArrayIndex(Definition def, string uniqueKey, string freindlyName, IIOProvider io, Guid propertyId)
        : base(def, uniqueKey, freindlyName, io) {
    }
    protected override void WriteArray(IAppendStream stream, int[] array) => stream.WriteIntArray(array);
    protected override int[] ReadArray(IReadStream stream) => stream.ReadIntArray();
}
