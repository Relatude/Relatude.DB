using System.Text.Json.Serialization;

namespace Relatude.DB.Datamodels.Properties;
public enum RelationValueType {
    NotRelevant,
    Collection,
    Enumerable,
    List,
    Array,
    Native
}
public class RelationPropertyModel : PropertyModel {
    public override bool ExcludeFromTextIndex { get; set; }
    public bool TextIndexRelatedDisplayName { get; set; }
    public bool TextIndexRelatedContent { get; set; }
    public int TextIndexRecursiveLevelLimit { get; set; } = 1;
    public override string? GetDefaultDeclaration() {
        switch (RelationValueType) {
            case RelationValueType.NotRelevant:
            case RelationValueType.Collection:
            case RelationValueType.Enumerable:
            case RelationValueType.List:
            case RelationValueType.Array:
                return "[]"; // Collection is represented as an empty array
            case RelationValueType.Native:
                throw new NotSupportedException("Default declaration for native relation value type is not supported. Must be evaluated in the context of NodeStore. ");
            default:
                throw new ArgumentOutOfRangeException(nameof(RelationValueType), RelationValueType, "Unknown relation value type.");
        }
    }
    public override PropertyType PropertyType { get => PropertyType.Relation; }
    public RelationValueType RelationValueType { get; set; }
    public Guid RelationId { get; set; } = Guid.Empty;
    public bool IsMany { get; set; }
    public bool IsNative { get; set; }
    public bool FromTargetToSource { get; set; } // the type this property belongs to will represent the outbound side of the relation and point to the inbound side, if this proeprty is true
    public Guid NodeTypeOfRelated { get; set; } = Guid.Empty;
    public bool AutoAssigned { get; internal set; }

    public override object GetDefaultValue() => throw new NotSupportedException();
    public static object ForceCorrectValueType(object value) {
        throw new NotSupportedException();
    }
    public override string GetDefaultValueAsCode() => throw new NotSupportedException();

    internal string GetFullNameOfRelated(Datamodel m) {
        if (m.NodeTypes.TryGetValue(NodeTypeOfRelated, out var t)) return t.FullName;
        return base.GetFullName(m);
    }

}
