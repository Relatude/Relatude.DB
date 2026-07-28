using System.Diagnostics.CodeAnalysis;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;

namespace Relatude.DB.GraphQL.Schema;

public enum GqlTypeKind { Scalar, Object, Interface, Enum, InputObject, List, NonNull }

/// <summary>Base of the lightweight GraphQL type system generated from the Relatude.DB datamodel.</summary>
public abstract class GqlType {
    public abstract GqlTypeKind Kind { get; }
    /// <summary>SDL notation for referencing this type, e.g. "[Product!]!".</summary>
    public abstract string ToTypeReference();
    /// <summary>Strips List/NonNull wrappers.</summary>
    public GqlNamedType UnwrapNamed() {
        var t = this;
        while (t is GqlWrapperType w) t = w.OfType;
        return (GqlNamedType)t;
    }
    public override string ToString() => ToTypeReference();
}

public abstract class GqlWrapperType(GqlType ofType) : GqlType {
    public GqlType OfType { get; } = ofType;
}
public sealed class GqlListType(GqlType ofType) : GqlWrapperType(ofType) {
    public override GqlTypeKind Kind => GqlTypeKind.List;
    public override string ToTypeReference() => "[" + OfType.ToTypeReference() + "]";
}
public sealed class GqlNonNullType(GqlType ofType) : GqlWrapperType(ofType) {
    public override GqlTypeKind Kind => GqlTypeKind.NonNull;
    public override string ToTypeReference() => OfType.ToTypeReference() + "!";
}

public abstract class GqlNamedType : GqlType {
    public required string Name { get; init; }
    public string? Description { get; set; }
    public override string ToTypeReference() => Name;
}

public sealed class GqlScalarType : GqlNamedType {
    public override GqlTypeKind Kind => GqlTypeKind.Scalar;
    /// <summary>True for the five spec scalars (Int, Float, String, Boolean, ID) — omitted from SDL output.</summary>
    public bool IsBuiltIn { get; init; }
}

public sealed class GqlEnumValue {
    public required string Name { get; init; }
    public int IntValue { get; init; }
    /// <summary>Set on orderBy enums: the property this value sorts on.</summary>
    public PropertyModel? Property { get; init; }
}

public sealed class GqlEnumType : GqlNamedType {
    public override GqlTypeKind Kind => GqlTypeKind.Enum;
    public List<GqlEnumValue> Values { get; } = [];
    Dictionary<string, GqlEnumValue>? _byName;
    Dictionary<int, GqlEnumValue>? _byInt;
    /// <summary>Builds the lookup dictionaries. Called once by the schema builder; the type is read-only afterwards.</summary>
    internal void Seal() {
        _byName = new(StringComparer.Ordinal);
        _byInt = [];
        foreach (var v in Values) {
            _byName[v.Name] = v;
            _byInt.TryAdd(v.IntValue, v);
        }
    }
    public bool TryGetByName(string name, [NotNullWhen(true)] out GqlEnumValue? value) {
        if (_byName != null) return _byName.TryGetValue(name, out value);
        value = Values.FirstOrDefault(v => v.Name == name);
        return value != null;
    }
    public bool TryGetByInt(int intValue, [NotNullWhen(true)] out GqlEnumValue? value) {
        if (_byInt != null) return _byInt.TryGetValue(intValue, out value);
        value = Values.FirstOrDefault(v => v.IntValue == intValue);
        return value != null;
    }
}

/// <summary>Common surface of object and interface types.</summary>
public interface IGqlCompositeType {
    string Name { get; }
    string? Description { get; }
    List<GqlField> Fields { get; }
    bool TryGetField(string name, [NotNullWhen(true)] out GqlField? field);
    NodeTypeModel? NodeType { get; }
}

public sealed class GqlObjectType : GqlNamedType, IGqlCompositeType {
    public override GqlTypeKind Kind => GqlTypeKind.Object;
    public List<GqlInterfaceType> Interfaces { get; } = [];
    public List<GqlField> Fields { get; } = [];
    /// <summary>The datamodel type behind this GraphQL type; null for synthetic types (Query, result wrappers, FileInfo).</summary>
    public NodeTypeModel? NodeType { get; init; }
    Dictionary<string, GqlField>? _fieldMap;
    internal void Seal() { _fieldMap = Fields.ToDictionary(f => f.Name, StringComparer.Ordinal); }
    public bool TryGetField(string name, [NotNullWhen(true)] out GqlField? field) {
        if (_fieldMap != null) return _fieldMap.TryGetValue(name, out field);
        field = Fields.FirstOrDefault(f => f.Name == name);
        return field != null;
    }
}

public sealed class GqlInterfaceType : GqlNamedType, IGqlCompositeType {
    public override GqlTypeKind Kind => GqlTypeKind.Interface;
    public List<GqlInterfaceType> Interfaces { get; } = [];
    public List<GqlField> Fields { get; } = [];
    public List<GqlObjectType> PossibleTypes { get; } = [];
    public NodeTypeModel? NodeType { get; init; }
    /// <summary>True when generated for a concrete class with descendants (no matching datamodel interface exists).</summary>
    public bool IsSynthesized { get; init; }
    Dictionary<string, GqlField>? _fieldMap;
    internal void Seal() { _fieldMap = Fields.ToDictionary(f => f.Name, StringComparer.Ordinal); }
    public bool TryGetField(string name, [NotNullWhen(true)] out GqlField? field) {
        if (_fieldMap != null) return _fieldMap.TryGetValue(name, out field);
        field = Fields.FirstOrDefault(f => f.Name == name);
        return field != null;
    }
}

/// <summary>Semantics of a filter-input field, used by the filter translator.</summary>
public enum FilterOp { None, Eq, Ne, Gt, Gte, Lt, Lte, In, Nin, And, Or, Not, RelEq, RelIn }

public sealed class GqlInputField {
    public required string Name { get; init; }
    public required GqlType Type { get; init; }
    public string? Description { get; set; }
    /// <summary>For per-type filter inputs: the property this field filters on.</summary>
    public PropertyModel? Property { get; init; }
    public FilterOp Op { get; init; }
}

public sealed class GqlInputObjectType : GqlNamedType {
    public override GqlTypeKind Kind => GqlTypeKind.InputObject;
    public List<GqlInputField> InputFields { get; } = [];
    Dictionary<string, GqlInputField>? _fieldMap;
    internal void Seal() { _fieldMap = InputFields.ToDictionary(f => f.Name, StringComparer.Ordinal); }
    public bool TryGetInputField(string name, [NotNullWhen(true)] out GqlInputField? field) {
        if (_fieldMap != null) return _fieldMap.TryGetValue(name, out field);
        field = InputFields.FirstOrDefault(f => f.Name == name);
        return field != null;
    }
}
