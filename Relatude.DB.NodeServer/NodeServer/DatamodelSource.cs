namespace Relatude.DB.NodeServer {
    public enum DatamodelSourceType {
        AssemblyNameReference = 0,
        TypeNameReference = 1,
        AssemblyFileReference = 2,
        TypeNameFileReference = 3,
        JsonFile = 4,
        CSharpCodeFile = 5,
    }
    public class DatamodelSource {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public string? Namespace { get; set; }
        public DatamodelSourceType Type { get; set; }
        public string? Reference { get; set; }
        public Guid? FileIO { get; set; }
    }
}
