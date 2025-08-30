using System.Collections.Generic;
using System.Diagnostics;
using System.Transactions;
using WAF.Common;
using WAF.Datamodels;
using WAF.DataStores.Transactions;
using WAF.Tasks;
using WAF.Transactions;
namespace WAF.DataStores;
public sealed partial class DataStoreLocal : IDataStore {
    public Task<TransactionResult> ExecuteAsync(TransactionData transaction, bool flushToDisk = false) {
        return Task.FromResult(Execute(transaction, flushToDisk));
    }
    public TransactionResult Execute(TransactionData transaction, bool flushToDisk = false) {
        if (transaction.Actions.Count == 0) return TransactionResult.Empty; // nothing to do
        if (_queryLogger.Enabled) {
            var sw = Stopwatch.StartNew();
            var transId = execute_withTransformOption(transaction, true, flushToDisk, out var primitiveActionCount);
            if (_queryLogger.Enabled) {
                foreach (var a in transaction.Actions) _queryLogger.RecordAction(transId.TransactionId, a.OperationName(), a.ToString());
                _queryLogger.RecordTransaction(transId.TransactionId, sw.Elapsed.TotalMilliseconds, transaction.Actions.Count, primitiveActionCount, _settings.ForceDiskFlushOnEveryTransaction || flushToDisk);
            }
            return transId;
        } else {
            return execute_withTransformOption(transaction, true, flushToDisk, out _);
        }
    }

    TransactionResult execute_withTransformOption(TransactionData transaction, bool transformValues, bool flushToDisk, out int primitiveActionCount) {
        _lock.EnterWriteLock();
        updateCurrentActivity(DataStoreActivityCategory.Executing, "Executing transaction", 0);
        try {
            validateDatabaseState();
            if (transaction.Timestamp <= _log.LastTimestamp) {
                if (transaction.Timestamp > 0) throw new ExceptionWithoutIntegrityLoss("Invalid timestamp value. Transactions can only be executed once. ");
                transaction.Timestamp = _log.NewTimestamp();
            }
            var newTasks = new List<KeyValuePair<TaskData, string?>>();
            var resultingOperations = new ResultingOperation[transaction.Actions.Count];
            execute(transaction, transformValues, _settings.ForceDiskFlushOnEveryTransaction || flushToDisk, out primitiveActionCount, resultingOperations, newTasks);
            foreach (var t in newTasks) EnqueueTask(t.Key, t.Value);
            return new TransactionResult(transaction.Timestamp, resultingOperations);
        } catch (ExceptionWithoutIntegrityLoss err) {
            // database state is ok, entiere transaction is cancelled and any changes have been rolled back
            LogError("Transaction Error. ", err);
            throw;
        } catch (Exception err) {
            _state = DataStoreState.Error;
            logCriticalTransactionError("Critical Transaction Error. ", err, transaction);
            throw new Exception("Critical error. Database left in unknown state. Restart required. ", err);
        } finally {
            endCurrentActivity();
            _lock.ExitWriteLock();
        }
    }
    void execute(TransactionData transaction, bool transformValues, bool flushToDisk, out int primitiveActionCount, ResultingOperation[] resultingOperations, List<KeyValuePair<TaskData, string?>> newTasks) {
        HashSet<Guid>? lockExcemptions = null;
        if (transaction.LockExcemptions != null) {
            if (!_nodeWriteLocks.LocksAreActive(transaction.LockExcemptions))
                throw new ExceptionWithoutIntegrityLoss("Required locks IDs: " +
                    string.Join(", ", transaction.LockExcemptions) + " are no longer active. ");
            lockExcemptions = new(transaction.LockExcemptions);
        }
        var executed = executeActions(transaction, lockExcemptions, transformValues, resultingOperations, newTasks); // may encounter invalid data, then reverse actions and throw ExceptionWithoutIntegrityLoss
        primitiveActionCount = executed.Count;
        if (executed.Count == 0) {
            if (flushToDisk) _log.FlushToDisk();
            return; // no actions executed, no need to write to disk
        }
        var executedTransaction = new ExecutedPrimitiveTransaction(executed, transaction.Timestamp);
        _log.QueDiskWrites(executedTransaction);
        _rewriter?.RegisterNewTransactionWhileRewriting(executedTransaction);
        if (flushToDisk) _log.FlushToDisk();
        _noPrimitiveActionsSinceLastStateSnaphot += primitiveActionCount;
        _noPrimitiveActionsSinceClearCache += primitiveActionCount;
        _noTransactionsSinceLastStateSnaphot++;
        _noTransactionsSinceClearCache++;
        _noPrimitiveActionsInLogThatCanBeTruncated += executed.Count(a => a.Operation == PrimitiveOperation.Remove);
    }
    List<PrimitiveActionBase> executeActions(TransactionData transaction, HashSet<Guid>? lockExcemptions, bool transformValues, ResultingOperation[] resultingOperations, List<KeyValuePair<TaskData, string?>> newTasks) {
        // will attempt to execute all actions, if any fails, it will reverse all executed actions and throw ExceptionWithoutIntegrityLoss
        var executed = new List<PrimitiveActionBase>();
        try {
            _guids.StartRecordingNewIds(); // can be cancelled later in case transaction fails so that new IDs are not wasted
            bool anyLocks = _nodeWriteLocks.AnyLocks();
            var i = 0;
            var count = transaction.Actions.Count;
            foreach (var action in transaction.Actions) {
                updateCurrentActivity(100 * i++ / count);
                foreach (var a in ActionFactory.Convert(this, action, transformValues, newTasks, out var resultingOperation)) {
                    if (anyLocks) validateLocks(a, lockExcemptions);
                    executeAction(a); // safe errors might occur if constraints are violated ( typically for relations or unique value constraints )
                    executed.Add(a);
                    resultingOperations[i - 1] = resultingOperation;
                }
            }
            _guids.CommitNewIds();
            if (transaction.InnerCallbackBeforeCommitting != null) {
                try {
                    transaction.InnerCallbackBeforeCommitting();
                } catch (Exception err) {
                    throw new ExceptionWithoutIntegrityLoss("Transaction callback failed: " + err.Message, err);
                }
            }
            return executed;
        } catch (ExceptionWithoutIntegrityLoss) { // rollback
            // rollback with oposite actions in reverse order:
            for (var n = executed.Count - 1; n >= 0; n--) {
                // Console.WriteLine("Rollback: " + executed[n]);
                executeAction(executed[n].Opposite());
            }
            _guids.CancelNewIds();
            throw;
        }
    }
    void validateLocks(PrimitiveActionBase a, HashSet<Guid>? transactionLocks) {
        if (transactionLocks != null) {
            foreach (var id in transactionLocks) {
                if (!_nodeWriteLocks.LockIsActive(id)) throw new ExceptionWithoutIntegrityLoss("Required lock with ID: " + id + " is no longer active. ");
            }
        }
        if (a is PrimitiveNodeAction na) {
            if (_nodeWriteLocks.IsLocked(na.Node.__Id, transactionLocks)) throw new ExceptionWithoutIntegrityLoss("Node with ID: " + na.Node.__Id + " is locked and cannot be modified with action: " + na.ToString());
        } else if (a is PrimitiveRelationAction ra) {
            if (_nodeWriteLocks.IsLocked(ra.Source, transactionLocks)) throw new ExceptionWithoutIntegrityLoss("Node with ID: " + ra.Source + " is locked and cannot have relations changed. ");
            if (_nodeWriteLocks.IsLocked(ra.Target, transactionLocks)) throw new ExceptionWithoutIntegrityLoss("Node with ID: " + ra.Target + " is locked and cannot have relations changed. ");
        }
    }
    void executeAction(PrimitiveActionBase action) {
        if (action is PrimitiveNodeAction na) executeNodeAction(na);
        else if (action is PrimitiveRelationAction ra) executeRelationAction(ra);
        else throw new NotImplementedException();
    }
    void executeNodeAction(PrimitiveNodeAction action) {
        switch (action.Operation) {
            case PrimitiveOperation.Add: {
                    if (_nodes.Contains(action.Node.__Id))
                        throw new NodeConstraintException("A node with the ID: " + action.Node.__Id + "(" + action.Node.Id + ") already exists. ", action.Node.Id);
                    if (_index.WillUniqueConstraintsBeViolated(action.Node, out var p)) {
                        var propName = Datamodel.NodeTypes[action.Node.NodeType].ToString() + "." + p.CodeName;
                        var value = action.Node.TryGetValue(p.Id, out var v) ? v : "";
                        throw new ValueConstraintException("The value: \"" + value + "\" of " + propName + " is not unique for node with ID: " + action.Node.__Id + "(" + action.Node.Id + ")", p.Id);
                    }
                    _nodes.Add(action.Node, action.Segment);
                    _index.Add(action.Node);
                }
                break;
            case PrimitiveOperation.Remove: {
                    if (!_nodes.Contains(action.Node.__Id)) throw new ExceptionWithoutIntegrityLoss("Cannot remove unknown node: " + action.Node);
                    _nodes.Remove(action.Node, out var segmentRemoved);
                    action.Segment = segmentRemoved; // must keep segment to be able to execute oposite action later
                    _index.Remove(action.Node);
                }
                break;
            default:
                break;
        }
    }
    void executeRelationAction(PrimitiveRelationAction action) {
        _relations.RegisterAction(action);
    }
    void validateDatabaseState() {
        if (_state != Common.DataStoreState.Open) throw new Exception("Store not opened. Current state is: " + _state);
    }
    //public Task<object> IncrementValueAsync(Guid nodeId, Guid propertyId, object value, bool flushToDisk = false) {
    //    return IncrementValueAsync(_guids.GetId(nodeId), propertyId, value, flushToDisk);
    //}
    //public Task<object> IncrementValueAsync(int nodeId, Guid propertyId, object value, bool flushToDisk = false) {
    //    var lockId = RequestLockAsync(nodeId, 1000, 2000).Result;
    //    try {
    //        var property = _definition.Properties[propertyId];
    //        var node = Get(nodeId);
    //        value = property.ForceValueType(value, out _);
    //        if (!node.TryGetValue(propertyId, out var v)) v = property.GetDefaultValue();
    //        if (v is int vint) v = vint + (int)value;
    //        else if (v is long vlong) v = vlong + (long)value;
    //        else if (v is float vfloat) v = vfloat + (float)value;
    //        else if (v is double vdouble) v = vdouble + (double)value;
    //        else if (v is decimal vdecimal) v = vdecimal + (decimal)value;
    //        else if (v is string vstring) v = vstring + (string)value;
    //        else throw new InvalidOperationException("Cannot add values of this datatype. ");
    //        node.AddOrUpdate(propertyId, v);
    //        var t = new TransactionData();
    //        t.UpdateNode(node);
    //        Execute(t);
    //        return Task.FromResult(v);
    //    } finally {
    //        ReleaseLock(lockId);
    //    }
    //}
    public Task<Guid> RequestGlobalLockAsync(double lockDurationInMs, double maxWaitTimeInMs) {
        _lock.EnterWriteLock();
        validateDatabaseState();
        try {
            return _nodeWriteLocks.RequestLockAsync(0, lockDurationInMs, maxWaitTimeInMs);
        } finally {
            _lock.ExitWriteLock();
        }
    }

    public Task<Guid> RequestLockAsync(Guid nodeId, double lockDurationInMs, double maxWaitTimeInMs) {
        return RequestLockAsync(_guids.GetId(nodeId), lockDurationInMs, maxWaitTimeInMs);
    }
    public Task<Guid> RequestLockAsync(int nodeId, double lockDurationInMs, double maxWaitTimeInMs) {
        _lock.EnterWriteLock();
        validateDatabaseState();
        try {
            if (lockDurationInMs > 60 * 1000) throw new Exception("Node write locks only supported up to 60 seconds. ");
            return _nodeWriteLocks.RequestLockAsync(nodeId, lockDurationInMs, maxWaitTimeInMs);
        } finally {
            _lock.ExitWriteLock();
        }
    }
    public void RefreshLock(Guid lockId) {
        _lock.EnterWriteLock();
        validateDatabaseState();
        try {
            _nodeWriteLocks.RefreshLock(lockId);
        } finally {
            _lock.ExitWriteLock();
        }
    }
    public void ReleaseLock(Guid lockId) {
        _lock.EnterWriteLock();
        validateDatabaseState();
        try {
            _nodeWriteLocks.Unlock(lockId);
        } finally {
            _lock.ExitWriteLock();
        }
    }
}
