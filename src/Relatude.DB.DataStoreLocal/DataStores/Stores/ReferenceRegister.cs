using Relatude.DB.Common;
using Relatude.DB.DataStores.Indexes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.DB.DataStores.Stores;

[Flags]
public enum ReferenceSourceType {
    Reference = 1,
    Html = 2,
    Markdown = 4,
    LinkProperty = 8,
}
[Flags]
public enum ReferenceTarget {
    Node = 1,
    NodeAndCulture = 2,
    ExternalPage = 3,
    ExternalFile = 4,
    ExternalEmail = 5,
    ExternalOther = 6,
}
public class ReferenceInternal {
    public ReferenceSourceType SourceType { get; set; }
    public ReferenceTarget Target { get; set; }
    public byte[] From{ get; set; } 
    public byte[] To { get; set; }
}

internal class ReferenceRegister : IIndex {
    public string UniqueKey => throw new NotImplementedException();
    public string FriendlyName => throw new NotImplementedException();
    public long PersistedTimestamp => throw new NotImplementedException();

    public void Register(ReferenceInternal reference) => throw new NotImplementedException();
    public void Unregister(ReferenceInternal reference) => throw new NotImplementedException();
    public void UnregisterAllReferencesForNode(int nodeId) => throw new NotImplementedException();
    public IEnumerable<ReferenceInternal> GetReferencesToNode(int nodeId) => throw new NotImplementedException();
    public IEnumerable<ReferenceInternal> GetReferencesToNodeAndCulture(int nodeId, int cultureId) => throw new NotImplementedException();
    public IEnumerable<ReferenceInternal> GetReferencesFromNode(int nodeId) => throw new NotImplementedException();

    public void ClearCache() {
        throw new NotImplementedException();
    }

    public void CompressMemory() {
        throw new NotImplementedException();
    }

    public void Add(int id, object value) {
        throw new NotImplementedException();
    }

    public void Remove(int id, object value) {
        throw new NotImplementedException();
    }

    public void RegisterAddDuringStateLoad(int id, object value) {
        throw new NotImplementedException();
    }

    public void RegisterRemoveDuringStateLoad(int id, object value) {
        throw new NotImplementedException();
    }

    public void ReadStateForMemoryIndexes(Guid walFileId) {
        throw new NotImplementedException();
    }

    public void SaveStateForMemoryIndexes(long logTimestamp, Guid walFileId) {
        throw new NotImplementedException();
    }

    public void WriteNewTimestampDueToRewriteHotswap(long newTimestamp, Guid walFileId) {
        throw new NotImplementedException();
    }

    public void FlagFirstCommit() {
        throw new NotImplementedException();
    }

    public void Dispose() {
        throw new NotImplementedException();
    }
}
