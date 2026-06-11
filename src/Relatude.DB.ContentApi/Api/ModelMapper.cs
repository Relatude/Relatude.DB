using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;

namespace Relatude.DB.ContentApi.Api;

// Translates the runtime Datamodel into a UI friendly description.
public static class ModelMapper {

    // Property types the API can read and write as plain JSON values.
    static readonly HashSet<PropertyType> _editableTypes = [
        PropertyType.String, PropertyType.Integer, PropertyType.Long,
        PropertyType.Double, PropertyType.Float, PropertyType.Decimal,
        PropertyType.Boolean, PropertyType.DateTime, PropertyType.DateTimeOffset,
        PropertyType.TimeSpan, PropertyType.Guid, PropertyType.StringArray,
    ];

    public static bool IsEditable(PropertyModel p) =>
        _editableTypes.Contains(p.PropertyType) && !IsSystemProperty(p);

    public static bool IsReadableValue(PropertyModel p) =>
        p.PropertyType is not (PropertyType.Relation or PropertyType.Embedded or PropertyType.ByteArray or PropertyType.FloatArray or PropertyType.File)
        && !IsSystemProperty(p);

    public static bool IsSystemProperty(PropertyModel p) => p.CodeName.StartsWith('_');

    public static ModelDto ToDto(Datamodel dm) {
        var types = new List<NodeTypeDto>();
        foreach (var type in dm.NodeTypes.Values) {
            if (type.Id == NodeConstants.BaseNodeTypeId) continue;
            var props = new List<PropertyDto>();
            foreach (var p in type.AllProperties.Values) {
                if (IsSystemProperty(p)) continue;
                props.Add(toDto(dm, p));
            }
            types.Add(new NodeTypeDto(
                type.Id,
                type.CodeName,
                type.FullName,
                Instantiable: !type.IsInterface,
                Parents: type.Parents.Where(p => p != NodeConstants.BaseNodeTypeId).ToList(),
                Properties: props
            ));
        }
        return new ModelDto(types.OrderBy(t => t.Name).ToList());
    }

    static PropertyDto toDto(Datamodel dm, PropertyModel p) {
        var isRelation = p.PropertyType == PropertyType.Relation;
        var relation = p as RelationPropertyModel;
        return new PropertyDto(
            p.Id,
            p.CodeName,
            p.PropertyType.ToString(),
            Editable: IsEditable(p),
            IsDisplayName: p.DisplayName,
            IsRelation: isRelation,
            IsMany: relation?.IsMany ?? false,
            RelatedTypeId: relation == null ? null : GetRelatedTypeId(dm, relation),
            Constraints: getConstraints(p)
        );
    }

    // The type on the other side of a relation property. NodeTypeOfRelated is not
    // reliable for explicitly declared (native) relation properties, so resolve the
    // opposite side from the relation definition itself when possible.
    public static Guid? GetRelatedTypeId(Datamodel dm, RelationPropertyModel relationProperty) {
        if (dm.Relations.TryGetValue(relationProperty.RelationId, out var relation)) {
            var otherSide = relationProperty.FromTargetToSource ? relation.SourceTypes : relation.TargetTypes;
            if (otherSide.Count > 0) return otherSide[0];
        }
        return relationProperty.NodeTypeOfRelated == Guid.Empty ? null : relationProperty.NodeTypeOfRelated;
    }

    static PropertyConstraintsDto? getConstraints(PropertyModel p) {
        switch (p) {
            case StringPropertyModel s:
                if (s.MinLength <= 0 && s.MaxLength == int.MaxValue && string.IsNullOrEmpty(s.RegularExpression)) return null;
                return new PropertyConstraintsDto(
                    s.MinLength > 0 ? s.MinLength : null,
                    s.MaxLength < int.MaxValue ? s.MaxLength : null,
                    string.IsNullOrEmpty(s.RegularExpression) ? null : s.RegularExpression,
                    null, null);
            case IntegerPropertyModel i when i.MinValue != int.MinValue || i.MaxValue != int.MaxValue:
                return new PropertyConstraintsDto(null, null, null,
                    i.MinValue != int.MinValue ? i.MinValue : null,
                    i.MaxValue != int.MaxValue ? i.MaxValue : null);
            case DoublePropertyModel d when d.MinValue != double.MinValue || d.MaxValue != double.MaxValue:
                return new PropertyConstraintsDto(null, null, null,
                    d.MinValue != double.MinValue ? d.MinValue : null,
                    d.MaxValue != double.MaxValue ? d.MaxValue : null);
            default:
                return null;
        }
    }
}
