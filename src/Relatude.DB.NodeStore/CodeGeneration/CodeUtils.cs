using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Nodes;
using System.Text;

namespace Relatude.DB.CodeGeneration;

internal static class CodeUtils {
    public static string FieldOrProperty(string type, string name, ModelType mType, string? defaultDeclaration = null, bool getterOnly = false) {
        var accessors = getterOnly ? " { get; }" : " { get; set; }"; // embedded (list) properties must not have a setter
        switch (mType) {
            case ModelType.Interface: return type + " " + name + accessors;
            case ModelType.Class: return "public " + type + " " + name + accessors + (string.IsNullOrEmpty(defaultDeclaration) ? "" : (" = " + defaultDeclaration + ";"));
            case ModelType.Record: return "public " + type + " " + name + accessors;
            case ModelType.Struct: return "public " + type + " " + name + ";";
            default: throw new Exception("Unknown model type " + mType);
        }
    }
    // The base node type (INode) is synthetic and has no CLR type: when a set of target types
    // only meet at the base, no valid member type can be generated - fail with a clear message
    // instead of emitting a type name that will not compile.
    internal static NodeTypeModel CommonBaseWithClrType(Datamodel dm, IEnumerable<Guid> ids, string context) {
        var t = dm.FindFirstCommonBase(ids);
        if (t.Id == NodeConstants.BaseNodeTypeId && !ids.Contains(NodeConstants.BaseNodeTypeId))
            throw new Exception("Unable to generate code for " + context + ": the types "
                + string.Join(", ", ids.Select(id => dm.NodeTypes.TryGetValue(id, out var nt) ? nt.CodeName : id.ToString()))
                + " have no common base type in the datamodel. ");
        return t;
    }
    public static string GetTypeName(PropertyModel p, Datamodel datamodel) {
        if (p is IntegerPropertyModel intP && intP.IsEnum) {
            if (string.IsNullOrEmpty(intP.FullEnumTypeName))
                throw new Exception("The enum property " + p.CodeName + " has no FullEnumTypeName, so no code can be generated for it. "
                    + "When an enum property is defined in JSON, FullEnumTypeName must name an enum type that exists at runtime. ");
            return intP.FullEnumTypeName;
        }
        if (p is EnumArrayPropertyModel enumArrayP) {
            if (string.IsNullOrEmpty(enumArrayP.FullEnumTypeName))
                throw new Exception("The enum array property " + p.CodeName + " has no FullEnumTypeName, so no code can be generated for it. "
                    + "When an enum array property is defined in JSON, FullEnumTypeName must name an enum type that exists at runtime. ");
            return enumArrayP.FullEnumTypeName + "[]";
        }
        return p.PropertyType switch {
            PropertyType.Boolean => "bool",
            PropertyType.Integer => "int",
            PropertyType.Double => "double",
            PropertyType.Float => "float",
            PropertyType.String => "string",
            PropertyType.StringArray => "string[]",
            PropertyType.GuidArray => "Guid[]",
            PropertyType.Guid => "Guid",
            PropertyType.DateTime => "DateTime",
            PropertyType.DateTimeOffset => "DateTimeOffset",
            PropertyType.TimeSpan => "TimeSpan",
            PropertyType.GeoCoordinate => "Relatude.DB.Common.GeoCoordinate",
            PropertyType.Long => "long",
            PropertyType.ByteArray => "byte[]",
            PropertyType.FloatArray => "float[]",
            PropertyType.Decimal => "decimal",
            PropertyType.File => "Relatude.DB.Common.FileValue",
            PropertyType.Embedded => getTypeNameEmbedded(p, datamodel),
            PropertyType.Reference => getTypeNameReference(p, datamodel),
            PropertyType.References => getTypeNameReferences(p, datamodel),
            PropertyType.Relation => getTypeNameRelationCollection(p, datamodel),
            _ => throw new NotSupportedException("The type " + p.PropertyType + " is not supported by the code generator."),
        };
    }
    static string getTypeNameEmbedded(PropertyModel p, Datamodel dm) {
        if (p is not EmbeddedPropertyModel inp) throw new Exception("PropertyModel " + p.ToString() + " is not an EmbeddedPropertyModel.");
        var typeName = string.Empty;
        switch (inp.EmbeddedValueType) {
            case EmbeddedValueType.InnerNodeList:
                typeName += nameWithoutGeneric<Embedded<object>>();
                typeName += "<";
                typeName += CommonBaseWithClrType(dm, inp.InnerNodeTypes, "embedded property " + p.CodeName).FullName;
                typeName += ">";
                break;
            case EmbeddedValueType.InnerNodeMap:
                typeName += nameWithoutGeneric<EmbeddedMap<object, object>>();
                typeName += "<";
                typeName += GetInnerPropertyKeyPropertyTypeName(inp, dm);
                typeName += ", ";
                typeName += CommonBaseWithClrType(dm, inp.InnerNodeTypes, "embedded property " + p.CodeName).FullName;
                typeName += ">";
                break;
            default:
                throw new Exception("Unknown EmbeddedValueType " + inp.EmbeddedValueType);
        }
        return typeName;
    }
    static string getTypeNameReference(PropertyModel p, Datamodel dm) {
        if (p is not ReferencePropertyModel inp) throw new Exception("PropertyModel " + p.ToString() + " is not a ReferencePropertyModel.");
        var nodeType = CommonBaseWithClrType(dm, inp.NodeTypes, "reference property " + p.CodeName).FullName;
        return inp.ReferenceValueType switch {
            ReferenceValueType.Wrapper => nameWithoutGeneric<Reference<object>>() + "<" + nodeType + ">",
            ReferenceValueType.Object => nodeType,
            _ => throw new NotSupportedException("The reference value type " + inp.ReferenceValueType + " is not supported for single references."),
        };
    }
    static string getTypeNameReferences(PropertyModel p, Datamodel dm) {
        if (p is not ReferencesPropertyModel inp) throw new Exception("PropertyModel " + p.ToString() + " is not a ReferencesPropertyModel.");
        var nodeType = CommonBaseWithClrType(dm, inp.NodeTypes, "references property " + p.CodeName).FullName;
        return inp.ReferenceValueType switch {
            ReferenceValueType.Wrapper => nameWithoutGeneric<References<object>>() + "<" + nodeType + ">",
            ReferenceValueType.Array => nodeType + "[]",
            ReferenceValueType.List => "List<" + nodeType + ">",
            ReferenceValueType.Collection => "ICollection<" + nodeType + ">",
            ReferenceValueType.Enumerable => "IEnumerable<" + nodeType + ">",
            _ => throw new NotSupportedException("The reference value type " + inp.ReferenceValueType + " is not supported for references."),
        };
    }
    public static string GetInnerPropertyKeyPropertyTypeName(EmbeddedPropertyModel p, Datamodel dm) {
        switch (p.EmbeddedValueType) {
            case EmbeddedValueType.InnerNodeList:
                return typeof(Guid).FullName!;
            case EmbeddedValueType.InnerNodeMap:
                if (p.KeyProperty == InnerNodeDataMap<object>.PropertyIdNodeGuidId) {
                    return typeof(Guid).FullName!;
                } else if (p.KeyProperty == InnerNodeDataMap<object>.PropertyIdNodeIntId) {
                    return typeof(int).FullName!;
                } else {
                    if (p.KeyProperty != Guid.Empty) {
                        if (dm.Properties.TryGetValue(p.KeyProperty, out var keyProp)) {
                            if (keyProp.PropertyType == PropertyType.Embedded) // prevent recursive loop....
                                throw new Exception("The embedded map property " + p.CodeName + " uses an embedded property as its key. An embedded property cannot be a map key. ");
                            return GetTypeName(keyProp, dm);
                        } else {
                            throw new Exception("The embedded map property " + p.CodeName + " refers to a key property with id " + p.KeyProperty
                                + " that is not part of the datamodel. Correct the key property id, or add the property to the datamodel. ");
                        }
                    } else {
                        throw new Exception("The embedded map property " + p.CodeName + " has no key property. "
                            + "Set the key type to the node id, or name a key property of the inner node type. ");
                    }
                }
            default:
                throw new Exception("Unknown EmbeddedValueType: " + p.EmbeddedValueType);
        }
    }
    static string nameWithoutGeneric<T>() {
        var fullName = typeof(T).FullName!;
        var index = fullName.IndexOf('`');
        if (index >= 0) return fullName.Substring(0, index);
        return fullName;
    }
    static string getTypeNameRelationCollection(PropertyModel p, Datamodel dm) {
        if (p is not RelationPropertyModel rp) throw new Exception("PropertyModel " + p.ToString() + " is not a RelationPropertyModel.");
        var relation = dm.Relations[rp.RelationId];
        // only non-native shapes need a CLR type for the related node (native shapes use the relation class):
        var nodeType = new Lazy<NodeTypeModel>(() =>
            CommonBaseWithClrType(dm, rp.FromTargetToSource ? relation.SourceTypes : relation.TargetTypes, "relation property " + p.CodeName));
        if (rp.IsMany) {
            if (rp.RelationValueType == RelationValueType.Array) return nodeType.Value + "[]";
            if (rp.RelationValueType == RelationValueType.List) return "List<" + nodeType.Value + ">";
            if (rp.RelationValueType == RelationValueType.Collection) return "ICollection<" + nodeType.Value + ">";
            if (rp.RelationValueType == RelationValueType.Enumerable) return "IEnumerable<" + nodeType.Value + ">";
            if (rp.RelationValueType == RelationValueType.Native) {
                var code = relation.FullName();
                switch (relation.RelationType) {
                    case RelationType.OneOne:
                        if (string.IsNullOrEmpty(relation.CodeNameSources)) {
                            code += "." + nameof(OneOne<object>.One);
                        } else {
                            code += "." + relation.CodeNameSources;
                        }
                        break;
                    case RelationType.ManyMany:
                        if (string.IsNullOrEmpty(relation.CodeNameSources)) {
                            code += "." + nameof(ManyMany<object>.Many);
                        } else {
                            code += "." + relation.CodeNameSources;
                        }
                        break;
                    case RelationType.OneToMany:
                        if (string.IsNullOrEmpty(relation.CodeNameSources)) {
                            if (rp.FromTargetToSource) code += "." + nameof(OneToMany<object, object>.Many);
                            else code += "." + nameof(OneToMany<object, object>.One);
                        } else {
                            if (string.IsNullOrEmpty(relation.CodeNameTargets)) throw new Exception("Relation " + relation.CodeName + " is missing CodeNameTargets.");
                            if (rp.FromTargetToSource) code += "." + relation.CodeNameSources;
                            else code += "." + relation.CodeNameTargets;
                        }
                        break;
                    case RelationType.ManyToMany:
                        if (string.IsNullOrEmpty(relation.CodeNameSources)) {
                            if (rp.FromTargetToSource) code += "." + nameof(ManyToMany<object, object>.ManyFrom);
                            else code += "." + nameof(ManyToMany<object, object>.ManyTo);
                        } else {
                            if (string.IsNullOrEmpty(relation.CodeNameTargets)) throw new Exception("Relation " + relation.CodeName + " is missing CodeNameTargets.");
                            if (rp.FromTargetToSource) code += "." + relation.CodeNameSources;
                            else code += "." + relation.CodeNameTargets;
                        }
                        break;
                    default:
                        throw new NotSupportedException("The relation type " + relation.RelationType + " is not supported by the code generator.");
                }
                return code;
            }
            throw new NotSupportedException("The collection type " + rp.RelationValueType + " is not supported by the code generator.");
        } else {
            if (rp.RelationValueType == RelationValueType.Native) {
                var code = relation.FullName();
                switch (relation.RelationType) {
                    case RelationType.OneOne:
                        if (string.IsNullOrEmpty(relation.CodeNameSources)) {
                            code += "." + nameof(OneOne<object>.One);
                        } else {
                            code += "." + relation.CodeNameSources;
                        }
                        break;
                    case RelationType.OneToOne:
                        if (string.IsNullOrEmpty(relation.CodeNameSources)) {
                            if (rp.FromTargetToSource) code += "." + nameof(OneToOne<object, object>.OneFrom);
                            else code += "." + nameof(OneToOne<object, object>.OneTo);
                        } else {
                            if (string.IsNullOrEmpty(relation.CodeNameTargets)) throw new Exception("Relation " + relation.CodeName + " is missing CodeNameTargets.");
                            if (rp.FromTargetToSource) code += "." + relation.CodeNameSources;
                            else code += "." + relation.CodeNameTargets;
                        }
                        break;
                    case RelationType.OneToMany:
                        if (string.IsNullOrEmpty(relation.CodeNameSources)) {
                            code += "." + nameof(OneToMany<object, object>.One);
                        } else {
                            code += "." + relation.CodeNameSources;
                        }
                        break;
                    default:
                        throw new NotSupportedException("The relation type " + relation.RelationType + " is not supported by the code generator.");
                }
                return code;
            } else {
                return nodeType.Value.ToString();
            }
        }
    }
    public static string GuidName(Guid g) => "g" + g.ToString().Replace("-", "");
    public static void Generate_CreateStaticGuids(StringBuilder sb, NodeTypeModel nodeDef, Datamodel datamodel) {
        sb.AppendLine("static Guid " + GuidName(nodeDef.Id) + " = Guid.Parse(\"" + nodeDef.Id + "\");");
        foreach (var p in nodeDef.AllProperties) {
            sb.AppendLine("static Guid " + GuidName(p.Key) + " = Guid.Parse(\"" + p.Key + "\");");
            if (p.Value is EmbeddedPropertyModel inp) {
                sb.AppendLine("static Guid " + GuidName(p.Key) + "_KeyProperty = Guid.Parse(\"" + inp.KeyProperty + "\");");
            }
        }
    }
    public static string? getDefaultDeclaration(string? currentNamespace, PropertyModel p, Datamodel dm) {
        if (p is not RelationPropertyModel rp) return p.GetDefaultDeclaration();
        if (rp.RelationValueType != RelationValueType.Native) return p.GetDefaultDeclaration();
        return "new()";
    }
    public static bool IsFirstClassUsingName_NameOfInternalIdProperty(NodeTypeModel nodeDef, Datamodel datamodel) {
        return isFirstClassInParentsThatUseThisName(nodeDef.NameOfInternalIdProperty!, nodeDef, datamodel, n => n.NameOfInternalIdProperty!);
    }
    public static bool IsFirstClassUsingName_NameOfPublicIdProperty(NodeTypeModel nodeDef, Datamodel datamodel) {
        return isFirstClassInParentsThatUseThisName(nodeDef.NameOfPublicIdProperty!, nodeDef, datamodel, n => n.NameOfPublicIdProperty!);
    }
    public static bool IsFirstClassUsingName_NameOfChangedUtcProperty(NodeTypeModel nodeDef, Datamodel datamodel) {
        return isFirstClassInParentsThatUseThisName(nodeDef.NameOfChangedUtcProperty!, nodeDef, datamodel, n => n.NameOfChangedUtcProperty!);
    }
    public static bool IsFirstClassUsingName_NameOfCreatedUtcProperty(NodeTypeModel nodeDef, Datamodel datamodel) {
        return isFirstClassInParentsThatUseThisName(nodeDef.NameOfCreatedUtcProperty!, nodeDef, datamodel, n => n.NameOfCreatedUtcProperty!);
    }
    public static bool IsFirstClassUsingName_NameOfMetaProperty(NodeTypeModel nodeDef, Datamodel datamodel) {
        return isFirstClassInParentsThatUseThisName(nodeDef.NameOfMetaProperty!, nodeDef, datamodel, n => n.NameOfMetaProperty!);
    }
    public static bool IsFirstClassUsingName_NameOfDisplayNameProperty(NodeTypeModel nodeDef, Datamodel datamodel) {
        return isFirstClassInParentsThatUseThisName(nodeDef.NameOfDisplayNameProperty!, nodeDef, datamodel, n => n.NameOfDisplayNameProperty!);
    }
    public static bool IsFirstClassUsingName_NameOfAddressProperty(NodeTypeModel nodeDef, Datamodel datamodel) {
        return isFirstClassInParentsThatUseThisName(nodeDef.NameOfAddressProperty!, nodeDef, datamodel, n => n.NameOfAddressProperty!);
    }
    static bool isFirstClassInParentsThatUseThisName(string propName, NodeTypeModel nodeDef, Datamodel datamodel, Func<NodeTypeModel, string> getPropName) {
        if (nodeDef.Parents.Count == 0) return true;
        foreach (var parentId in nodeDef.Parents) {
            var parent = datamodel.NodeTypes[parentId];
            // an interface parent only declares the member - a class or record must still
            // implement it itself, so only class parents suppress the declaration:
            if (nodeDef.ModelType != ModelType.Interface && parent.IsInterface) continue;
            if (getPropName(parent) == propName) return false;
            if (!isFirstClassInParentsThatUseThisName(propName, parent, datamodel, getPropName)) return false;
        }
        return true;
    }
}
