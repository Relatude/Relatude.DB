using Relatude.DB.Datamodels;

namespace Relatude.DB.CodeGeneration;
// Extensions neede for building model from types and compiling model classes
public enum ModelCodeLanguage {
    CSharp,
    // Typescript
    // Javascript
    // Java
    // Phyton
    // PHP
}
public static class DatamodelCodeGenExtensions {
    public static string GetModelCode(this Datamodel model, bool addAttributes = true, ModelCodeLanguage langauge = ModelCodeLanguage.CSharp) {
        return CodeGeneratorForCSharpModels.GenerateCSharpModelCode(model, addAttributes);
    }
}
