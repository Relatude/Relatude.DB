using System;
using Relatude.DB.Common;
using Relatude.DB.Transactions;
namespace Relatude.DB.Nodes;
public partial class Transaction {
    public async Task<TransactionResult> ExecuteAsync(bool flushToDisk = false) {
        var result = await Store.ExecuteAsync(this, flushToDisk);
        _transactionData = new();
        return result;
    }
    public TransactionResult Execute(bool flushToDisk = false) {
        var stateId = Store.Execute(this, flushToDisk);
        _transactionData = new();
        return stateId;
    }

    class PluginWithActions(INodeTransactionPlugin plugin, List<ActionsWithIds> actionsWithIds) {
        public INodeTransactionPlugin Plugin = plugin;
        public List<ActionsWithIds> ActionsWithIds = actionsWithIds;
    }
    class ActionsWithIds(ActionBase action, List<IdKey> ids) {
        public ActionBase Action = action;
        public List<IdKey> Ids = ids;
    }
    List<PluginWithActions>? _relevantPlugins;
    internal void PrepareRelevantPlugins() {
        if (Store.TransactionPlugins.Count == 0) return; // no plugins, nothing to do

        List<IdKey>? needTypeInfo = null; // we need type info to determine relevant plugins
        foreach (var plugin in Store.TransactionPlugins) {
            foreach (var action in _transactionData.Actions) {
                plugin.AddIdKeysThatNeedTypeInfo(action, ref needTypeInfo);
            }
        }
        Dictionary<IdKey, Guid>? typeByNodeId = null;
        if (needTypeInfo != null && needTypeInfo.Count > 0) { // for most actions we don't need type info as it is already in the action
            typeByNodeId = Store.Datastore.GetNodeType(needTypeInfo); // all this to reduce callbacks to datastore to max one call per transaction
        }

        foreach (var plugin in Store.TransactionPlugins) {
            List<ActionsWithIds>? actionNodeIds = null;
            foreach (var action in _transactionData.Actions) {
                var ids = plugin.GetRelevantNodeIds(action, typeByNodeId);
                if (ids.Count > 0) {
                    if (actionNodeIds == null) actionNodeIds = [];
                    actionNodeIds.Add(new(action, ids));
                }
                foreach (var id in ids) plugin.OnBefore(id, action, this);
            }
            if (actionNodeIds != null) {
                if (_relevantPlugins == null) _relevantPlugins = [];
                _relevantPlugins.Add(new(plugin, actionNodeIds));
            }
        }
    }
    internal void OnBeforeExecute() {
        if (_relevantPlugins == null) return; // no plugins, nothing to do
        foreach (var pluginWithActions in _relevantPlugins) {
            foreach (var actionAndId in pluginWithActions.ActionsWithIds) {
                foreach (var id in actionAndId.Ids) pluginWithActions.Plugin.OnBefore(id, actionAndId.Action, this);
            }
        }
        int i = 0;
        foreach (var action in _transactionData.Actions) action._i = i++; // set action index for later reference
    }
    internal void OnAfterExecute(TransactionResult result) {
        if (_relevantPlugins == null) return; // no plugins, nothing to do
        foreach (var pluginWithActions in _relevantPlugins) {
            foreach (var actionAndId in pluginWithActions.ActionsWithIds) {
                foreach (var id in actionAndId.Ids) {
                    var finalOperation = result.ResultingOperations[actionAndId.Action._i];
                    pluginWithActions.Plugin.OnAfter(id, actionAndId.Action, finalOperation);
                }
            }
        }
    }
    internal void OnErrorExecute(Exception error) {
        if (_relevantPlugins == null) return; // no plugins, nothing to do
        foreach (var pluginWithActions in _relevantPlugins) {
            foreach (var actionAndId in pluginWithActions.ActionsWithIds) {
                foreach (var id in actionAndId.Ids) pluginWithActions.Plugin.OnAfterError(id, actionAndId.Action, error);
            }
        }
    }

}
