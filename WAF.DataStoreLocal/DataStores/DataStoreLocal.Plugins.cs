//using WAF.Datamodels;
//using WAF.Transactions;
//namespace WAF.DataStores;

//// NOT IN USE AT THE MOMENT
//public sealed partial class DataStoreLocal : IDataStore {
//    readonly List<ITransactionDataPlugin> _transactionPlugins = new();
//    readonly List<INodeDataTransactionDataPlugin> _nodeTransactionPlugins = new();
//    readonly List<IDataRelationTransactionDataPlugin> _relationTransactionPlugins = new();
//    public void RegisterTransactionPlugin(ITransactionDataPlugin plugin) => _transactionPlugins.Add(plugin);
//    public void RegisterNodeTransactionPlugin(INodeDataTransactionDataPlugin plugin, Guid typeId, bool includeDescendants) => _nodeTransactionPlugins.Add(plugin);
//    public void RegisterRelationTransactionPlugin(IDataRelationTransactionDataPlugin plugin, Guid relationId) => _relationTransactionPlugins.Add(plugin);
//    void callPlugins(TransactionData transaction) {
//        foreach (var plugin in _transactionPlugins) {
//            plugin.OnBeforeTransaction(transaction);
//        }
//        foreach (var action in transaction.Actions) {
//            if(action is NodeAction nodeAction) {
//                foreach (var nodePlugin in _nodeTransactionPlugins) {
//                    switch (nodeAction.Operation) {
//                        case NodeOperation.Insert:
//                            nodePlugin.OnBeforeInsert((NodeData)nodeAction.Node);
//                            nodePlugin.OnBeforeInsertOrUpdate((NodeData)nodeAction.Node);
//                            break;
//                        case NodeOperation.Update:
//                            nodePlugin.OnBeforeInsertOrUpdate((NodeData)nodeAction.Node);
//                            break;
//                        case NodeOperation.Remove:
//                            break;
//                        default:
//                            break;
//                    }
//                }
//            } else if(action is RelationAction relationAction) {
//                throw new NotImplementedException();
//            }
//        }
//    }

//}

