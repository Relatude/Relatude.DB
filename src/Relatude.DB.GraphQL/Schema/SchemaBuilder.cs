using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;

namespace Relatude.DB.GraphQL.Schema;

/// <summary>Builds an immutable <see cref="GqlSchema"/> from a Relatude.DB datamodel.</summary>
internal sealed class SchemaBuilder {
    readonly Datamodel _dm;
    readonly GraphQLOptions _options;
    readonly NameRegistry _typeNames = new("Int", "Float", "String", "Boolean", "ID", "DateTime", "Long", "Decimal", "Query", "Node", "FileInfo", "RelatedNodeFilterInput");
    readonly List<GqlNamedType> _allTypes = [];
    readonly Dictionary<Guid, string> _typeNameByNodeId = [];
    readonly Dictionary<Guid, GqlObjectType> _objectTypes = [];
    readonly Dictionary<Guid, GqlInterfaceType> _interfaceTypes = [];      // datamodel interfaces
    readonly Dictionary<Guid, GqlInterfaceType> _synthesizedInterfaces = []; // per concrete class with concrete descendants
    readonly Dictionary<string, GqlEnumType?> _enumTypes = new(StringComparer.Ordinal); // by CLR full name; null = unresolvable
    readonly Dictionary<string, GqlInputObjectType> _sharedInputs = new(StringComparer.Ordinal);
    GqlScalars _scalars = null!;
    GqlInterfaceType _node = null!;
    GqlObjectType? _fileInfo;
    List<NodeTypeModel> _exposed = null!;

    SchemaBuilder(Datamodel dm, GraphQLOptions options) { _dm = dm; _options = options; }

    public static GqlSchema Build(Datamodel dm, GraphQLOptions options) => new SchemaBuilder(dm, options).build();

    GqlSchema build() {
        _dm.EnsureInitalization();
        createScalars();
        _node = new GqlInterfaceType { Name = "Node", Description = "Base interface implemented by all Relatude.DB node types." };
        _node.Fields.AddRange(systemFields());
        _allTypes.Add(_node);

        _exposed = _dm.NodeTypes.Values
            .Where(isExposed)
            .OrderBy(t => t.CodeName, StringComparer.Ordinal)
            .ThenBy(t => t.Id)
            .ToList();

        foreach (var t in _exposed) _typeNameByNodeId[t.Id] = _typeNames.Claim(t.CodeName, t.Namespace?.Replace('.', '_') + "_" + t.CodeName);
        createShells();
        foreach (var t in _exposed) buildFieldsFor(t);
        wireInterfacesAndPossibleTypes();

        var query = new GqlObjectType { Name = "Query", Description = "Read-only entry points generated from the Relatude.DB datamodel." };
        _allTypes.Add(query);
        buildRootFields(query);

        var schema = new GqlSchema { Datamodel = _dm, QueryType = query, NodeInterface = _node, Scalars = _scalars };
        foreach (var t in _allTypes) {
            schema.Types.Add(t.Name, t);
            switch (t) {
                case GqlObjectType o: o.Seal(); break;
                case GqlInterfaceType i: i.Seal(); break;
                case GqlInputObjectType io: io.Seal(); break;
                case GqlEnumType e: e.Seal(); break;
            }
        }
        foreach (var (id, o) in _objectTypes) schema.ObjectTypesByNodeTypeId.Add(id, o);
        foreach (var t in _exposed) schema.ReferenceTypesByNodeTypeId.Add(t.Id, referenceTypeFor(t)!);
        return schema;
    }

    bool isExposed(NodeTypeModel t) {
        if (t.Id == NodeConstants.BaseNodeTypeId) return false;
        if (t.Hidden || t.IsInnerNode) return false;
        if (!_options.IncludeSystemTypes && t.Namespace == "Relatude.DB.Native.Models") return false;
        return _options.TypeFilter?.Invoke(t) ?? true;
    }

    void createScalars() {
        var sInt = new GqlScalarType { Name = "Int", IsBuiltIn = true };
        var sFloat = new GqlScalarType { Name = "Float", IsBuiltIn = true };
        var sString = new GqlScalarType { Name = "String", IsBuiltIn = true };
        var sBool = new GqlScalarType { Name = "Boolean", IsBuiltIn = true };
        var sId = new GqlScalarType { Name = "ID", IsBuiltIn = true };
        var sDateTime = new GqlScalarType { Name = "DateTime", Description = "A UTC timestamp serialized as an ISO-8601 string." };
        var sLong = new GqlScalarType { Name = "Long", Description = "A 64-bit integer serialized as a JSON number. Values above 2^53 lose precision in JavaScript clients." };
        var sDecimal = new GqlScalarType { Name = "Decimal", Description = "A decimal number serialized as a JSON number." };
        _scalars = new GqlScalars { Int = sInt, Float = sFloat, String = sString, Boolean = sBool, Id = sId, DateTime = sDateTime, Long = sLong, Decimal = sDecimal };
        _allTypes.AddRange([sInt, sFloat, sString, sBool, sId, sDateTime, sLong, sDecimal]);
    }

    void createShells() {
        foreach (var t in _exposed) {
            var name = _typeNameByNodeId[t.Id];
            if (t.IsInterface) {
                var i = new GqlInterfaceType { Name = name, NodeType = t, Description = t.FullName };
                _interfaceTypes.Add(t.Id, i);
                _allTypes.Add(i);
            } else {
                var o = new GqlObjectType { Name = name, NodeType = t, Description = t.FullName };
                _objectTypes.Add(t.Id, o);
                _allTypes.Add(o);
            }
        }
        // a concrete class with exposed concrete descendants needs an interface stand-in, so that
        // root/relation fields referring to it can return subclass instances without breaking the type system
        foreach (var t in _exposed) {
            if (t.IsInterface) continue;
            var hasConcreteDescendants = t.ThisAndDescendingTypes.Values.Any(d => d.Id != t.Id && !d.IsInterface && _typeNameByNodeId.ContainsKey(d.Id));
            if (!hasConcreteDescendants) continue;
            var i = new GqlInterfaceType {
                Name = _typeNames.Claim(_typeNameByNodeId[t.Id] + "Interface"),
                NodeType = t,
                IsSynthesized = true,
                Description = $"Common fields of {_typeNameByNodeId[t.Id]} and its subtypes.",
            };
            _synthesizedInterfaces.Add(t.Id, i);
            _allTypes.Add(i);
        }
    }

    /// <summary>The type used when referring to a node type in field positions.</summary>
    GqlNamedType? referenceTypeFor(NodeTypeModel t) {
        if (t.Id == NodeConstants.BaseNodeTypeId) return _node;
        if (_interfaceTypes.TryGetValue(t.Id, out var i)) return i;
        if (_synthesizedInterfaces.TryGetValue(t.Id, out var s)) return s;
        if (_objectTypes.TryGetValue(t.Id, out var o)) return o;
        return null;
    }

    void buildFieldsFor(NodeTypeModel t) {
        var fields = buildNodeFields(t);
        if (t.IsInterface) _interfaceTypes[t.Id].Fields.AddRange(fields);
        else {
            _objectTypes[t.Id].Fields.AddRange(fields);
            if (_synthesizedInterfaces.TryGetValue(t.Id, out var s)) s.Fields.AddRange(buildNodeFields(t));
        }
    }

    List<GqlField> buildNodeFields(NodeTypeModel t) {
        var fields = systemFields();
        var used = new HashSet<string>(fields.Select(f => f.Name), StringComparer.Ordinal);
        foreach (var p in sortedProperties(t)) {
            var f = buildPropertyField(p, used);
            if (f != null) fields.Add(f);
        }
        return fields;
    }

    static IEnumerable<PropertyModel> sortedProperties(NodeTypeModel t)
        => t.AllProperties.Values.Where(p => !p.Internal).OrderBy(p => p.CodeName, StringComparer.Ordinal).ThenBy(p => p.Id);

    List<GqlField> systemFields() => [
        new GqlField { Name = "id", Type = nn(_scalars.Id), Source = FieldSource.Id, Description = "The node's public id." },
        new GqlField { Name = "displayName", Type = _scalars.String, Source = FieldSource.DisplayName, Description = "The node's display name." },
        new GqlField { Name = "createdUtc", Type = nn(_scalars.DateTime), Source = FieldSource.CreatedUtc },
        new GqlField { Name = "changedUtc", Type = nn(_scalars.DateTime), Source = FieldSource.ChangedUtc },
    ];

    static string fieldNameOf(PropertyModel p) {
        var n = NameRegistry.CamelCase(p.CodeName);
        return n is "id" or "displayName" or "createdUtc" or "changedUtc" ? n + "Value" : n;
    }

    static string claimFieldName(PropertyModel p, HashSet<string> used) {
        var name = fieldNameOf(p);
        if (!used.Add(name)) {
            var i = 2;
            while (!used.Add(name + "_" + i)) i++;
            name = name + "_" + i;
        }
        return name;
    }

    GqlField? buildPropertyField(PropertyModel p, HashSet<string> used) {
        switch (p.PropertyType) {
            case PropertyType.Any:
            case PropertyType.ByteArray:
            case PropertyType.FloatArray:
            case PropertyType.Embedded:
                return null;
            case PropertyType.Relation: {
                    var rp = (RelationPropertyModel)p;
                    var target = resolveRelationTarget(rp);
                    var refType = target == null ? null : referenceTypeFor(target);
                    if (target == null || refType == null) return null;
                    if (rp.IsMany) {
                        return new GqlField {
                            Name = claimFieldName(p, used), Type = nn(listOf(nn(refType))), Source = FieldSource.RelationMany,
                            Property = p, TargetNodeType = target, Arguments = { topArgument() },
                        };
                    }
                    return new GqlField { Name = claimFieldName(p, used), Type = refType, Source = FieldSource.RelationOne, Property = p, TargetNodeType = target };
                }
            case PropertyType.Reference: {
                    var target = commonBase(((ReferencePropertyModel)p).NodeTypes);
                    var refType = target == null ? null : referenceTypeFor(target);
                    if (target == null || refType == null) return null;
                    return new GqlField { Name = claimFieldName(p, used), Type = refType, Source = FieldSource.ReferenceOne, Property = p, TargetNodeType = target };
                }
            case PropertyType.References: {
                    var target = commonBase(((ReferencesPropertyModel)p).NodeTypes);
                    var refType = target == null ? null : referenceTypeFor(target);
                    if (target == null || refType == null) return null;
                    return new GqlField {
                        Name = claimFieldName(p, used), Type = nn(listOf(nn(refType))), Source = FieldSource.ReferenceMany,
                        Property = p, TargetNodeType = target, Arguments = { topArgument() },
                    };
                }
            case PropertyType.File: {
                    _fileInfo ??= createFileInfoType();
                    return new GqlField { Name = claimFieldName(p, used), Type = _fileInfo, Source = FieldSource.FileProperty, Property = p, Description = "Null when no file is uploaded." };
                }
            case PropertyType.Integer: {
                    var ip = (IntegerPropertyModel)p;
                    if (ip.IsEnum) {
                        var et = getEnumType(ip.FullEnumTypeName, null, ip.LegalValues);
                        if (et != null) return new GqlField { Name = claimFieldName(p, used), Type = nn(et), Source = FieldSource.EnumProperty, Property = p };
                    }
                    return new GqlField { Name = claimFieldName(p, used), Type = nn(_scalars.Int), Source = FieldSource.ScalarProperty, Property = p };
                }
            case PropertyType.EnumArray: {
                    var ep = (EnumArrayPropertyModel)p;
                    var et = getEnumType(ep.FullEnumTypeName, ep.LegalValueNames, ep.LegalValues);
                    if (et != null) return new GqlField { Name = claimFieldName(p, used), Type = nn(listOf(nn(et))), Source = FieldSource.EnumArrayProperty, Property = p };
                    return new GqlField { Name = claimFieldName(p, used), Type = nn(listOf(nn(_scalars.Int))), Source = FieldSource.ScalarProperty, Property = p };
                }
            default: {
                    var scalar = scalarTypeFor(p.PropertyType);
                    if (scalar == null) return null;
                    return new GqlField { Name = claimFieldName(p, used), Type = scalar, Source = FieldSource.ScalarProperty, Property = p };
                }
        }
    }

    GqlType? scalarTypeFor(PropertyType pt) => pt switch {
        PropertyType.Boolean => nn(_scalars.Boolean),
        PropertyType.String => _scalars.String,
        PropertyType.StringArray => nn(listOf(nn(_scalars.String))),
        PropertyType.Double or PropertyType.Float => nn(_scalars.Float),
        PropertyType.Decimal => nn(_scalars.Decimal),
        PropertyType.DateTime or PropertyType.DateTimeOffset => nn(_scalars.DateTime),
        PropertyType.TimeSpan => nn(_scalars.String),
        PropertyType.Guid => nn(_scalars.Id),
        PropertyType.Long => nn(_scalars.Long),
        PropertyType.GuidArray => nn(listOf(nn(_scalars.Id))),
        _ => null,
    };

    GqlArgument topArgument() => new() { Name = "top", Type = _scalars.Int, Description = "Limits the number of related nodes returned." };

    NodeTypeModel? resolveRelationTarget(RelationPropertyModel rp) {
        if (!_dm.Relations.TryGetValue(rp.RelationId, out var rel)) return null;
        var set = rp.FromTargetToSource ? rel.SourceTypes : rel.TargetTypes;
        return commonBase(set);
    }

    NodeTypeModel? commonBase(List<Guid>? typeIds) {
        if (typeIds == null || typeIds.Count == 0) return null;
        if (typeIds.Count == 1) return _dm.NodeTypes.TryGetValue(typeIds[0], out var t) ? t : null;
        try { return _dm.FindFirstCommonBase(typeIds); } catch { return null; }
    }

    GqlObjectType createFileInfoType() {
        var t = new GqlObjectType { Name = "FileInfo", Description = "Metadata of an uploaded file." };
        t.Fields.AddRange([
            new GqlField { Name = "name", Type = nn(_scalars.String), Source = FieldSource.FileName },
            new GqlField { Name = "size", Type = nn(_scalars.Long), Source = FieldSource.FileSize, Description = "File size in bytes." },
            new GqlField { Name = "width", Type = nn(_scalars.Int), Source = FieldSource.FileWidth, Description = "Image/video width in pixels; 0 when not applicable." },
            new GqlField { Name = "height", Type = nn(_scalars.Int), Source = FieldSource.FileHeight, Description = "Image/video height in pixels; 0 when not applicable." },
            new GqlField { Name = "contentType", Type = _scalars.String, Source = FieldSource.FileContentType },
        ]);
        _allTypes.Add(t);
        return t;
    }

    GqlEnumType? getEnumType(string? clrFullName, string[]? names, int[]? values) {
        if (string.IsNullOrEmpty(clrFullName)) return null;
        if (_enumTypes.TryGetValue(clrFullName, out var cached)) return cached;
        if (names == null || values == null || names.Length == 0 || names.Length != values.Length) {
            (names, values) = probeClrEnum(clrFullName);
        }
        if (names == null || values == null) {
            _enumTypes[clrFullName] = null;
            return null;
        }
        var shortName = clrFullName[(Math.Max(clrFullName.LastIndexOf('.'), clrFullName.LastIndexOf('+')) + 1)..];
        var et = new GqlEnumType { Name = _typeNames.Claim(shortName, shortName + "Enum"), Description = clrFullName };
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < names.Length; i++) {
            var name = NameRegistry.Sanitize(names[i]);
            if (name is "true" or "false" or "null") name = "_" + name;
            if (!usedNames.Add(name)) continue;
            et.Values.Add(new GqlEnumValue { Name = name, IntValue = values[i] });
        }
        _allTypes.Add(et);
        _enumTypes[clrFullName] = et;
        return et;
    }

    (string[]?, int[]?) probeClrEnum(string clrFullName) {
        var clr = Type.GetType(clrFullName);
        if (clr == null) {
            foreach (var asm in _dm.Assemblies) {
                clr = asm.GetType(clrFullName);
                if (clr != null) break;
            }
        }
        if (clr == null || !clr.IsEnum) return (null, null);
        try {
            var names = Enum.GetNames(clr);
            var values = names.Select(n => Convert.ToInt32(Enum.Parse(clr, n))).ToArray();
            return (names, values);
        } catch { return (null, null); }
    }

    void wireInterfacesAndPossibleTypes() {
        var concreteSorted = _exposed.Where(t => !t.IsInterface).ToList();
        foreach (var t in concreteSorted) _node.PossibleTypes.Add(_objectTypes[t.Id]);
        foreach (var t in _exposed) {
            if (t.IsInterface) {
                var i = _interfaceTypes[t.Id];
                i.Interfaces.Add(_node);
                foreach (var a in ancestorsOf(t)) {
                    if (_interfaceTypes.TryGetValue(a.Id, out var ai)) i.Interfaces.Add(ai);
                }
                foreach (var d in t.ThisAndDescendingTypes.Values.OrderBy(d => d.CodeName, StringComparer.Ordinal).ThenBy(d => d.Id)) {
                    if (!d.IsInterface && _objectTypes.TryGetValue(d.Id, out var o)) i.PossibleTypes.Add(o);
                }
            } else {
                var o = _objectTypes[t.Id];
                o.Interfaces.Add(_node);
                foreach (var a in ancestorsOf(t)) {
                    if (_interfaceTypes.TryGetValue(a.Id, out var ai)) o.Interfaces.Add(ai);
                    else if (_synthesizedInterfaces.TryGetValue(a.Id, out var si)) o.Interfaces.Add(si);
                }
                if (_synthesizedInterfaces.TryGetValue(t.Id, out var own)) o.Interfaces.Add(own);
            }
        }
        foreach (var (typeId, si) in _synthesizedInterfaces) {
            si.Interfaces.Add(_node);
            var t = _dm.NodeTypes[typeId];
            foreach (var d in t.ThisAndDescendingTypes.Values.OrderBy(d => d.CodeName, StringComparer.Ordinal).ThenBy(d => d.Id)) {
                if (!d.IsInterface && _objectTypes.TryGetValue(d.Id, out var o)) si.PossibleTypes.Add(o);
            }
        }
    }

    IEnumerable<NodeTypeModel> ancestorsOf(NodeTypeModel t)
        => t.ThisAndAllInheritedTypes.Values.Where(a => a.Id != t.Id).OrderBy(a => a.CodeName, StringComparer.Ordinal).ThenBy(a => a.Id);

    // ---- filter inputs, orderBy enums, result wrappers, root fields ----

    void buildRootFields(GqlObjectType query) {
        var rootNames = new NameRegistry("__schema", "__type", "__typename");
        foreach (var t in _exposed) {
            var typeName = _typeNameByNodeId[t.Id];
            var refType = referenceTypeFor(t)!;
            var composite = (IGqlCompositeType)refType;
            var filterInput = buildFilterInput(t, typeName);
            var orderByEnum = buildOrderByEnum(typeName, composite);
            var wrapper = buildResultWrapper(typeName, refType, t);

            var singular = new GqlField {
                Name = rootNames.Claim(NameRegistry.CamelCase(typeName)),
                Type = refType, Source = FieldSource.RootSingle, TargetNodeType = t,
                Description = $"Fetches a single {typeName} by id.",
                Arguments = { new GqlArgument { Name = "id", Type = nn(_scalars.Id) } },
            };
            query.Fields.Add(singular);

            var list = new GqlField {
                Name = rootNames.Claim(NameRegistry.Pluralize(NameRegistry.CamelCase(typeName)), "all" + typeName),
                Type = nn(wrapper), Source = FieldSource.RootList, TargetNodeType = t,
                Description = $"Queries {typeName} nodes with filtering, search, ordering and paging.",
            };
            if (filterInput != null) list.Arguments.Add(new GqlArgument { Name = "filter", Type = filterInput });
            list.Arguments.Add(new GqlArgument { Name = "search", Type = _scalars.String, Description = "Free-text search (BM25 + optional semantic index)." });
            if (orderByEnum != null) {
                list.Arguments.Add(new GqlArgument { Name = "orderBy", Type = orderByEnum });
                list.Arguments.Add(new GqlArgument { Name = "descending", Type = _scalars.Boolean, DefaultValue = false, HasDefaultValue = true });
            }
            list.Arguments.Add(new GqlArgument { Name = "page", Type = _scalars.Int, DefaultValue = 0, HasDefaultValue = true, Description = "Zero-based page index." });
            list.Arguments.Add(new GqlArgument { Name = "pageSize", Type = _scalars.Int, Description = $"Defaults to {_options.DefaultPageSize}, capped at {_options.MaxPageSize}." });
            list.Arguments.Add(new GqlArgument { Name = "ids", Type = listOf(nn(_scalars.Id)), Description = "Restricts the result to the given node ids." });
            query.Fields.Add(list);
        }
    }

    GqlInputObjectType? buildFilterInput(NodeTypeModel t, string typeName) {
        var input = new GqlInputObjectType {
            Name = _typeNames.Claim(typeName + "FilterInput"),
            Description = "All given conditions must match (logical AND). Use the search argument for text matching.",
        };
        var used = new HashSet<string>(["and", "or", "not"], StringComparer.Ordinal);
        foreach (var p in sortedProperties(t)) {
            var opInput = operatorInputFor(p);
            if (opInput == null) continue;
            input.InputFields.Add(new GqlInputField { Name = claimFieldName(p, used), Type = opInput, Property = p });
        }
        if (input.InputFields.Count == 0) return null; // nothing filterable; and/or/not alone would be pointless
        input.InputFields.Add(new GqlInputField { Name = "and", Type = listOf(nn(input)), Op = FilterOp.And });
        input.InputFields.Add(new GqlInputField { Name = "or", Type = listOf(nn(input)), Op = FilterOp.Or });
        input.InputFields.Add(new GqlInputField { Name = "not", Type = input, Op = FilterOp.Not });
        _allTypes.Add(input);
        return input;
    }

    GqlInputObjectType? operatorInputFor(PropertyModel p) {
        switch (p.PropertyType) {
            case PropertyType.Boolean: return sharedOperatorInput("BooleanFilterInput", _scalars.Boolean, ordered: false, withIn: false);
            case PropertyType.String: return sharedOperatorInput("StringFilterInput", _scalars.String, ordered: false, withIn: true);
            case PropertyType.Double or PropertyType.Float: return sharedOperatorInput("FloatFilterInput", _scalars.Float, ordered: true, withIn: true);
            case PropertyType.Decimal: return sharedOperatorInput("DecimalFilterInput", _scalars.Decimal, ordered: true, withIn: true);
            case PropertyType.DateTime or PropertyType.DateTimeOffset: return sharedOperatorInput("DateTimeFilterInput", _scalars.DateTime, ordered: true, withIn: false);
            case PropertyType.Long: return sharedOperatorInput("LongFilterInput", _scalars.Long, ordered: true, withIn: true);
            case PropertyType.Guid: return sharedOperatorInput("IdFilterInput", _scalars.Id, ordered: false, withIn: true);
            case PropertyType.Integer: {
                    var ip = (IntegerPropertyModel)p;
                    if (ip.IsEnum) {
                        var et = getEnumType(ip.FullEnumTypeName, null, ip.LegalValues);
                        if (et != null) return enumOperatorInput(et);
                    }
                    return sharedOperatorInput("IntFilterInput", _scalars.Int, ordered: true, withIn: true);
                }
            case PropertyType.Relation:
            case PropertyType.Reference:
                return relatedNodeFilterInput();
            default:
                return null; // arrays, files, TimeSpan, References etc. are not filterable in v1
        }
    }

    GqlInputObjectType sharedOperatorInput(string name, GqlScalarType valueType, bool ordered, bool withIn) {
        if (_sharedInputs.TryGetValue(name, out var existing)) return existing;
        var input = new GqlInputObjectType { Name = name };
        input.InputFields.Add(new GqlInputField { Name = "eq", Type = valueType, Op = FilterOp.Eq });
        input.InputFields.Add(new GqlInputField { Name = "ne", Type = valueType, Op = FilterOp.Ne });
        if (ordered) {
            input.InputFields.Add(new GqlInputField { Name = "gt", Type = valueType, Op = FilterOp.Gt });
            input.InputFields.Add(new GqlInputField { Name = "gte", Type = valueType, Op = FilterOp.Gte });
            input.InputFields.Add(new GqlInputField { Name = "lt", Type = valueType, Op = FilterOp.Lt });
            input.InputFields.Add(new GqlInputField { Name = "lte", Type = valueType, Op = FilterOp.Lte });
        }
        if (withIn) {
            input.InputFields.Add(new GqlInputField { Name = "in", Type = listOf(nn(valueType)), Op = FilterOp.In });
            input.InputFields.Add(new GqlInputField { Name = "nin", Type = listOf(nn(valueType)), Op = FilterOp.Nin });
        }
        _sharedInputs.Add(name, input);
        _allTypes.Add(input);
        return input;
    }

    GqlInputObjectType enumOperatorInput(GqlEnumType et) {
        var name = et.Name + "FilterInput";
        if (_sharedInputs.TryGetValue(name, out var existing)) return existing;
        var input = new GqlInputObjectType { Name = _typeNames.Claim(name) };
        input.InputFields.Add(new GqlInputField { Name = "eq", Type = et, Op = FilterOp.Eq });
        input.InputFields.Add(new GqlInputField { Name = "ne", Type = et, Op = FilterOp.Ne });
        input.InputFields.Add(new GqlInputField { Name = "in", Type = listOf(nn(et)), Op = FilterOp.In });
        input.InputFields.Add(new GqlInputField { Name = "nin", Type = listOf(nn(et)), Op = FilterOp.Nin });
        _sharedInputs.Add(name, input);
        _allTypes.Add(input);
        return input;
    }

    GqlInputObjectType relatedNodeFilterInput() {
        const string name = "RelatedNodeFilterInput";
        if (_sharedInputs.TryGetValue(name, out var existing)) return existing;
        var input = new GqlInputObjectType { Name = name, Description = "Filters on the id of the related node." };
        input.InputFields.Add(new GqlInputField { Name = "eq", Type = _scalars.Id, Op = FilterOp.RelEq });
        input.InputFields.Add(new GqlInputField { Name = "in", Type = listOf(nn(_scalars.Id)), Op = FilterOp.RelIn });
        _sharedInputs.Add(name, input);
        _allTypes.Add(input);
        return input;
    }

    static readonly HashSet<PropertyType> _sortable = [
        PropertyType.Boolean, PropertyType.Integer, PropertyType.String, PropertyType.Double, PropertyType.Float,
        PropertyType.Decimal, PropertyType.DateTime, PropertyType.DateTimeOffset, PropertyType.Long,
    ];

    GqlEnumType? buildOrderByEnum(string typeName, IGqlCompositeType composite) {
        var values = new List<GqlEnumValue>();
        foreach (var f in composite.Fields) {
            if (f.Property == null) continue;
            if (f.Source != FieldSource.ScalarProperty && f.Source != FieldSource.EnumProperty) continue;
            if (!_sortable.Contains(f.Property.PropertyType)) continue;
            values.Add(new GqlEnumValue { Name = f.Name, Property = f.Property });
        }
        if (values.Count == 0) return null;
        var e = new GqlEnumType { Name = _typeNames.Claim(typeName + "OrderBy") };
        e.Values.AddRange(values);
        _allTypes.Add(e);
        return e;
    }

    GqlObjectType buildResultWrapper(string typeName, GqlNamedType refType, NodeTypeModel t) {
        var w = new GqlObjectType { Name = _typeNames.Claim(typeName + "Result"), Description = $"A page of {typeName} nodes." };
        w.Fields.AddRange([
            new GqlField { Name = "items", Type = nn(listOf(nn(refType))), Source = FieldSource.WrapperItems, TargetNodeType = t },
            new GqlField { Name = "totalCount", Type = nn(_scalars.Int), Source = FieldSource.WrapperTotalCount, Description = "Total matches before paging." },
            new GqlField { Name = "pageIndex", Type = nn(_scalars.Int), Source = FieldSource.WrapperPageIndex },
            new GqlField { Name = "pageSize", Type = _scalars.Int, Source = FieldSource.WrapperPageSize },
            new GqlField { Name = "durationMs", Type = nn(_scalars.Float), Source = FieldSource.WrapperExecutionTimeMs,
                Description = "Time spent fetching this result from the store, including loading the selected relations. Excludes parsing and result projection; see extensions.durationMs for the whole request." },
        ]);
        _allTypes.Add(w);
        return w;
    }

    static GqlNonNullType nn(GqlType t) => new(t);
    static GqlListType listOf(GqlType t) => new(t);
}
