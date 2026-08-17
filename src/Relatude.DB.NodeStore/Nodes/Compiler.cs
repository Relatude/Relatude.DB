using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
#if DEBUG
            //if (true) {
            //    //var path = Path.GetTempPath();
            //    var path = "C:\\WAF\\Code\\Relatude.DB\\examples\\Website.Simple\\NewFolder";
            //    path = Path.Combine(path, "RelatudeDBCompiledModels");
            //    if (Directory.Exists(path)) Directory.Delete(path, true);
            //    Directory.CreateDirectory(path);
            //    foreach (var code in codeStrings) {
            //        var filePath = Path.Combine(path, code.className + ".cs");
            //        File.WriteAllText(filePath, code.code);
            //    }
            //}
#endif
            var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp12);
            var syntaxTrees = codeStrings.Select(code => SyntaxFactory.ParseSyntaxTree(code.code, parseOptions, code.className + ".cs"));
            var assemblyPath = Path.GetDirectoryName(typeof(object).Assembly.Location) + string.Empty;
            var refs = new List<MetadataReference> {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Compiler).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Datamodel).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(IDataStore).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Text.Json.Serialization.JsonAttribute).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "mscorlib.dll")),
                MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.dll")),
                MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Linq.dll")),
                MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Core.dll")),
                MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Collections.dll")),
                MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Runtime.dll")),
                MetadataReference.CreateFromFile(typeof(FileValue).Assembly.Location),
            };
            foreach (var a in datamodel.Assemblies) {
                if (a.IsDynamic || string.IsNullOrEmpty(a.Location)) throw new Exception(
                    "The model assembly " + a.GetName().Name + " is dynamic or was loaded from memory, so it has no file on disk "
                    + "and cannot be referenced when compiling the model mapping code. Load the model types from an assembly file instead. ");
                refs.Add(MetadataReference.CreateFromFile(a.Location));
            }
            var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release);
            var compiler = CSharpCompilation.Create("models", syntaxTrees, refs, options);
            using var ms = new MemoryStream();
            var result = compiler.Emit(ms);
            if (result.Success) return ms.ToArray();
            throw new Exception(describeCompilationErrors(result, codeStrings));
        }
        static string describeCompilationErrors(Microsoft.CodeAnalysis.Emit.EmitResult result, List<(string className, string code)> codeStrings) {
            var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            var sb = new StringBuilder();
            sb.AppendLine("Failed to compile the generated model mapping code (" + errors.Count + " error" + (errors.Count == 1 ? "" : "s") + "). "
                + "This code is generated from the datamodel, so the usual cause is a model member the generator cannot handle correctly, "
                + "an enum or node type that is not available at runtime, or a model type whose assembly could not be referenced. "
                + "The file names below tell which model type each error belongs to:");
            foreach (var e in errors.Take(10)) {
                var span = e.Location.GetLineSpan();
                var line = span.StartLinePosition.Line;
                sb.AppendLine(span.Path + "(" + (line + 1) + "): " + e.GetMessage());
                var source = codeStrings.FirstOrDefault(c => c.className + ".cs" == span.Path).code;
                if (source != null) {
                    var lines = source.Split('\n');
                    if (line >= 0 && line < lines.Length) sb.AppendLine("    generated code: " + lines[line].Trim());
                }
            }
            if (errors.Count > 10) sb.AppendLine("... and " + (errors.Count - 10) + " more error(s).");
            return sb.ToString();
        }
    }
}
