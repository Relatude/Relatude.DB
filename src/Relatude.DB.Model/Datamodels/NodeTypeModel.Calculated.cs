using System.Text;
using Relatude.DB.Datamodels.Properties;
namespace Relatude.DB.Datamodels {
    public partial class NodeTypeModel {
        // Calculated and not part of Json serialization: ( by default only {get set} properties are serialized )
        public readonly Dictionary<Guid, PropertyModel> AllProperties = [];
        public readonly Dictionary<string, PropertyModel> AllPropertiesByName = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, Guid> AllPropertyIdsByName = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<Guid, NodeTypeModel> ThisAndAllInheritedTypes = [];
        public readonly Dictionary<Guid, NodeTypeModel> ThisAndDescendingTypes = [];
        // per type, so they honour what this type and its base types override (PropertyOverrides):
        public readonly List<PropertyModel> TextIndexProperties = [];
        internal readonly Dictionary<Guid, object?> DefaultValues = [];
        internal readonly Dictionary<Guid, int> IndexBoosts = [];
        /// <summary>
        /// The value a node of this type reads for the property while it has none stored, and starts with
        /// when it is created: the property's default, unless this type or one of its base types overrides
        /// it. Use this rather than <see cref="PropertyModel.GetDefaultValue"/> wherever the node's type is
        /// known. Valid once the model is initialized.
        /// </summary>
        public object? GetDefaultValue(PropertyModel property) => DefaultValues.TryGetValue(property.Id, out var value) ? value : property.GetDefaultValue();
        /// <summary>The default this type gives the property in place of the property's own, when it overrides it.</summary>
        public bool TryGetOverriddenDefaultValue(Guid propertyId, out object? value) => DefaultValues.TryGetValue(propertyId, out value);
        /// <summary>How many extra times the property's text goes into this type's text index: the property's boost, unless the type overrides it.</summary>
        public int GetIndexBoost(PropertyModel property) => IndexBoosts.TryGetValue(property.Id, out var boost) ? boost : property.IndexBoost;
        /// <summary>Whether nodes of the type have a name: it inherits IName (<see cref="NodeConstants.NamePropertyId"/>).</summary>
        public bool HasName => AllProperties.ContainsKey(NodeConstants.NamePropertyId);
        /// <summary>What a node of this type stored under a former property is read as: this type's <see cref="FormerPropertyIds"/> and those of the types it inherits from.</summary>
        public readonly Dictionary<Guid, Guid> AllFormerPropertyIds = [];
        public override string ToString() {
            return (string.IsNullOrEmpty(Namespace) ? string.Empty : Namespace + ".") + CodeName;
        }
    }
}
