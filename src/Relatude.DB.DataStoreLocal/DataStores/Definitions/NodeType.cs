using Relatude.DB.Datamodels;

namespace Relatude.DB.DataStores.Definitions {
    internal class NodeType {
        public NodeTypeModel Model { get; }
        

        public NodeType(NodeTypeModel cm) {
            Id = cm.Id;
            Namespace = cm.Namespace;
            CodeName = cm.CodeName;
            this.Model = cm;
        }
        public Guid Id;
        public string? Namespace;
        public string CodeName;
        internal Dictionary<string, Property> AllPropertiesByName = [];
        internal Dictionary<Guid, Property> AllProperties = [];
        public override string ToString() {
            return nameof(NodeType) + ": " + Namespace + "." + CodeName;
        }
    }
}
