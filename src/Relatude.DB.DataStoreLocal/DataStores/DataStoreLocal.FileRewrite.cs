using System.Collections.Concurrent;
using System.Security.Cryptography;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Files;
using Relatude.DB.Transactions;
namespace Relatude.DB.DataStores;

public sealed partial class DataStoreLocal : IDataStore {
    /// <summary>
    /// Rewrites the files of one file store into another: every file value pointing into
    /// <paramref name="fromStoreId"/> - revisions and embedded objects included - gets a copy of its bytes
    /// written by <paramref name="toStoreId"/>, with that store's hash and layout and, where it keeps one copy
    /// per content, shared with identical files, and points at the copy from then on. Name, meta and extracted
    /// text stay, and no indexing is queued. Guid.Empty is the implicit store.
    /// <para>With the same store on both sides, only the values the store would now write differently are
    /// rewritten: a hash of another algorithm, or a file not yet kept by its hash - which is how files stored
    /// before SameHashSameFile or a new HashAlgorithm was turned on catch up.</para>
    /// <para>The database stays in use. Each node is updated under its own lock, and only the values that
    /// still point at the copy that was read are changed, so an upload replacing a file meanwhile wins. The
    /// old copies are left where they are, for the unreferenced file cleanup to remove: older versions in a
    /// node's history still point at them. Running it again picks up whatever is left, cancelled runs
    /// included.</para>
    /// </summary>
    public Task<RewriteFilesResult> RewriteFilesAsync(Guid fromStoreId, Guid toStoreId, Action<string, int>? onProgress = null, CancellationToken cancellationToken = default)
        => rewriteFilesAsync(fromStoreId, toStoreId, onProgress, cancellationToken);
    /// <summary>
    /// Brings the files that belong in <paramref name="toStoreId"/> into it, written the way it writes files
    /// now: every file value of a property whose uploads go to that store - the property names it, or names
    /// none and it is the default store - wherever its file is stored today, revisions and embedded objects
    /// included. A value already in the store is rewritten only where the store would now write it
    /// differently, as with the same store on both sides of the other overload. Files of properties that
    /// upload into another store stay where they are.
    /// <para>One call for both reasons to rewrite: moving files onto a store that took over - a new default,
    /// blob storage - and catching old files up with a hash or one copy per content turned on later.
    /// Everything else is as with <see cref="RewriteFilesAsync(Guid, Guid, Action{string, int}?, CancellationToken)"/>.</para>
    /// </summary>
    public Task<RewriteFilesResult> RewriteFilesAsync(Guid toStoreId, Action<string, int>? onProgress = null, CancellationToken cancellationToken = default)
        => rewriteFilesAsync(null, toStoreId, onProgress, cancellationToken);
    // without a store to read from, the values that belong in the target are read from wherever they are
    async Task<RewriteFilesResult> rewriteFilesAsync(Guid? fromStoreId, Guid toStoreId, Action<string, int>? onProgress, CancellationToken cancellationToken) {
        validateDatabaseState();
        if (fromStoreId is Guid fromId) getFileStore(fromId); // an unknown store fails before anything is read
        var to = getFileStore(toStoreId);
        var activityId = RegisterActvity(DataStoreActivityCategory.RunningTask, "Rewriting files");
        try {
            var lastPct = -1;
            var lastDescription = string.Empty;
            void report(string description, int pct) {
                if (pct == lastPct && description == lastDescription) return;
                lastPct = pct;
                lastDescription = description;
                UpdateActivity(activityId, description, pct);
                onProgress?.Invoke(description, pct);
            }
            var result = new RewriteFilesResult { FromStoreId = fromStoreId, ToStoreId = toStoreId };
            var failures = new List<RewriteFileFailure>();
            var (hashLength, keepsOneCopy) = writesFilesAs(to);
            // where each file property's uploads go now: the store it names, or the default one. Built once,
            // as the workers below read it side by side
            var homes = Datamodel.Properties.Values.OfType<FilePropertyModel>()
                .ToDictionary(f => f.Id, f => f.FileStorageProviderId != Guid.Empty ? f.FileStorageProviderId : _defaultFileStore.Id);
            bool belongs(Guid propertyId, FileValue v) => fromStoreId is Guid fromId
                ? v.StorageId == fromId
                : (homes.TryGetValue(propertyId, out var home) ? home : _defaultFileStore.Id) == to.Id;
            bool needsRewrite(FileValue v) => v.StorageId != to.Id || v.Hash.Length != hashLength || (keepsOneCopy && !FileValue.IsKeptByHash(v));
            bool isCandidate(Guid propertyId, FileValue v) => belongs(propertyId, v) && needsRewrite(v);
            // a stored file is its store and its id: two stores keeping one copy per content give the same
            // bytes the same id
            static (Guid, Guid) storedFile(FileValue v) => (v.StorageId, v.FileId);

            // 1: which nodes have values to rewrite, and which stored files more than one value points at -
            // those are copied once and the copy shared, the rest are copied as they come
            report("Identifying types with file properties", 0);
            var nodeIds = nodeIdsThatMayContainFiles(cancellationToken) ?? [];
            var todo = new List<int>();
            var references = new Dictionary<(Guid, Guid), int>();
            long bytesToCopy = 0;
            var values = new List<(Guid PropertyId, FileValue Value)>();
            var inNode = new HashSet<(Guid, Guid)>();
            const int batchSize = 1000;
            for (var offset = 0; offset < nodeIds.Length; offset += batchSize) {
                cancellationToken.ThrowIfCancellationRequested();
                var end = Math.Min(offset + batchSize, nodeIds.Length);
                _lock.EnterReadLock();
                try {
                    for (var i = offset; i < end; i++) {
                        if (!_nodes.TryGet(nodeIds[i], out var node, out _)) continue;
                        values.Clear();
                        collectFileValues(node, values);
                        inNode.Clear();
                        foreach (var (propertyId, v) in values) {
                            if (!belongs(propertyId, v)) continue;
                            result.ValuesFound++;
                            if (!needsRewrite(v)) { result.ValuesUpToDate++; continue; }
                            // counted per node, the way a node lets go of its files below (revisions repeat them)
                            var stored = storedFile(v);
                            if (!inNode.Add(stored)) continue;
                            if (references.TryGetValue(stored, out var n)) {
                                references[stored] = n + 1;
                            } else {
                                references[stored] = 1;
                                bytesToCopy += v.Size;
                            }
                        }
                        if (inNode.Count > 0) todo.Add(nodeIds[i]);
                    }
                } finally {
                    _lock.ExitReadLock();
                }
                result.NodesScanned = end;
                report("Finding the files to rewrite", (int)(end * 10L / nodeIds.Length));
            }
            // a file only one node points at is copied for that node and forgotten
            var shared = references.Where(r => r.Value > 1).ToDictionary(r => r.Key, r => r.Value);
            references.Clear();
            if (todo.Count == 0) {
                report("Nothing to rewrite", 100);
                return result;
            }

            // 2: per node, copy its files, then point its values at the copies
            var copies = new Dictionary<(Guid, Guid), Lazy<Task<FileInsertResult>>>();
            var copyLock = new object(); // guards shared and copies, which every worker reads and changes
            var reportLock = new object();
            int filesCopied = 0, nodesDone = 0;
            long bytesCopied = 0;
            Task<FileInsertResult> copyOf(FileValue v) {
                Lazy<Task<FileInsertResult>>? copy = null;
                var stored = storedFile(v);
                lock (copyLock) {
                    // the first node to need a shared file copies it, the others wait for that copy
                    if (shared.ContainsKey(stored) && !copies.TryGetValue(stored, out copy)) {
                        copies[stored] = copy = new Lazy<Task<FileInsertResult>>(() => copyFile(v));
                    }
                }
                return copy?.Value ?? copyFile(v);
            }
            // forgets a shared copy once every node pointing at it has been dealt with
            void doneWith((Guid, Guid) stored) {
                lock (copyLock) {
                    if (!shared.TryGetValue(stored, out var left)) return;
                    if (left > 1) shared[stored] = left - 1;
                    else { shared.Remove(stored); copies.Remove(stored); }
                }
            }
            async Task<FileInsertResult> copyFile(FileValue v) {
                FileInsertResult r;
                var from = getFileStore(v.StorageId);
                if (from.TryGetLocalFilePath(v, out var localPath)) {
                    await using var file = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, true);
                    if (file.Length != v.Size) throw new Exception("The stored file is " + file.Length + " bytes, not the " + v.Size + " the value records. ");
                    r = await to.InsertAsync(Guid.NewGuid(), file, v.Name);
                } else {
                    await using var stream = new KnownLengthStream(await from.GetFileStream(v), v.Size);
                    r = await to.InsertAsync(Guid.NewGuid(), stream, v.Name);
                }
                if (r.Length != v.Size) throw new Exception("The copy is " + r.Length + " bytes, not the " + v.Size + " the value records. ");
                Interlocked.Increment(ref filesCopied);
                Interlocked.Add(ref bytesCopied, r.Length);
                return r;
            }
            void fail(Guid nodeId, Guid nodeTypeId, FileValue v, string reason) {
                lock (failures) {
                    result.FailedCount++;
                    if (failures.Count < RewriteFilesResult.MaxListed) {
                        failures.Add(new RewriteFileFailure {
                            NodeId = nodeId,
                            NodeType = Datamodel.NodeTypes.TryGetValue(nodeTypeId, out var t) ? t.FullName : nodeTypeId.ToString(),
                            Property = v.PropertyPath != null && Datamodel.Properties.TryGetValue(v.PropertyPath.PropertyId, out var p) ? p.CodeName : string.Empty,
                            FileName = v.Name,
                            Size = v.Size,
                            Reason = reason,
                        });
                    } else {
                        result.ListTruncated = true;
                    }
                }
            }
            async Task rewriteNode(int id, CancellationToken ct) {
                // the values as they are now: the node may have changed since it was counted
                var found = new List<(Guid PropertyId, FileValue Value)>();
                Guid nodeGuid, nodeTypeId;
                _lock.EnterReadLock();
                try {
                    if (!_nodes.TryGet(id, out var node, out _)) return; // deleted meanwhile
                    nodeGuid = node.Id;
                    nodeTypeId = node.NodeType;
                    collectFileValues(node, found);
                } finally {
                    _lock.ExitReadLock();
                }
                var candidates = found.Where(f => isCandidate(f.PropertyId, f.Value)).Select(f => f.Value).ToList();
                var targets = new Dictionary<(Guid, Guid), FileInsertResult>();
                foreach (var v in candidates.DistinctBy(storedFile)) {
                    ct.ThrowIfCancellationRequested();
                    try {
                        targets[storedFile(v)] = await copyOf(v);
                    } catch (Exception err) {
                        // every value of the node pointing at it stays as it was: revisions repeat a file
                        foreach (var same in candidates.Where(f => storedFile(f) == storedFile(v))) fail(nodeGuid, nodeTypeId, same, err.Message);
                        doneWith(storedFile(v));
                    }
                }
                if (targets.Count == 0) return;
                // under the node's lock, so nothing writes the node between reading it and updating it
                Guid lockId;
                try {
                    lockId = await RequestLockAsync(id, 30_000, 30_000);
                } catch (Exception err) {
                    foreach (var v in candidates.Where(v => targets.ContainsKey(storedFile(v)))) fail(nodeGuid, nodeTypeId, v, "The node could not be locked: " + err.Message);
                    foreach (var stored in targets.Keys) doneWith(stored);
                    return;
                }
                try {
                    var rewritten = 0;
                    // only a value still pointing at the copy that was read is changed
                    FileValue? replace(Guid propertyId, FileValue v) {
                        if (!isCandidate(propertyId, v) || !targets.TryGetValue(storedFile(v), out var r)) return null;
                        rewritten++;
                        return v.CopyWithStoredFile(to.Id, r.FileId, r.StoreKey, r.FileHash, r.Length);
                    }
                    var t = new TransactionData { LockExcemptions = [lockId] };
                    void update(INodeDataExternal copy) {
                        var action = NodeAction.ForceUpdate(copy);
                        action.NoReindex = true; // the files moved, their content did not
                        t.Add(action);
                    }
                    _lock.EnterReadLock();
                    try {
                        if (_nodes.TryGet(id, out var node, out _)) {
                            if (node is NodeDataRevisions revisions) {
                                // one update per revision: an update names the revision it replaces
                                foreach (var revision in revisions.Revisions) {
                                    var copy = revision.CopyRevision();
                                    if (replaceFileValues(copy, replace)) update(copy);
                                }
                            } else if (node is NodeData data) {
                                var copy = data.Copy();
                                if (replaceFileValues(copy, replace)) update(copy);
                            }
                        }
                    } finally {
                        _lock.ExitReadLock();
                    }
                    if (t.Actions.Count > 0) {
                        Execute(t, false, false, _defaultQueryCtx);
                        lock (failures) result.ValuesRewritten += rewritten;
                    }
                } catch (Exception err) {
                    foreach (var v in candidates.Where(v => targets.ContainsKey(storedFile(v)))) fail(nodeGuid, nodeTypeId, v, err.Message);
                } finally {
                    ReleaseLock(lockId);
                    foreach (var stored in targets.Keys) doneWith(stored);
                }
            }
            // a few files at a time keeps blob storage busy; a SingleFile store appends one file after another
            var parallel = to is MultiFileStore ? 4 : 1;
            var description = "Rewriting the files of " + todo.Count + (todo.Count == 1 ? " node" : " nodes");
            await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = cancellationToken }, async (id, ct) => {
                await rewriteNode(id, ct);
                var done = Interlocked.Increment(ref nodesDone);
                lock (reportLock) {
                    report(description + " · " + Volatile.Read(ref bytesCopied).ToByteString() + " of " + bytesToCopy.ToByteString() + " copied",
                        10 + (int)(done * 90L / todo.Count));
                }
            });
            result.FilesCopied = filesCopied;
            result.BytesCopied = bytesCopied;
            result.Failures = [.. failures];
            report("Rewrite completed", 100);
            return result;
        } finally {
            DeRegisterActivity(activityId);
        }
    }
    /// <summary>How a store writes a file now: the length of its hash in hex, and whether it keeps one
    /// copy per content. A SingleFile store always hashes with MD5 and shares nothing.</summary>
    static (int HashLength, bool KeepsOneCopy) writesFilesAs(IFileStore store) {
        if (store is not MultiFileStore multi) return (32, false);
        var hashLength = multi.HashAlgorithm == HashAlgorithmName.SHA256 ? 64 : 32;
        return (hashLength, multi.SameHashSameFile);
    }
    /// <summary>Every file value of a node with the id of the property holding it: all revisions, embedded
    /// objects included.</summary>
    static void collectFileValues(INodeData node, List<(Guid PropertyId, FileValue Value)> into) {
        if (node is NodeDataRevisions revisions) { // revision containers hold no values of their own
            foreach (var revision in revisions.Revisions) collectFileValues(revision, into);
            return;
        }
        foreach (var entry in node.Values) {
            if (entry.Value is FileValue fileValue) {
                if (!fileValue.IsEmpty) into.Add((entry.PropertyId, fileValue));
            } else if (entry.Value is IInnerNodeDataMap innerNodes) {
                foreach (var inner in innerNodes) collectFileValues(inner, into);
            }
        }
    }
    /// <summary>Replaces the file values of a writable node copy, embedded objects included, with what
    /// <paramref name="replace"/> answers for the property and the value (null keeps a value). True when
    /// anything was replaced.</summary>
    static bool replaceFileValues(NodeDataAbstract node, Func<Guid, FileValue, FileValue?> replace) {
        var changed = false;
        foreach (var entry in node.Values.ToArray()) {
            if (entry.Value is FileValue value) {
                if (value.IsEmpty || replace(entry.PropertyId, value) is not FileValue replacement) continue;
                node.AddOrUpdate(entry.PropertyId, replacement);
                changed = true;
            } else if (entry.Value is IInnerNodeDataMap inner) {
                var copy = inner.Copy(); // the stored map is read-only and shared; its copy holds copies of the inner nodes
                var innerChanged = false;
                foreach (var innerNode in copy) innerChanged |= replaceFileValues(innerNode, replace);
                if (!innerChanged) continue;
                node.AddOrUpdate(entry.PropertyId, copy);
                changed = true;
            }
        }
        return changed;
    }
    /// <summary>A stream of a known length over one that cannot tell its own, which is what a file store
    /// insert needs to read another store's file.</summary>
    sealed class KnownLengthStream(Stream inner, long length) : Stream {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
