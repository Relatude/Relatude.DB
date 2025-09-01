using Relatude.DB.Common;
using Relatude.DB.Connection;
using Relatude.DB.Serialization;
using Relatude.DB.Transactions;
using System.Text;

namespace Relatude.DB.DataStores {
    public static class StoreDataExtentions {
        public static async Task<MemoryStream> BinaryCallAsync(this IDataStore db, Stream input) {
            try {
                var output = new MemoryStream();
                var method = input.ReadString();
                if (nameof(db.GetAsync) == method) ToBytes.NodeData(await db.GetAsync(input.ReadGuid()), db.Datamodel, output);
                else if (nameof(db.GetAsync) + "_uint" == method) ToBytes.NodeData(await db.GetAsync((int)input.ReadUInt()), db.Datamodel, output);
                else if (nameof(db.ExecuteAsync) == method) await db.ExecuteAsync(new TransactionData(FromBytes.ActionBaseList(db.Datamodel, input)));
                //else if (nameof(db.MaintenanceAsync) == method) (await db.MaintenanceAsync((MaintenanceAction)input.ReadInt()))
                //else if (nameof(db.QueryAsync) == method) ToBytes.ObjectToBytes(await db.QueryAsync(input.ReadString()), db.Datamodel, output);
                else throw new NotSupportedException(method + " is not supported.");
                return output;
            } catch (Exception ex) {
                return RemoteServerException.CreateErrorResponseStream(ex);
            }
        }
    }
}