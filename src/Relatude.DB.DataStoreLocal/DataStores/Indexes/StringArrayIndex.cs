using Relatude.DB.DataStores.Definitions;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Indexes;

public class StringArrayIndex : ValueArrayIndexBase<string>, IStringArrayIndex {
    internal StringArrayIndex(Definition def, string uniqueKey, string freindlyName, IIOProvider io, FileKeyUtility fileKey, Guid propertyId)
        : base(def, uniqueKey, freindlyName, io, fileKey) {
    }
    protected override void WriteArray(IAppendStream stream, string[] array) => stream.WriteStringArray(array);
    protected override string[] ReadArray(IReadStream stream) => stream.ReadStringArray();
}
