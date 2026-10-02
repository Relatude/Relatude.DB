using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.GraphQL.Endpoints;

namespace Relatude.DB.GraphQL.Schema;

/// <summary>Builds an immutable <see cref="GqlSchema"/> from a datamodel and an endpoint definition.</summary>
internal sealed class SchemaBuilder {
    /// <summary>A node type the endpoint exposes, with the properties and names settled for it.</summary>
    sealed class ExposedType {
        public required NodeTypeModel Type;
        public GraphQLTypeDefinition? Def;
        public required string Name;
        /// <summary>Selected property id → requested field name (null = derived).</summary>
        public required Dictionary<Guid, string?> OwnProperties;
        /// <summary>Final property id → field name, inherited names included.</summary>
        public Dictionary<Guid, string> FieldNames = [];
        public List<PropertyModel> Properties = [];
        public bool ReadOnly;
    }

    static readonly string[] _systemFieldNames = ["id", "displayName", "createdUtc", "changedUtc"];

    readonly Datamodel _dm;
    readonly GraphQLEndpointDefinition _def;
    readonly GraphQLOptions _options;
    readonly bool _exact;
    readonly List<string> _warnings = [];
    readonly NameRegistry _typeNames = new("Int", "Float", "String", "Boolean", "ID", "DateTime", "Long", "Decimal",
        "Query", "Mutation", "Node", "FileInfo", "GeoCoordinate", "GeoCoordinateInput", "RelatedNodeFilterInput");
    readonly List<GqlNamedType> _allTypes = [];
    readonly Dictionary<Guid, ExposedType> _exposedById = [];
    readonly Dictionary<Guid, GqlObjectType> _objectTypes = [];
    readonly Dictionary<Guid, GqlInterfaceType> _interfaceTypes = [];
    readonly Dictionary<Guid, GqlInterfaceType> _synthesizedInterfaces = [];
    readonly Dictionary<string, GqlEnumType?> _enumTypes = new(StringComparer.Ordinal);
    readonly Dictionary<string, GqlInputObjectType> _sharedInputs = new(StringComparer.Ordinal);
    readonly Dictionary<Guid, (GqlInputObjectType? Filter, GqlEnumType? OrderBy, GqlObjectType Wrapper)> _listTypes = [];
    GqlScalars _scalars = null!;
    GqlInterfaceType _node = null!;
    GqlObjectType? _fileInfo;
    GqlObjectType? _geo;
    GqlInputObjectType? _geoInput;
    List<ExposedType> _exposed = null!;

    SchemaBuilder(Datamodel dm, GraphQLEndpointDefinition def, GraphQLOptions options) {
        _dm = dm;
        _def = def;
        _options = options;
        _exact = def.ExactNames;
    }

    public static GqlSchema Build(Datamodel dm, GraphQLEndpointDefinition def, GraphQLOptions options) => new SchemaBuilder(dm, def, options).build();

    void warn(string message) => _warnings.Add(message);

    GqlSchema build() {
        _dm.EnsureInitalization();
        createScalars();
        _node = new GqlInterfaceType { Name = "Node", Description = "Base interface implemented by all node types." };
        _node.Fields.AddRange(systemFields());
        _allTypes.Add(_node);

        computeExposure();
        claimTypeNames();
        createShells();
        resolveFieldNames();
        foreach (var e in _exposed) buildFieldsFor(e);
        wireInterfacesAndPossibleTypes();

        var query = new GqlObjectType { Name = "Query", Description = "Entry points of the endpoint." };
        _allTypes.Add(query);
        buildRootFields(query);
        var mutation = _def.AllowMutations ? buildMutationFields() : null;

        var schema = new GqlSchema { Datamodel = _dm, Definition = _def, QueryType = query, MutationType = mutation, NodeInterface = _node, Scalars = _scalars };
        foreach (var t in _allTypes) {
            schema.Types.Add(t.Name, t);
            switch (t) {
                case GqlObjectType o: o.Seal(); break;
                case GqlInterfaceType i: i.Seal(); break;
                case GqlInputObjectType io: io.Seal(); break;
                case GqlEnumType en: en.Seal(); break;
            }
        }
        foreach (var t in _dm.NodeTypes.Values) {
            // an instance of an unexposed subtype is projected as its nearest exposed ancestor
            var nearest = nearestExposed(t);
            if (nearest != null && !nearest.IsInterface && _objectTypes.TryGetValue(nearest.Id, out var o)) schema.ObjectTypesByNodeTypeId.Add(t.Id, o);
            var reference = referenceTypeFor(t);
            if (reference != null) schema.ReferenceTypesByNodeTypeId.Add(t.Id, reference);
        }
        schema.Warnings.AddRange(_warnings);
        return schema;
    }

    // ---- exposure ----

    bool isExposable(NodeTypeModel t, bool explicitSelection) {
        if (t.Id == NodeConstants.BaseNodeTypeId) return false;
        if (t.Hidden || t.IsInnerNode) return false;
        if (!explicitSelection && !_def.IncludeSystemTypes && t.Namespace == "Relatude.DB.Native.Models") return false;
        return _options.TypeFilter?.Invoke(t) ?? true;
    }

    static Dictionary<Guid, string?> allProperties(NodeTypeModel t) {
        var d = new Dictionary<Guid, string?>();
        foreach (var p in t.AllProperties.Values) if (!p.Internal) d[p.Id] = null;
        return d;
    }

    void computeExposure() {
        var list = new List<ExposedType>();
        if (_def.Mode == GraphQLEndpointMode.WholeDatamodel) {
            foreach (var t in _dm.NodeTypes.Values) {
                if (!isExposable(t, explicitSelection: false)) continue;
                list.Add(new ExposedType { Type = t, Name = t.CodeName, OwnProperties = allProperties(t) });
            }
        } else {
            var seen = new HashSet<Guid>();
            foreach (var td in _def.Types) {
                if (!_dm.NodeTypes.TryGetValue(td.NodeTypeId, out var t)) {
                    warn($"Type {td.Name ?? td.NodeTypeId.ToString()} is not in the datamodel and was left out.");
                    continue;
                }
                if (!isExposable(t, explicitSelection: true)) {
                    warn($"Type {t.CodeName} cannot be exposed (hidden, inner or filtered out) and was left out.");
                    continue;
                }
                if (!seen.Add(t.Id)) {
                    warn($"Type {t.CodeName} is listed twice; the second entry was ignored.");
                    continue;
                }
                Dictionary<Guid, string?> own;
                if (td.Properties == null) {
                    own = allProperties(t);
                } else {
                    own = [];
                    foreach (var pd in td.Properties) {
                        if (!t.AllProperties.TryGetValue(pd.PropertyId, out var p)) {
                            warn($"{t.CodeName}: property {pd.Name ?? pd.PropertyId.ToString()} is not in the datamodel and was left out.");
                            continue;
                        }
                        if (p.Internal) continue;
                        own[p.Id] = string.IsNullOrWhiteSpace(pd.Name) ? null : pd.Name.Trim();
                    }
                }
                list.Add(new ExposedType {
                    Type = t, Def = td,
                    Name = string.IsNullOrWhiteSpace(td.Name) ? t.CodeName : td.Name.Trim(),
                    OwnProperties = own, ReadOnly = td.ReadOnly,
                });
            }
        }
        // ancestors first, so inherited field names are settled before the types that inherit them
        _exposed = list.OrderBy(e => e.Type.ThisAndAllInheritedTypes.Count).ThenBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Type.Id).ToList();
        foreach (var e in _exposed) _exposedById[e.Type.Id] = e;
    }

    void claimTypeNames() {
        foreach (var e in _exposed.OrderBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Type.Id)) {
            var wanted = NameRegistry.Sanitize(e.Name);
            var claimed = _typeNames.Claim(e.Name, e.Type.Namespace?.Replace('.', '_') + "_" + e.Type.CodeName);
            if (claimed != wanted) warn($"Type name {e.Name} is taken; {claimed} is used for {e.Type.FullName}.");
            e.Name = claimed;
        }
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
        foreach (var e in _exposed) {
            if (e.Type.IsInterface) {
                var i = new GqlInterfaceType { Name = e.Name, NodeType = e.Type, Description = e.Type.FullName };
                _interfaceTypes.Add(e.Type.Id, i);
                _allTypes.Add(i);
            } else {
                var o = new GqlObjectType { Name = e.Name, NodeType = e.Type, Description = e.Type.FullName };
                _objectTypes.Add(e.Type.Id, o);
                _allTypes.Add(o);
            }
        }
        // a concrete class with exposed concrete descendants needs an interface stand-in, so that
        // root/relation fields referring to it can return subclass instances without breaking the type system
        foreach (var e in _exposed) {
            if (e.Type.IsInterface) continue;
            var hasConcreteDescendants = e.Type.ThisAndDescendingTypes.Values.Any(d => d.Id != e.Type.Id && !d.IsInterface && _exposedById.ContainsKey(d.Id));
            if (!hasConcreteDescendants) continue;
            var i = new GqlInterfaceType {
                Name = _typeNames.Claim(e.Name + "Interface"),
                NodeType = e.Type,
                IsSynthesized = true,
                Description = $"Common fields of {e.Name} and its subtypes.",
            };
            _synthesizedInterfaces.Add(e.Type.Id, i);
            _allTypes.Add(i);
        }
    }

    /// <summary>The exposed type an instance of <paramref name="t"/> is seen as: itself, or its most derived exposed ancestor.</summary>
    NodeTypeModel? nearestExposed(NodeTypeModel t) {
        if (_exposedById.ContainsKey(t.Id)) return t;
        NodeTypeModel? best = null;
        foreach (var a in t.ThisAndAllInheritedTypes.Values) {
            if (a.Id == t.Id || a.IsInterface || !_exposedById.ContainsKey(a.Id)) continue;
            if (best == null || a.ThisAndAllInheritedTypes.Count > best.ThisAndAllInheritedTypes.Count) best = a;
        }
        return best;
    }

    /// <summary>The type used when referring to a node type in field positions.</summary>
    GqlNamedType? referenceTypeFor(NodeTypeModel t) {
        if (t.Id == NodeConstants.BaseNodeTypeId) return _node;
        if (_interfaceTypes.TryGetValue(t.Id, out var i)) return i;
        if (_synthesizedInterfaces.TryGetValue(t.Id, out var s)) return s;
        if (_objectTypes.TryGetValue(t.Id, out var o)) return o;
        var nearest = nearestExposed(t);
        return nearest == null || nearest.Id == t.Id ? null : referenceTypeFor(nearest);
    }

    IEnumerable<NodeTypeModel> ancestorsOf(NodeTypeModel t)
        => t.ThisAndAllInheritedTypes.Values.Where(a => a.Id != t.Id).OrderBy(a => a.CodeName, StringComparer.Ordinal).ThenBy(a => a.Id);

    // ---- field names ----

    string derivedFieldName(PropertyModel p) {
        var n = _exact ? NameRegistry.Sanitize(p.CodeName) : NameRegistry.CamelCase(p.CodeName);
        return _systemFieldNames.Contains(n) ? n + "Value" : n;
    }

    void resolveFieldNames() {
        foreach (var e in _exposed) {
            var used = new HashSet<string>(_systemFieldNames, StringComparer.Ordinal);
            foreach (var a in ancestorsOf(e.Type)) {
                if (!_exposedById.TryGetValue(a.Id, out var ae)) continue;
                foreach (var (pid, name) in ae.FieldNames) {
                    if (!e.Type.AllProperties.ContainsKey(pid)) continue;
                    if (e.FieldNames.TryAdd(pid, name)) used.Add(name);
                }
            }
            var own = e.OwnProperties.Keys.Select(id => e.Type.AllProperties[id]).OrderBy(p => p.CodeName, StringComparer.Ordinal).ThenBy(p => p.Id);
            foreach (var p in own) {
                var wanted = e.OwnProperties[p.Id];
                if (e.FieldNames.TryGetValue(p.Id, out var inherited)) {
                    if (wanted != null && NameRegistry.Sanitize(wanted) != inherited) warn($"{e.Name}.{wanted}: the field is named {inherited} by the base type that also exposes it, so that name is used.");
                    continue;
                }
                var name = wanted == null ? derivedFieldName(p) : NameRegistry.Sanitize(wanted);
                if (!used.Add(name)) {
                    var i = 2;
                    while (!used.Add(name + "_" + i)) i++;
                    warn($"{e.Name}.{name} ({p.CodeName}): the field name is taken; {name}_{i} is used.");
                    name = name + "_" + i;
                }
                e.FieldNames[p.Id] = name;
            }
            e.Properties = e.FieldNames.Keys
                .Select(id => e.Type.AllProperties.TryGetValue(id, out var p) ? p : null)
                .Where(p => p != null).Select(p => p!)
                .OrderBy(p => p.CodeName, StringComparer.Ordinal).ThenBy(p => p.Id)
                .ToList();
        }
    }

    // ---- node fields ----

    void buildFieldsFor(ExposedType e) {
        if (e.Type.IsInterface) _interfaceTypes[e.Type.Id].Fields.AddRange(buildNodeFields(e));
        else {
            _objectTypes[e.Type.Id].Fields.AddRange(buildNodeFields(e));
            if (_synthesizedInterfaces.TryGetValue(e.Type.Id, out var s)) s.Fields.AddRange(buildNodeFields(e));
        }
    }

    List<GqlField> buildNodeFields(ExposedType e) {
        var fields = systemFields();
        foreach (var p in e.Properties) {
            var f = buildPropertyField(e, p, e.FieldNames[p.Id]);
            if (f != null) fields.Add(f);
        }
        return fields;
    }

    List<GqlField> systemFields() => [
        new GqlField { Name = "id", Type = nn(_scalars.Id), Source = FieldSource.Id, Description = "The node's public id." },
        new GqlField { Name = "displayName", Type = _scalars.String, Source = FieldSource.DisplayName, Description = "The node's display name." },
        new GqlField { Name = "createdUtc", Type = nn(_scalars.DateTime), Source = FieldSource.CreatedUtc },
        new GqlField { Name = "changedUtc", Type = nn(_scalars.DateTime), Source = FieldSource.ChangedUtc },
    ];

    GqlField? buildPropertyField(ExposedType e, PropertyModel p, string name) {
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
                    if (target == null || refType == null) {
                        if (_def.Mode == GraphQLEndpointMode.Selected) warn($"{e.Name}.{name}: the related type {target?.CodeName ?? "?"} is not part of the endpoint, so the field was left out.");
                        return null;
                    }
                    if (rp.IsMany) {
                        return new GqlField {
                            Name = name, Type = nn(listOf(nn(refType))), Source = FieldSource.RelationMany,
                            Property = p, TargetNodeType = target, Arguments = { topArgument() },
                        };
                    }
                    return new GqlField { Name = name, Type = refType, Source = FieldSource.RelationOne, Property = p, TargetNodeType = target };
                }
            case PropertyType.Reference: {
                    var target = commonBase(((ReferencePropertyModel)p).NodeTypes);
                    var refType = target == null ? null : referenceTypeFor(target);
                    if (target == null || refType == null) {
                        if (_def.Mode == GraphQLEndpointMode.Selected) warn($"{e.Name}.{name}: the referenced type {target?.CodeName ?? "?"} is not part of the endpoint, so the field was left out.");
                        return null;
                    }
                    return new GqlField { Name = name, Type = refType, Source = FieldSource.ReferenceOne, Property = p, TargetNodeType = target };
                }
            case PropertyType.References: {
                    var target = commonBase(((ReferencesPropertyModel)p).NodeTypes);
                    var refType = target == null ? null : referenceTypeFor(target);
                    if (target == null || refType == null) {
                        if (_def.Mode == GraphQLEndpointMode.Selected) warn($"{e.Name}.{name}: the referenced type {target?.CodeName ?? "?"} is not part of the endpoint, so the field was left out.");
                        return null;
                    }
                    return new GqlField {
                        Name = name, Type = nn(listOf(nn(refType))), Source = FieldSource.ReferenceMany,
                        Property = p, TargetNodeType = target, Arguments = { topArgument() },
                    };
                }
            case PropertyType.File: {
                    _fileInfo ??= createFileInfoType();
                    return new GqlField { Name = name, Type = _fileInfo, Source = FieldSource.FileProperty, Property = p, Description = "Null when no file is uploaded." };
                }
            case PropertyType.GeoCoordinate: {
                    _geo ??= createGeoType();
                    return new GqlField { Name = name, Type = _geo, Source = FieldSource.GeoProperty, Property = p, Description = "Null when no position is set." };
                }
            case PropertyType.Integer: {
                    var ip = (IntegerPropertyModel)p;
                    if (ip.IsEnum) {
                        var et = getEnumType(ip.FullEnumTypeName, ip.LegalValueNames, ip.LegalValues);
                        if (et != null) return new GqlField { Name = name, Type = nn(et), Source = FieldSource.EnumProperty, Property = p };
                    }
                    return new GqlField { Name = name, Type = nn(_scalars.Int), Source = FieldSource.ScalarProperty, Property = p };
                }
            case PropertyType.EnumArray: {
                    var ep = (EnumArrayPropertyModel)p;
                    var et = getEnumType(ep.FullEnumTypeName, ep.LegalValueNames, ep.LegalValues);
                    if (et != null) return new GqlField { Name = name, Type = nn(listOf(nn(et))), Source = FieldSource.EnumArrayProperty, Property = p };
                    return new GqlField { Name = name, Type = nn(listOf(nn(_scalars.Int))), Source = FieldSource.ScalarProperty, Property = p };
                }
            default: {
                    var scalar = scalarTypeFor(p.PropertyType);
                    if (scalar == null) return null;
                    return new GqlField { Name = name, Type = scalar, Source = FieldSource.ScalarProperty, Property = p };
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

    GqlObjectType createGeoType() {
        var t = new GqlObjectType { Name = "GeoCoordinate", Description = "A position on the globe." };
        t.Fields.AddRange([
            new GqlField { Name = "latitude", Type = nn(_scalars.Float), Source = FieldSource.GeoLatitude },
            new GqlField { Name = "longitude", Type = nn(_scalars.Float), Source = FieldSource.GeoLongitude },
        ]);
        _allTypes.Add(t);
        return t;
    }

    GqlInputObjectType createGeoInputType() {
        var t = new GqlInputObjectType { Name = "GeoCoordinateInput", Description = "A position on the globe." };
        t.InputFields.Add(new GqlInputField { Name = "latitude", Type = nn(_scalars.Float) });
        t.InputFields.Add(new GqlInputField { Name = "longitude", Type = nn(_scalars.Float) });
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
        foreach (var e in _exposed.Where(e => !e.Type.IsInterface)) _node.PossibleTypes.Add(_objectTypes[e.Type.Id]);
        foreach (var e in _exposed) {
            var t = e.Type;
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

    // ---- root fields: per type, views ----

    string rootName(string typeName) => _exact ? typeName : NameRegistry.CamelCase(typeName);

    void buildRootFields(GqlObjectType query) {
        var rootNames = new NameRegistry("__schema", "__type", "__typename");
        foreach (var e in _exposed.OrderBy(e => e.Name, StringComparer.Ordinal)) {
            var refType = referenceTypeFor(e.Type)!;
            var filterInput = buildFilterInput(e);
            var orderByEnum = buildOrderByEnum(e.Name, (IGqlCompositeType)refType);
            var wrapper = buildResultWrapper(e.Name, refType, e.Type);
            _listTypes[e.Type.Id] = (filterInput, orderByEnum, wrapper);

            var singleName = claimRoot(rootNames, e.Def?.SingleName, rootName(e.Name), e.Name);
            query.Fields.Add(new GqlField {
                Name = singleName,
                Type = refType, Source = FieldSource.RootSingle, TargetNodeType = e.Type,
                Description = $"Fetches a single {e.Name} by id.",
                Arguments = { new GqlArgument { Name = "id", Type = nn(_scalars.Id) } },
            });

            var listName = claimRoot(rootNames, e.Def?.ListName, NameRegistry.Pluralize(rootName(e.Name)), e.Name);
            var list = new GqlField {
                Name = listName,
                Type = nn(wrapper), Source = FieldSource.RootList, TargetNodeType = e.Type,
                Description = $"Queries {e.Name} nodes with filtering, search, ordering and paging.",
            };
            addListArguments(list, e.Type);
            query.Fields.Add(list);
        }
        foreach (var view in _def.Views) buildViewField(query, rootNames, view);
    }

    string claimRoot(NameRegistry rootNames, string? requested, string derived, string typeName) {
        var wanted = string.IsNullOrWhiteSpace(requested) ? derived : requested.Trim();
        var claimed = rootNames.Claim(wanted, derived, derived + typeName);
        if (claimed != NameRegistry.Sanitize(wanted)) warn($"Root field name {wanted} is taken; {claimed} is used for {typeName}.");
        return claimed;
    }

    void addListArguments(GqlField list, NodeTypeModel t) {
        var (filterInput, orderByEnum, _) = _listTypes[t.Id];
        if (filterInput != null) list.Arguments.Add(new GqlArgument { Name = "filter", Type = filterInput });
        list.Arguments.Add(new GqlArgument { Name = "search", Type = _scalars.String, Description = "Free-text search (BM25 + optional semantic index)." });
        if (orderByEnum != null) {
            list.Arguments.Add(new GqlArgument { Name = "orderBy", Type = orderByEnum });
            list.Arguments.Add(new GqlArgument { Name = "descending", Type = _scalars.Boolean, DefaultValue = false, HasDefaultValue = true });
        }
        list.Arguments.Add(new GqlArgument { Name = "page", Type = _scalars.Int, DefaultValue = 0, HasDefaultValue = true, Description = "Zero-based page index." });
        list.Arguments.Add(new GqlArgument { Name = "pageSize", Type = _scalars.Int, Description = $"Defaults to {_def.DefaultPageSize}, capped at {_def.MaxPageSize}." });
        list.Arguments.Add(new GqlArgument { Name = "ids", Type = listOf(nn(_scalars.Id)), Description = "Restricts the result to the given node ids." });
    }

    void buildViewField(GqlObjectType query, NameRegistry rootNames, GraphQLViewDefinition view) {
        var label = string.IsNullOrWhiteSpace(view.Name) ? "(unnamed view)" : view.Name.Trim();
        var check = ViewQueries.Inspect(view.Query, _dm);
        if (check.Error != null) {
            warn($"View {label}: {check.Error}");
            return;
        }
        if (!_exposedById.TryGetValue(check.NodeType!.Id, out var e)) {
            warn($"View {label}: its type {check.NodeType.CodeName} is not part of the endpoint, so the view was left out.");
            return;
        }
        if (string.IsNullOrWhiteSpace(view.Name)) {
            warn("A view without a name was left out.");
            return;
        }
        var name = rootNames.Claim(view.Name.Trim());
        if (name != NameRegistry.Sanitize(view.Name.Trim())) warn($"View name {view.Name} is taken; {name} is used.");
        var (_, _, wrapper) = _listTypes[e.Type.Id];
        var field = new GqlField {
            Name = name, Type = nn(wrapper), Source = FieldSource.RootView, TargetNodeType = e.Type, ViewQuery = view.Query.Trim(),
            Description = string.IsNullOrWhiteSpace(view.Description) ? $"{e.Name} nodes selected by the view {name}." : view.Description.Trim(),
        };
        addListArguments(field, e.Type);
        query.Fields.Add(field);
    }

    // ---- filter inputs, orderBy enums, result wrappers ----

    GqlInputObjectType? buildFilterInput(ExposedType e) {
        var input = new GqlInputObjectType {
            Name = _typeNames.Claim(e.Name + "FilterInput"),
            Description = "All given conditions must match (logical AND). Use the search argument for text matching.",
        };
        var used = new HashSet<string>(["and", "or", "not"], StringComparer.Ordinal);
        foreach (var p in e.Properties) {
            var opInput = operatorInputFor(p);
            if (opInput == null) continue;
            var name = e.FieldNames[p.Id];
            if (!used.Add(name)) continue; // a property named and/or/not keeps the logical operator
            input.InputFields.Add(new GqlInputField { Name = name, Type = opInput, Property = p });
        }
        if (input.InputFields.Count == 0) return null;
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
                        var et = getEnumType(ip.FullEnumTypeName, ip.LegalValueNames, ip.LegalValues);
                        if (et != null) return enumOperatorInput(et);
                    }
                    return sharedOperatorInput("IntFilterInput", _scalars.Int, ordered: true, withIn: true);
                }
            case PropertyType.Relation:
            case PropertyType.Reference:
                return relatedNodeFilterInput();
            default:
                return null;
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
                Description = "Time spent fetching this result from the store, including loading the selected relations." },
        ]);
        _allTypes.Add(w);
        return w;
    }

    // ---- mutations ----

    GqlObjectType? buildMutationFields() {
        var mutation = new GqlObjectType { Name = "Mutation", Description = "Creates, updates and deletes nodes." };
        var rootNames = new NameRegistry("__typename");
        foreach (var e in _exposed.Where(e => !e.Type.IsInterface && !e.ReadOnly).OrderBy(e => e.Name, StringComparer.Ordinal)) {
            var input = buildInputType(e);
            if (input == null) continue;
            var objectType = _objectTypes[e.Type.Id];
            string name(string verb) => rootNames.Claim((_exact ? char.ToUpperInvariant(verb[0]) + verb[1..] : verb) + e.Name);
            mutation.Fields.Add(new GqlField {
                Name = name("create"), Type = objectType, Source = FieldSource.MutationCreate, TargetNodeType = e.Type,
                Description = $"Creates a {e.Name} and returns it.",
                Arguments = { new GqlArgument { Name = "input", Type = nn(input) } },
            });
            mutation.Fields.Add(new GqlField {
                Name = name("update"), Type = objectType, Source = FieldSource.MutationUpdate, TargetNodeType = e.Type,
                Description = $"Updates the given fields of a {e.Name} and returns it. Fields left out are unchanged; null clears a field.",
                Arguments = { new GqlArgument { Name = "id", Type = nn(_scalars.Id) }, new GqlArgument { Name = "input", Type = nn(input) } },
            });
            mutation.Fields.Add(new GqlField {
                Name = name("delete"), Type = nn(_scalars.Boolean), Source = FieldSource.MutationDelete, TargetNodeType = e.Type,
                Description = $"Deletes a {e.Name}. Returns false when no such node exists.",
                Arguments = { new GqlArgument { Name = "id", Type = nn(_scalars.Id) } },
            });
        }
        if (mutation.Fields.Count == 0) return null;
        _allTypes.Add(mutation);
        return mutation;
    }

    GqlInputObjectType? buildInputType(ExposedType e) {
        var fields = new List<GqlInputField>();
        foreach (var p in e.Properties) {
            var shaped = inputTypeFor(p);
            if (shaped == null) continue;
            fields.Add(new GqlInputField { Name = e.FieldNames[p.Id], Type = shaped.Value.Type, Property = p, Source = shaped.Value.Source });
        }
        if (fields.Count == 0) return null;
        var input = new GqlInputObjectType {
            Name = _typeNames.Claim(e.Name + "Input"),
            Description = $"Values of a {e.Name}. Relations and references take the id of the related node.",
        };
        input.InputFields.AddRange(fields);
        _allTypes.Add(input);
        return input;
    }

    (GqlType Type, FieldSource Source)? inputTypeFor(PropertyModel p) {
        switch (p.PropertyType) {
            case PropertyType.Any:
            case PropertyType.ByteArray:
            case PropertyType.FloatArray:
            case PropertyType.Embedded:
            case PropertyType.File:
                return null;
            case PropertyType.Relation:
                return ((RelationPropertyModel)p).IsMany ? (listOf(nn(_scalars.Id)), FieldSource.RelationMany) : (_scalars.Id, FieldSource.RelationOne);
            case PropertyType.Reference:
                return (_scalars.Id, FieldSource.ReferenceOne);
            case PropertyType.References:
                return (listOf(nn(_scalars.Id)), FieldSource.ReferenceMany);
            case PropertyType.GeoCoordinate:
                _geoInput ??= createGeoInputType();
                return (_geoInput, FieldSource.GeoProperty);
            case PropertyType.Integer: {
                    var ip = (IntegerPropertyModel)p;
                    if (ip.IsEnum) {
                        var et = getEnumType(ip.FullEnumTypeName, ip.LegalValueNames, ip.LegalValues);
                        if (et != null) return (et, FieldSource.EnumProperty);
                    }
                    return (_scalars.Int, FieldSource.ScalarProperty);
                }
            case PropertyType.EnumArray: {
                    var ep = (EnumArrayPropertyModel)p;
                    var et = getEnumType(ep.FullEnumTypeName, ep.LegalValueNames, ep.LegalValues);
                    if (et != null) return (listOf(nn(et)), FieldSource.EnumArrayProperty);
                    return (listOf(nn(_scalars.Int)), FieldSource.ScalarProperty);
                }
            default: {
                    var scalar = scalarTypeFor(p.PropertyType);
                    if (scalar == null) return null;
                    return (scalar is GqlNonNullType nonNull ? nonNull.OfType : scalar, FieldSource.ScalarProperty);
                }
        }
    }

    static GqlNonNullType nn(GqlType t) => new(t);
    static GqlListType listOf(GqlType t) => new(t);
}
