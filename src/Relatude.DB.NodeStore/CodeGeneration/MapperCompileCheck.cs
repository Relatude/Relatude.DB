using Relatude.DB.Datamodels;
using Relatude.DB.Nodes;

namespace Relatude.DB.CodeGeneration;

/// <summary>
/// Compiles the code a <see cref="NodeStore"/> generates from a datamodel when it opens - the
/// interface implementations and the value mappers - without opening anything. The datamodel
/// editor runs this on a model before activating it, so a model that would stop the database from
/// opening is reported with the compiler's own diagnostics instead. Nothing is loaded or kept.
/// </summary>
public static class MapperCompileCheck {
    /// <summary>Throws with the compiler diagnostics when the generated code does not compile.</summary>
    public static void Verify(Datamodel datamodel) {
        Compiler.BuildDll(GenerateCode(datamodel), datamodel);
    }
    /// <summary>
    /// Everything a store compiles when it opens: the classes of runtime types the application has none
    /// for, the implementations of model interfaces, and a value mapper per node type.
    /// </summary>
    internal static List<(string className, string code)> GenerateCode(Datamodel datamodel) {
        datamodel.EnsureInitalization();
        return RuntimeTypeGen.Generate(datamodel)
            .Concat(InterfaceGen.GetImplementations(datamodel))
            .Concat(MapperGen.GenerateValueMappers(datamodel))
            .ToList();
    }
}
