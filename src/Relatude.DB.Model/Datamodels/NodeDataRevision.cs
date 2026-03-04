using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.Datamodels;

public enum RevisionType : int {
    AwaitingPublicationApproval = -2, // id from -20000000 => -29999999
    AwaitingArchiveApproval = -3, // id from -30000000 => -39999999
    AwaitingDeleteApproval = -4, // id from -40000000 => -49999999

    Preliminary = -1, // id from -10000000->-19999999

    Published = 0, // id always 0. Only one per culture! and only one indexed

    PublishOverride = 1, // id from 10000000->19999999 - used for AB testing, campaigns, etc.
    Archived = 2, // id from 20000000->29999999

    Binned = 3, // id from 30000000->39999999
}
public static class RevisionUtil {
    public static RevisionType GetRevisionType(int revisionId) {

        // is it big enough to have a digit in the place of 10 millions?
        var noDigitsOfRevisionId = revisionId == 0 ? 1 : (int)Math.Floor(Math.Log10(Math.Abs(revisionId)) + 1);
        if (revisionId != 0) if (noDigitsOfRevisionId > 8) throw new ArgumentException($"Invalid revision ID: {revisionId}. Revision ID must have at most 8 digits.");

        // get the digit in the place of 10 millions, which determines the revision type
        var isNegative = revisionId < 0;
        var digit = Math.Abs(revisionId);
        while (digit >= 10) digit /= 10;
        if (isNegative) digit = -digit;

        // check if the digit corresponds to a defined RevisionType
        if (!Enum.IsDefined(typeof(RevisionType), digit)) throw new ArgumentException($"Invalid revision ID: {revisionId}. No corresponding RevisionType found.");

        return (RevisionType)digit;
    }
    public static void Validate(int revisionId) {
        GetRevisionType(revisionId);
    }
}

public class NodeDataRevision : NodeDataAbstract, INodeDataOuter {
    public Guid CultureId => Meta?.CultureId ?? Guid.Empty;
    public int RevisionKey => Meta?.RevisionKey ?? 0; // used for internal meta dictionary to save memory, the meta must be unique for each revisions
    public Guid RevisionGuid { get; } // used for external references to revisions
    public RevisionType RevisionType => Meta?.RevisionType ?? RevisionType.Published;
    public NodeDataRevision(Guid guid, int id, Guid nodeType,
    DateTime createdUtc, DateTime changedUtc,
    Properties<object> values, INodeMeta? meta, Guid revisionGuid)
    : base(guid, id, nodeType, createdUtc, changedUtc, values, meta) {
        RevisionGuid = revisionGuid;
    }
    public NodeDataRevision CopyAndChangeMeta(INodeMeta? newMeta, Guid revisionGuid) {
        return new(Id, __Id, NodeType, CreatedUtc, ChangedUtc, new(_values), newMeta, revisionGuid);
    }
    public NodeDataRevision CopyRevision() => new(Id, __Id, NodeType, CreatedUtc, ChangedUtc, new(_values), Meta, RevisionGuid);
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

        // ensure revision id is unique across for each culture:
        var revisionsByCulture = revisions.GroupBy(r => r.Meta?.CultureId);
        var hasDuplicateRevisionIdsForSameCulture = revisionsByCulture.Any(g => g.Select(r => r.RevisionKey).GroupBy(id => id).Any(g2 => g2.Count() > 1));
        if (hasDuplicateRevisionIdsForSameCulture) throw new ArgumentException("Revision IDs must be unique for each culture. ");

        // ensure revision guid is unique across all revisions:
        var hasDuplicateRevisionGuids = revisions.GroupBy(r => r.RevisionGuid).Any(g => g.Count() > 1);
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
