using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;

namespace Relatude.DB.Nodes {
    [AttributeUsage(AttributeTargets.Class)]
    public class TypeGuidAttribute : Attribute { public string Guid { get; set; } = string.Empty; }
    internal class Compiler {
        public static byte[] BuildDll(List<(string className, string code)> sourceCode, Datamodel datamodel) {
            var types = new Dictionary<Guid, Type>();
            var dllBytes = compileCode(sourceCode, datamodel);
            return dllBytes;
        }
        public static Dictionary<Guid, Type> LoadDll(byte[] dll) {
            var types = new Dictionary<Guid, Type>();
            var loader = new AssemblyLoadContext(null);
            using var dllStream = new MemoryStream(dll);
            var assembly = loader.LoadFromStream(dllStream);
            foreach (var type in assembly.GetTypes()) {
                var attr = type.GetCustomAttributes<TypeGuidAttribute>().FirstOrDefault();
                if (attr != null) types.Add(new Guid(attr.Guid), type);
            }
            return types;
        }
        public static Dictionary<Guid, Type> Build(List<(string className, string code)> sourceCode, Datamodel datamodel) {
            var types = new Dictionary<Guid, Type>();
            var dllBytes = compileCode(sourceCode, datamodel);
            var loader = new AssemblyLoadContext(null);
            using var dllStream = new MemoryStream(dllBytes);
            var assembly = loader.LoadFromStream(dllStream);
            foreach (var type in assembly.GetTypes()) {
                var attr = type.GetCustomAttributes<TypeGuidAttribute>().FirstOrDefault();
                if (attr != null) types.Add(new Guid(attr.Guid), type);
            }
            return types;
        }
        static byte[] compileCode(List<(string className, string code)> codeStrings, Datamodel datamodel) {
// # if DEBUG
//             if (true) { 
//                 var path="C:\\WAF_Temp\\CodeGen\\";
//                 if (!Directory.Exists(path)) Directory.CreateDirectory(path);
//                 foreach (var code in codeStrings) {
//                     var filePath = Path.Combine(path, code.className + ".cs");
//                     File.WriteAllText(filePath, code.code);
//                 }
//             }
// #endif
            var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp10);
            var syntaxTrees = codeStrings.Select(code => SyntaxFactory.ParseSyntaxTree(code.code, parseOptions, code.className + ".cs"));
            var assemblyPath = Path.GetDirectoryName(typeof(object).Assembly.Location) + string.Empty;
            var refs = new List<MetadataReference> {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Compiler).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Datamodel).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(IDataStore).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "mscorlib.dll")),
                MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.dll")),
                MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Linq.dll")),
                MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Core.dll")),
                MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Collections.dll")),
                MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Runtime.dll")),
                MetadataReference.CreateFromFile(typeof(FileValue).Assembly.Location),
            };
            foreach (var a in datamodel.Assemblies) refs.Add(MetadataReference.CreateFromFile(a.Location));
            var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release);
            var compiler = CSharpCompilation.Create("models", syntaxTrees, refs, options);
            using var ms = new MemoryStream();
            var result = compiler.Emit(ms);
            if (result.Success) return ms.ToArray();
            var compilationErrors = new StringBuilder();
            foreach (var e in result.Diagnostics) compilationErrors.AppendLine(e + ". ");
            var details = compilationErrors.ToString();
            if (details.Length > 500) details = details[..500];
            throw new Exception("Compilation failed: " + details);
        }
    }
}
