using System.Diagnostics.CodeAnalysis;
using Relatude.DB.Common;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.IO;
using Relatude.DB.Serialization;
using Relatude.DB.Transactions;
using Relatude.DB.DataStores.Transactions;

namespace Relatude.DB.DataStores.Stores;
/// <summary>
/// Loads all transactions from log file, starting from given timestamp position
/// If throwErrors is true, then all errors are thrown as exceptions, otherwise they are added to errors list, 
/// and loading continues, all transaction are validated with a checksum
/// </summary>
/// <param name="fromTransactionAtPos"></param>
/// <param name="lastTimeStamp"></param>
/// <param name="throwOnErrors"></param>
/// <param name="errors"></param>
/// <exception cref="Exception"></exception>
internal class LogReader : IDisposable {
    IReadStream? _readStream;
    long _lastTimestampID;
    readonly Definition _definition;
    long _fileSize;
    public LogReader(string fileKey, Definition definition, IIOProvider io, long fromTransactionAtPos, long fromTimestamp, out Guid fileId) {
        fileId = Guid.Empty;
        _fileSize = io.GetFileSizeOrZeroIfUnknown(fileKey);
        if (_fileSize > 0) _readStream = verifyFileAndOpen(fileKey, io, fromTransactionAtPos, out fileId);
        _definition = definition;
        _lastTimestampID = fromTimestamp;
    }
    static IReadStream verifyFileAndOpen(string fileKey, IIOProvider io, long fromTransactionAtPos, out Guid fileId) {
        var readStream = io.OpenRead(fileKey, 0);
        readStream.ValidateMarker(LogStore._logStartMarker);
        var version = readStream.ReadVerifiedLong();
        if (version != LogStore._logVersioNumber) throw new IOException("Incompatible log file format version number. Expected version " + LogStore._logVersioNumber + " but found " + version + " .");
        fileId = readStream.ReadGuid();
        if (fromTransactionAtPos > 0) { // reopen at a specific position
            readStream.Dispose();
            readStream = io.OpenRead(fileKey, fromTransactionAtPos);
        }
        return readStream;
    }
    public long LastReadTimestamp { get => _lastTimestampID; }
    public bool ReadNextTransaction([MaybeNullWhen(false)] out ExecutedPrimitiveTransaction transaction, bool throwOnErrors, Action<string, Exception> log, out long byteSizeOfTransaction) {
        while (_readStream != null && _readStream.More()) {
            var foundMarker = _readStream.MoveToNextValidMarker(LogStore._transactionStartMarker);
            if (!foundMarker) break;
            try {
                transaction = tryReadNext(_readStream, _definition, ref _lastTimestampID, out byteSizeOfTransaction);
                return true;
            } catch (Exception error) {
                var errMsg = "Corruption or partially written transactions found in log file. " + error.Message + " Last valid timestamp UTC: " + new DateTime(_lastTimestampID, DateTimeKind.Utc);
                log(errMsg, error);
                if (throwOnErrors)
                    throw new LogReadException(errMsg, error);
            }
        }
        transaction = null;
        byteSizeOfTransaction = 0;
        return false;
    }
    static ExecutedPrimitiveTransaction tryReadNext(IReadStream readStream, Definition def, ref long lastTimestampID, out long byteSizeOfTransaction) {
        var startPosition = readStream.Position;
        var timestamp = readStream.ReadLong();
        var noActions = readStream.ReadVerifiedInt();
        var actions = new List<PrimitiveActionBase>(noActions);
        for (var i = 0; i < noActions; i++) {
            readStream.ValidateMarker(LogStore._actionMarker);
            var segmentStreamPosition = readStream.Position + 8; // add 8 as first byte is for array length, we only want exact position of node data bytes
            var actionData = readStream.ReadByteArray();
            var checkSum = readStream.ReadUInt();
            if (checkSum != actionData.GetChecksum()) throw new IOException("Data in log file is corrupted. ");
            var ms = new MemoryStream(actionData, false);
            var action = PFromBytes.ActionBase(def.Datamodel, ms, out var nodeSegmentRelativeOffset, out var nodeSegmenLength);
            if (action is PrimitiveNodeAction na) { // check that loaded node is of known type
                if (def.NodeTypes.ContainsKey(na.Node.NodeType)) {
                    var absolutePosition = segmentStreamPosition + nodeSegmentRelativeOffset;
                    na.Segment = new NodeSegment(absolutePosition, nodeSegmenLength);
                    actions.Add(action);
                } else {
                    // unknown node types are ignored
                }
            } else if (action is PrimitiveRelationAction ra) { // check that loaded relation is of known type
                if (def.Relations.ContainsKey(ra.RelationId)) {
                    actions.Add(action);
                } else {
                    // unknown relation types are ignored
                }
            } else { // cultures and collections....
                throw new NotSupportedException("Unknown action type: " + action.GetType().Name);
            }
        }
        readStream.ValidateMarker(LogStore._transactionEndMarker);
        var t = new ExecutedPrimitiveTransaction(actions, timestamp);
        if (lastTimestampID < t.Timestamp) lastTimestampID = t.Timestamp;
        byteSizeOfTransaction = readStream.Position - startPosition;
        return t;
    }
    public void Dispose() {
        _readStream?.Dispose();
        _readStream = null;
    }
    internal long FileSize => _fileSize;
    internal long Position => _readStream == null ? 0 : _readStream.Position;
}

