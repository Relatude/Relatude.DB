using System.Text;
using WAF.Datamodels.Properties;

namespace WAF.Datamodels {
    public partial class NodeTypeModel {
        // Calculated and not part of Json serialization: ( by default only {get set} properties are serialized )
        public readonly Dictionary<Guid, PropertyModel> AllProperties = [];
        public readonly Dictionary<string, PropertyModel> AllPropertiesByName = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, Guid> AllPropertyIdsByName = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<Guid, NodeTypeModel> ThisAndAllInheritedTypes = [];
        public readonly Dictionary<Guid, NodeTypeModel> ThisAndDescendingTypes = [];
        public readonly List<PropertyModel> DisplayProperties = [];
        public readonly List<PropertyModel> TextIndexProperties = [];

        public void BuildDisplayName(INodeData node, StringBuilder sb) {
            var i = 0;
            foreach (var prop in DisplayProperties) {
                if (node.TryGetValue(prop.Id, out var value)) {
                    var text = prop.GetTextIndex(value);
                    if (string.IsNullOrEmpty(text)) continue;
                    var isFirst = i++ == 0;
                    if (!isFirst) sb.Append(" ");
                    sb.Append(text);
                }
            }
        }
        public string GetDisplayName(INodeData node) {
            var parts = new string[DisplayProperties.Count];
            var i = 0;
            foreach (var prop in DisplayProperties) {
                if (node.TryGetValue(prop.Id, out var value)) {
                    var text = prop.GetTextIndex(value);
                    if (string.IsNullOrEmpty(text)) continue;
                    parts[i++] = text;
                }
            }
            if (i == 0) return string.Empty;
            return string.Join(" ", parts, 0, i);
        }

        public override string ToString() {
            return (string.IsNullOrEmpty(Namespace) ? string.Empty : Namespace + ".") + CodeName;
        }
    }
}
