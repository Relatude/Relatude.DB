using WAF.Datamodels;
using WAF.Transactions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WAF.DataStores;
using System.Text.Json;

namespace WAF.Serialization {
    public static class FromJson {
        public static List<ActionBase> ToActionList(Datamodel datamodel, string? json) {
            if (string.IsNullOrEmpty(json)) return new List<ActionBase>();
            throw new NotImplementedException();
            //var length = stream.ReadInt();
            //var actions = new List<ActionBase>(length);
            //for (int i = 0; i < length; i++) actions.Add(ToActionBase(datamodel, stream, out _, out _));
            //return actions;
        }
        public static T Generic<T>(object o) {
            throw new NotImplementedException();
        }
        internal static StoreStatus StoreInfo(string json) {
            var result = JsonSerializer.Deserialize<StoreStatus>(json);
            if(result == null) throw new Exception("Failed to deserialize StoreStatus. ");
            return result;
        }
    }
}
