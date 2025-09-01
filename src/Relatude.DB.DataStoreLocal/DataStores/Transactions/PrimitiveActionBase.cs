using Relatude.DB.Query.Cache;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.Transactions;

namespace Relatude.DB.DataStores.Transactions;
public abstract class PrimitiveActionBase {
    public PrimitiveActionBase(PrimitiveActionTarget target) { ActionTarget = target; }
    public PrimitiveActionTarget ActionTarget { get; }
    public abstract PrimitiveActionBase Opposite(); // return null if there is no opposite action
    public PrimitiveOperation Operation { get; protected set; }    
    //public void RegisterEffects(TransactionEffects effects) {
        
    //}
}
