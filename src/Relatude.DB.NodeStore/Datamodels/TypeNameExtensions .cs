namespace Relatude.DB.Datamodels;
public static class TypeNameExtensions {
    private static readonly Dictionary<Type, string> _aliases = new()
    {
        { typeof(bool), "bool" },
        { typeof(byte), "byte" },
        { typeof(sbyte), "sbyte" },
        { typeof(char), "char" },
        { typeof(decimal), "decimal" },
        { typeof(double), "double" },
        { typeof(float), "float" },
        { typeof(int), "int" },
        { typeof(uint), "uint" },
        { typeof(long), "long" },
        { typeof(ulong), "ulong" },
        { typeof(object), "object" },
        { typeof(short), "short" },
        { typeof(ushort), "ushort" },
        { typeof(string), "string" },
        { typeof(void), "void" }
    };

    public static string GetCSharpName(this Type type) {
        return _aliases.TryGetValue(type, out var alias) ? alias : type.Name;
    }
}