using System;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Transactions;
namespace Relatude.DB.Nodes;
public partial class Transaction {
    /// <summary>
    /// Commits everything queued on this transaction: all operations are applied together, or none of them are and
    /// an exception is thrown. Pass flushToDisk to wait until the change is on disk rather than only in the log
    /// buffer. The transaction is emptied afterwards, so the instance can be filled and executed again.
    /// </summary>
    public async Task<TransactionResult> ExecuteAsync(bool flushToDisk = false) {
        var result = await Store.ExecuteAsync(this, flushToDisk);
        _transactionData = new();
        return result;
    }
    /// <summary>
    /// Commits everything queued on this transaction: all operations are applied together, or none of them are and
    /// an exception is thrown. The returned result carries the new state id of the store and what each operation
    /// ended up doing. The transaction is emptied afterwards and can be filled and executed again.
    /// </summary>
    public TransactionResult Execute(bool flushToDisk = false) {
        var stateId = Store.Execute(this, flushToDisk);
        _transactionData = new();
        return stateId;
    }

    class PluginWithActions(INodeTransactionPlugin plugin, List<ActionsWithIds> actionsWithIds) {
        public INodeTransactionPlugin Plugin = plugin;
        public List<ActionsWithIds> ActionsWithIds = actionsWithIds;
    }
    class ActionsWithIds(ActionBase action, List<NodeKey> ids) {
        public ActionBase Action = action;
        public List<NodeKey> Ids = ids;
    }
    List<PluginWithActions>? _relevantPlugins;
    internal void PrepareRelevantPlugins() {
        if (Store.TransactionPlugins.Count == 0) return; // no plugins, nothing to do

        Dictionary<NodeKey, Guid>? typeByNodeId = null; // we need type info to determine relevant plugins
        var typedActions = 0;
        foreach (var plugin in Store.TransactionPlugins) {
            List<ActionsWithIds>? actionNodeIds = null;
            var initialActions = _transactionData.Actions.ToList();
            // looked up again when an earlier plugin's OnBefore added actions, such as an address set on a node this
            // transaction inserts, so the plugins after it can tell whether those concern them
            if (initialActions.Count != typedActions) {
                typeByNodeId = nodeTypesNeeded(initialActions, typeByNodeId);
                typedActions = initialActions.Count;
            }
            foreach (var action in initialActions) {
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
    /// <summary>
    /// The node types the plugins need to tell which actions concern them, added to those already known. For most
    /// actions none are needed, as the type is in the action. A node this transaction inserts has its type in its insert,
    /// since the store does not know it yet; the rest are asked of the store, in one call.
    /// </summary>
    Dictionary<NodeKey, Guid>? nodeTypesNeeded(List<ActionBase> actions, Dictionary<NodeKey, Guid>? known) {
        List<NodeKey>? needTypeInfo = null;
        foreach (var plugin in Store.TransactionPlugins) {
            foreach (var action in actions) plugin.AddIdKeysThatNeedTypeInfo(action, ref needTypeInfo);
        }
        if (needTypeInfo == null) return known;
        var missing = needTypeInfo.Where(k => known == null || !known.ContainsKey(k)).Distinct().ToList();
        if (missing.Count == 0) return known;
        known ??= [];
        foreach (var action in actions) {
            if (action is not NodeAction nodeAction || nodeAction.Node is NodeDataOnlyId) continue;
            var key = nodeAction.Node.IdKey;
            known[key] = nodeAction.Node.NodeType;
            if (key.HasGuid) known[new(key.Guid)] = nodeAction.Node.NodeType;
            if (key.Int != 0) known[new(key.Int)] = nodeAction.Node.NodeType;
        }
        missing = [.. missing.Where(k => !known.ContainsKey(k))];
        if (missing.Count > 0) {
            foreach (var (key, type) in Store.Datastore.GetNodeType(missing)) known[key] = type;
        }
        return known;
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
