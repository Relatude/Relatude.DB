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

    public static int CreateNewRevisionKey(RevisionType keyType, NodeDataRevision[] existingRevs) {
        var keysOfSameType = existingRevs.Select(r => r.RevisionKey).Where(k => GetRevisionTypeFromKey(k) == keyType).ToArray();
        int newKey = (int)keyType * 10000000; // start of the range for the given revision type
        if (keysOfSameType.Length == 0) return newKey;
        while (keysOfSameType.Contains(newKey)) newKey++; // find the next available key in the range for the given revision type
        return newKey;
    }

    public static RevisionType GetRevisionTypeFromKey(int revisionKey) {

        // is it big enough to have a digit in the place of 10 millions?
        var noDigitsOfRevisionId = revisionKey == 0 ? 1 : (int)Math.Floor(Math.Log10(Math.Abs(revisionKey)) + 1);
        if (revisionKey != 0) if (noDigitsOfRevisionId > 8) throw new ArgumentException($"Invalid revision ID: {revisionKey}. Revision ID must have at most 8 digits.");

        // get the digit in the place of 10 millions, which determines the revision type
        var digit = revisionKey;
        while (digit >= 10) digit /= 10;

        // check if the digit corresponds to a defined RevisionType
        if (!Enum.IsDefined(typeof(RevisionType), digit)) throw new ArgumentException($"Invalid revision ID: {revisionKey}. No corresponding RevisionType found.");

        return (RevisionType)digit;
    }
    public static void Validate(int revisionId) {
        GetRevisionTypeFromKey(revisionId);
    }
}

public class NodeDataRevision : NodeDataAbstract, INodeDataOuter {
    public Guid CultureId => Meta?.CultureId ?? Guid.Empty;
    public int RevisionKey => Meta?.RevisionKey ?? 0; // used for internal meta dictionary to save memory, the meta must be unique for each revisions
    public Guid RevisionId { get; } // used for external references to revisions
    public RevisionType RevisionType => Meta?.RevisionType ?? RevisionType.Published;
    public NodeDataRevision(Guid guid, int id, Guid nodeType,
    DateTime createdUtc, DateTime changedUtc,
    Properties<object> values, INodeMeta? meta, Guid revisionId)
    : base(guid, id, nodeType, createdUtc, changedUtc, values, meta) {
        RevisionId = revisionId;
    }
    public NodeDataRevision CopyAndChangeMetaAndRevisionId(INodeMeta? newMeta, Guid revisionGuid) {
        return new(Id, __Id, NodeType, CreatedUtc, ChangedUtc, new(_values), newMeta, revisionGuid);
    }
    public NodeDataRevision CopyRevision() => new(Id, __Id, NodeType, CreatedUtc, ChangedUtc, new(_values), Meta, RevisionId);
    public INodeDataOuter CopyOuter() => CopyRevision();

    public NodeData CopyAndConvertToNodeData() {
        var data = new NodeData(Id, __Id, NodeType, CreatedUtc, ChangedUtc, new(_values), Meta);
        return data;
    }

}
public class NodeDataRevisions : INodeDataInner {
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
    public INodeMeta? Meta => throw new NA();
    public DateTime ChangedUtc => throw new NA();
    public DateTime CreatedUtc { get => throw new NA(); set => throw new NA(); }
    public INodeDataInner CopyInner() => CopyRevisions();
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

}
