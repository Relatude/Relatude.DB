namespace Relatude.DB.NodeServer {
    public enum DatamodelSourceType {
        AssemblyNameReference = 0,
        TypeNameReference = 1,
        JsonFile = 2,
        CSharpCodeFile = 3,
    }
    public class DatamodelSource {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public string? Namespace { get; set; }
        public DatamodelSourceType Type { get; set; }
        public string? Reference { get; set; }
        public Guid? FileIO { get; set; }
        /// <summary>
        /// When true, plain node-typed properties (and collections of node types) without an
        /// explicit relation are turned into auto-created relations, matching the old behavior.
        /// When false (default), such properties become Reference/References properties instead.
        /// </summary>
        public bool AutoDeduceRelations { get; set; } = false;
    }
}
