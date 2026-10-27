using Microsoft.CodeAnalysis;
using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.Datamodels;

public enum RevisionType : int {
    Published = 0, // id always 0. Only one per culture! and only one indexed
    PublishOverride = 1, // id from 10000000->19999999 - used for AB testing, campaigns, etc.
    Archived = 2, // id from 20000000->29999999
    Binned = 3, // id from 30000000->39999999
    Preliminary = 4, // id from 40000000->49999999 - not published, but can be used for preview, staging, etc.
    AwaitingPublicationApproval = 5, // id from 50000000->59999999 - not published, waiting for approval to be published
    AwaitingArchiveApproval = 6, // id from 60000000->69999999 - published, but waiting for approval to be archived
    AwaitingDeleteApproval = 7 // id from 70000000->79999999 - not published, waiting for approval to be deleted
}
public static class RevisionUtil {
    /// <summary>How many keys each revision type has: its keys are type * KeysPerType and up.</summary>
    public const int KeysPerType = 10_000_000;

    /// <summary>
    /// The key a new revision of the type gets: the first of the type's range that no revision of the node has, as
    /// keys are unique across all of a node's revisions. A published revision is one per culture, so a node published
    /// in three cultures has the published keys 0, 1 and 2.
    /// </summary>
    public static int CreateNewRevisionKey(RevisionType keyType, Guid cultureId, NodeDataRevision[] existingRevs) {
        if (keyType == RevisionType.Published) {
            // ensure there is not already a published revision for the given culture
            var hasPublishedRevisionForCulture = existingRevs.Any(r => r.RevisionType == RevisionType.Published && r.CultureId == cultureId);
            if (hasPublishedRevisionForCulture) throw new ArgumentException($"There can only be one published revision for each culture. Culture ID: {cultureId}. ");
        }
        var taken = existingRevs.Select(r => r.RevisionKey).ToHashSet();
        int newKey = (int)keyType * KeysPerType; // start of the range for the given revision type
        while (taken.Contains(newKey)) newKey++; // the next available key in the range for the given revision type
        if (newKey >= ((int)keyType + 1) * KeysPerType) throw new InvalidOperationException($"No revision key is left for {keyType}. ");
        return newKey;
    }

    /// <summary>The type a key is of: the range it lies in, so 0 to 9 999 999 are published, 20 000 000 and up archived.</summary>
    public static RevisionType GetRevisionTypeFromKey(int revisionKey) {
        if (revisionKey < 0) throw new ArgumentException($"Invalid revision ID: {revisionKey}. A revision key is not negative. ");
        var type = revisionKey / KeysPerType;
        if (!Enum.IsDefined(typeof(RevisionType), type)) throw new ArgumentException($"Invalid revision ID: {revisionKey}. No corresponding RevisionType found. ");
        return (RevisionType)type;
    }
    public static void Validate(int revisionId) {
        GetRevisionTypeFromKey(revisionId);
    }
}

public class NodeDataRevision : NodeDataAbstract, INodeDataExternal {
    public Guid CultureId => Meta?.CultureId ?? Guid.Empty;
    public Guid CollectionId => Meta?.CollectionId ?? Guid.Empty;
    public int RevisionKey => Meta?.RevisionKey ?? 0; // used for internal meta dictionary to save memory, the meta must be unique for each revisions
    public Guid RevisionId { get; } // used for external references to revisions
    public RevisionType RevisionType => Meta?.RevisionType ?? RevisionType.Published;
    public NodeDataRevision(Guid guid, int id, Guid nodeType,
    DateTime createdUtc, DateTime changedUtc,
    Properties<object> values, IInnerNodeMeta? meta, Guid revisionId)
    : base(guid, id, nodeType, createdUtc, changedUtc, values, meta) {
        RevisionId = revisionId;
    }
    public NodeDataRevision CopyAndChangeMetaAndRevisionId(IInnerNodeMeta? newMeta, Guid revisionGuid) {
        return new NodeDataRevision(Id, __Id, NodeType, CreatedUtc, ChangedUtc, new(_values), newMeta, revisionGuid);
    }
    public NodeDataRevision CopyAndChangeMeta(IInnerNodeMeta? newMeta) {
        return new NodeDataRevision(Id, __Id, NodeType, CreatedUtc, ChangedUtc, new(_values), newMeta, RevisionId);
    }
    public NodeDataRevision CopyRevision() => new(Id, __Id, NodeType, CreatedUtc, ChangedUtc, new(_values), Meta, RevisionId);
    public INodeDataExternal CopyExternal() => CopyRevision();

    public NodeData CopyAndConvertToNodeData() {
        var meta = Meta;
        if (Meta != null && Meta.RevisionType != RevisionType.Published) {
            meta = IInnerNodeMeta.ChangeRevision(Meta, 0); // when converting to NodeData, the revision type is always set to Published and revision key to 0, 
        }
        var data = new NodeData(Id, __Id, NodeType, CreatedUtc, ChangedUtc, new(_values), meta);
        return data;
    }

}
public class NodeDataRevisions : INodeDataInternal {
    public NodeDataRevisions(Guid guid, int id, Guid typeId, NodeDataRevision[] revisions) {
        _id = id;
        _guid = guid;
        NodeType = typeId;

        // ensure the there only exists at max one published revision for each culture:
        var publishedRevisions = revisions.Where(r => r.RevisionType == RevisionType.Published);
        var publishedRevisionsByCulture = publishedRevisions.GroupBy(r => r.Meta?.CultureId);
        var hasMultiplePublishedRevisionsForSameCulture = publishedRevisionsByCulture.Any(g => g.Count() > 1);
        if (hasMultiplePublishedRevisionsForSameCulture) throw new ArgumentException("There can only be one published revision for each culture. ");

        // ensure revision key is unique across all revisions:
        var hasDuplicateRevisionKeys = revisions.GroupBy(r => r.RevisionKey).Any(g => g.Count() > 1);
        if (hasDuplicateRevisionKeys) throw new ArgumentException("Revision keys must be unique across all revisions. ");

        // ensure revision guid is unique across all revisions:
        var hasDuplicateRevisionGuids = revisions.GroupBy(r => r.RevisionId).Any(g => g.Count() > 1);
        if (hasDuplicateRevisionGuids) throw new ArgumentException("Revision GUIDs must be unique across all revisions. ");


        // TODO: optimize the above checks for better perfomance

        Revisions = revisions;
    }
    int _id;
    public int __Id { get => _id; set => throw new NA(); }
    public NodeDataRevision[] Revisions { get; }
    //public string[]? Log{ get; }
    Guid _guid;
    public Guid Id { get => _guid; set => throw new NA(); }
    public Guid NodeType { get; }
    public IInnerNodeMeta? Meta => throw new NA();
    public DateTime ChangedUtc => throw new NA();
    public DateTime CreatedUtc { get => throw new NA(); set => throw new NA(); }
    public INodeDataInternal CopyInternal() => CopyRevisions();
    public NodeDataRevisions CopyRevisions() {
        var revs = new NodeDataRevision[Revisions.Length];
        for (int i = 0; i < Revisions.Length; i++) revs[i] = Revisions[i].CopyRevision();
        var data = new NodeDataRevisions(Id, __Id, NodeType, revs);
        return data;
    }

    public IEnumerable<PropertyEntry<object>> Values => throw new NA();
    public bool ReadOnly => true;
    public IRelations Relations => throw new NA();
    public int ValueCount => throw new NA();
    public void Add(Guid propertyId, object value) => throw new NA();
    public void AddOrUpdate(Guid propertyId, object value) => throw new NA();
    public bool Contains(Guid propertyId) => throw new NA();
    public void EnsureReadOnly() {
        foreach (var revs in Revisions) revs.EnsureReadOnly();
    }
    public void RemoveIfPresent(Guid propertyId) => throw new NA();
    public bool TryGetValue(Guid propertyId, [MaybeNullWhen(false)] out object value) => throw new NA();
    public bool TryGetValue<T>(Guid propertyId, [MaybeNullWhen(false)] out T value) => throw new NA();
    public string? DisplayName { get => throw new NA(); set => throw new NA(); }
    public string? Address { get => throw new NA(); set => throw new NA(); }
    public bool AutoAddress { get => throw new NA(); set => throw new NA(); }

}
