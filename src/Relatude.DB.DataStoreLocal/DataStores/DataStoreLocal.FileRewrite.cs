using System.Collections.Concurrent;
using System.Security.Cryptography;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
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
    public async Task<RewriteFilesResult> RewriteFilesAsync(Guid fromStoreId, Guid toStoreId, Action<string, int>? onProgress = null, CancellationToken cancellationToken = default) {
        validateDatabaseState();
        var from = getFileStore(fromStoreId);
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
            var sameStore = from.Id == to.Id;
            var (hashLength, keepsOneCopy) = writesFilesAs(to);
            bool needsRewrite(FileValue v) => !sameStore || v.Hash.Length != hashLength || (keepsOneCopy && !FileValue.IsKeptByHash(v));
            bool isCandidate(FileValue v) => v.StorageId == from.Id && needsRewrite(v);

            // 1: which nodes have values to rewrite, and which stored files more than one value points at -
            // those are copied once and the copy shared, the rest are copied as they come
            report("Identifying types with file properties", 0);
            var nodeIds = nodeIdsThatMayContainFiles(cancellationToken) ?? [];
            var todo = new List<int>();
            var references = new Dictionary<Guid, int>();
            long bytesToCopy = 0;
            var values = new List<FileValue>();
            var inNode = new HashSet<Guid>();
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
                        foreach (var v in values) {
                            if (v.StorageId != from.Id) continue;
                            result.ValuesFound++;
                            if (!needsRewrite(v)) { result.ValuesUpToDate++; continue; }
                            // counted per node, the way a node lets go of its files below (revisions repeat them)
                            if (!inNode.Add(v.FileId)) continue;
                            if (references.TryGetValue(v.FileId, out var n)) {
                                references[v.FileId] = n + 1;
                            } else {
                                references[v.FileId] = 1;
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
            var copies = new Dictionary<Guid, Lazy<Task<FileInsertResult>>>();
            var copyLock = new object(); // guards shared and copies, which every worker reads and changes
            var reportLock = new object();
            int filesCopied = 0, nodesDone = 0;
            long bytesCopied = 0;
            Task<FileInsertResult> copyOf(FileValue v) {
                Lazy<Task<FileInsertResult>>? copy = null;
                lock (copyLock) {
                    // the first node to need a shared file copies it, the others wait for that copy
                    if (shared.ContainsKey(v.FileId) && !copies.TryGetValue(v.FileId, out copy)) {
                        copies[v.FileId] = copy = new Lazy<Task<FileInsertResult>>(() => copyFile(v));
                    }
                }
                return copy?.Value ?? copyFile(v);
            }
            // forgets a shared copy once every node pointing at it has been dealt with
            void doneWith(Guid fileId) {
                lock (copyLock) {
                    if (!shared.TryGetValue(fileId, out var left)) return;
                    if (left > 1) shared[fileId] = left - 1;
                    else { shared.Remove(fileId); copies.Remove(fileId); }
                }
            }
            async Task<FileInsertResult> copyFile(FileValue v) {
                FileInsertResult r;
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
                var found = new List<FileValue>();
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
                var targets = new Dictionary<Guid, FileInsertResult>();
                foreach (var v in found.Where(isCandidate).DistinctBy(v => v.FileId)) {
                    ct.ThrowIfCancellationRequested();
                    try {
                        targets[v.FileId] = await copyOf(v);
                    } catch (Exception err) {
                        // every value of the node pointing at it stays as it was: revisions repeat a file
                        foreach (var same in found.Where(f => isCandidate(f) && f.FileId == v.FileId)) fail(nodeGuid, nodeTypeId, same, err.Message);
                        doneWith(v.FileId);
                    }
                }
                if (targets.Count == 0) return;
                // under the node's lock, so nothing writes the node between reading it and updating it
                Guid lockId;
                try {
                    lockId = await RequestLockAsync(id, 30_000, 30_000);
                } catch (Exception err) {
                    foreach (var v in found.Where(v => targets.ContainsKey(v.FileId))) fail(nodeGuid, nodeTypeId, v, "The node could not be locked: " + err.Message);
                    foreach (var fileId in targets.Keys) doneWith(fileId);
                    return;
                }
                try {
                    var rewritten = 0;
                    // only a value still pointing at the copy that was read is changed
                    FileValue? replace(FileValue v) {
                        if (v.StorageId != from.Id || !targets.TryGetValue(v.FileId, out var r)) return null;
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
                    foreach (var v in found.Where(v => targets.ContainsKey(v.FileId))) fail(nodeGuid, nodeTypeId, v, err.Message);
                } finally {
                    ReleaseLock(lockId);
                    foreach (var fileId in targets.Keys) doneWith(fileId);
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
    /// <summary>Replaces the file values of a writable node copy, embedded objects included, with what
    /// <paramref name="replace"/> answers (null keeps a value). True when anything was replaced.</summary>
    static bool replaceFileValues(NodeDataAbstract node, Func<FileValue, FileValue?> replace) {
        var changed = false;
        foreach (var entry in node.Values.ToArray()) {
            if (entry.Value is FileValue value) {
                if (value.IsEmpty || replace(value) is not FileValue replacement) continue;
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
