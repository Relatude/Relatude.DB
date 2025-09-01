using Relatude.DB.Transactions;
using Relatude.DB.DataStores.Transactions;
namespace Relatude.DB.DataStores.Transactions;
internal class ExecutedPrimitiveTransaction(List<PrimitiveActionBase> executedActions, long timestamp) {
    public List<PrimitiveActionBase> ExecutedActions { get; } = executedActions;
    public long Timestamp { get; } = timestamp;
}
