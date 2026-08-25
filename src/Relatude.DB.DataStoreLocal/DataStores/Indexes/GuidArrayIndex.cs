using Relatude.DB.DataStores.Definitions;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Indexes;

public class GuidArrayIndex : ValueArrayIndexBase<Guid>, IGuidArrayIndex {
    internal GuidArrayIndex(Definition def, string uniqueKey, string freindlyName, IIOProvider io, Guid propertyId)
        : base(def, uniqueKey, freindlyName, io) {
    }
    protected override void WriteArray(IAppendStream stream, Guid[] array) => stream.WriteGuidArray(array);
    protected override Guid[] ReadArray(IReadStream stream) => stream.ReadGuidArray();
}
