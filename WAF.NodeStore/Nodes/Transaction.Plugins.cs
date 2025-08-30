using System;
using WAF.Common;
using WAF.Transactions;
namespace WAF.Nodes;
public sealed partial class Transaction {
    public async Task<long> ExecuteAsync(bool flushToDisk = false) {
        var result = await _store.ExecuteAsync(this, flushToDisk);
        _transactionData = new();
        return result.TransactionId;
    }
    public long Execute(bool flushToDisk = false) {
        var stateId = _store.Execute(this, flushToDisk);
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
        if (_store.TransactionPlugins.Count == 0) return; // no plugins, nothing to do

        List<IdKey>? needTypeInfo = null; // we need type info to determine relevant plugins
        foreach (var plugin in _store.TransactionPlugins) {
            foreach (var action in _transactionData.Actions) {
                plugin.AddIdKeysThatNeedTypeInfo(action, ref needTypeInfo);
            }
        }
        Dictionary<IdKey, Guid>? typeByNodeId = null;
        if (needTypeInfo != null && needTypeInfo.Count > 0) { // for most actions we don't need type info as it is already in the action
            typeByNodeId = _store.Datastore.GetNodeType(needTypeInfo); // all this to reduce callbacks to datastore to max one call per transaction
        }

        foreach (var plugin in _store.TransactionPlugins) {
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

    }
    internal void OnAfterExecute() {
        if (_relevantPlugins == null) return; // no plugins, nothing to do
        foreach (var pluginWithActions in _relevantPlugins) {
            foreach (var actionAndId in pluginWithActions.ActionsWithIds) {
                foreach (var id in actionAndId.Ids) pluginWithActions.Plugin.OnAfter(id, actionAndId.Action);
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
