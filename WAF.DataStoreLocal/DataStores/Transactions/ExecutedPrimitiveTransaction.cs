using WAF.Transactions;
using WAF.DataStores.Transactions;
namespace WAF.DataStores.Transactions;
internal class ExecutedPrimitiveTransaction(List<PrimitiveActionBase> executedActions, long timestamp) {
    public List<PrimitiveActionBase> ExecutedActions { get; } = executedActions;
    public long Timestamp { get; } = timestamp;
}
