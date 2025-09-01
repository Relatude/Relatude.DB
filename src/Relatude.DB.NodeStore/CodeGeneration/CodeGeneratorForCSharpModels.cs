using System.Text;
using System.Xml.Linq;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Nodes;

namespace Relatude.DB.CodeGeneration;
public static class CodeGeneratorForCSharpModels {
    public static string GenerateCSharpModelCode(Datamodel datamodel, bool addAttributes = true) {
        var sb = new StringBuilder();
        datamodel.EnsureInitalization();
        sb.AppendLine("using " + typeof(object).Namespace + ";"); // System namespace only
        sb.AppendLine("");
        var nodeTypesByNamespace = datamodel.NodeTypes.Values
            .Where(n => n.Id != NodeConstants.BaseNodeTypeId)
            .GroupBy(n => n.Namespace ?? string.Empty)
            .OrderBy(g => g.Key).Select(g => new { Namespace = g.Key, NodeTypes = g });
        foreach (var kv in nodeTypesByNamespace) {
            if (!string.IsNullOrEmpty(kv.Namespace)) sb.AppendLine("namespace " + kv.Namespace + " {");
            foreach (var nodeDef in kv.NodeTypes) {
                sb.AppendLine("");
                appendModelCode(nodeDef, datamodel, sb, addAttributes);
            }
            if (!string.IsNullOrEmpty(kv.Namespace)) {
                sb.AppendLine("");
                sb.AppendLine("}"); // end namespace
            }
        }


        var relationsByNamespace = datamodel.Relations.Values
            .GroupBy(r => r.Namespace ?? string.Empty)
            .OrderBy(g => g.Key).Select(g => new { Namespace = g.Key, Relations = g });
        foreach (var kv in relationsByNamespace) {
            sb.AppendLine("");
            if (!string.IsNullOrEmpty(kv.Namespace)) sb.AppendLine("namespace " + kv.Namespace + " {");
            foreach (var relation in kv.Relations) {
                sb.AppendLine("");
                appendRelationCode(relation, datamodel, sb, addAttributes);
            }
            if (!string.IsNullOrEmpty(kv.Namespace)) {
                sb.AppendLine("");
                sb.AppendLine("}"); // end namespace
            }
            sb.AppendLine("");
        }
        return sb.ToString();
    }
    static string fieldOrProperty(string type, string name, ModelType mType, string? defaultDeclaration = null) {
        switch (mType) {
            case ModelType.Interface: return type + " " + name + " { get; set; }";
            case ModelType.Class: return "public " + type + " " + name + " { get; set; }" + (string.IsNullOrEmpty(defaultDeclaration) ? "" : (" = " + defaultDeclaration + ";"));
            case ModelType.Record: return "public " + type + " " + name + " { get; set; }";
            case ModelType.Struct: return type + " " + name + ";";
            default: throw new Exception("Unknown model type " + mType);
        }
    }
    static void appendModelCode(NodeTypeModel nodeDef, Datamodel datamodel, StringBuilder sb, bool addAttributes) {
        if (addAttributes) {
            sb.AppendLine("    [" + nameAtt<NodeAttribute>() + "(" + nameof(NodeAttribute.Id) + " = \"" + nodeDef.Id + "\")]");
            // TODO: add more attributes
        }
        var inheritance = string.Join(", ", nodeDef.Parents
            .Where(id => id != NodeConstants.BaseNodeTypeId)
            .Select(id => TypeAndNamespace(nodeDef.Namespace, datamodel.NodeTypes[id].FullName)));
        if (!string.IsNullOrEmpty(inheritance)) inheritance = " : " + inheritance;
        sb.AppendLine("    public " + nodeDef.ModelType.ToString().ToLower() + " " + nodeDef.CodeName + inheritance + " {");
        if (!string.IsNullOrEmpty(nodeDef.NameOfPublicIdProperty) && isFirstClassUsingName_NameOfPublicIdProperty(nodeDef, datamodel)) {
            if (addAttributes) sb.AppendLine("        [" + nameAtt<PublicIdPropertyAttribute>() + "()]");
            string typeName = nodeDef.DataTypeOfPublicId switch {
                DataTypePublicId.Guid => "Guid",
                DataTypePublicId.String => "string",
                _ => throw new Exception("Unknown datatype of public id: " + nodeDef.DataTypeOfPublicId),
            };
            sb.Append("        ");
            sb.AppendLine(fieldOrProperty(typeName, nodeDef.NameOfPublicIdProperty, nodeDef.ModelType));
        }
        if (!string.IsNullOrEmpty(nodeDef.NameOfInternalIdProperty) && isFirstClassUsingName_NameOfInternalIdProperty(nodeDef, datamodel)) {
            if (addAttributes) sb.AppendLine("        [" + nameAtt<InternalIdPropertyAttribute>() + "()]");
            sb.Append("        ");
            sb.AppendLine(fieldOrProperty(nodeDef.DataTypeOfInternalId?.ToString().ToLower() + "", nodeDef.NameOfInternalIdProperty, nodeDef.ModelType));
        }
        if (!string.IsNullOrEmpty(nodeDef.NameOfCreatedUtcProperty) && isFirstClassUsingName_NameOfCreatedUtcProperty(nodeDef, datamodel)) {
            if (addAttributes) sb.AppendLine("        [" + nameAtt<CreatedUtcPropertyAttribute>() + "()]");
            sb.Append("        ");
            sb.AppendLine(fieldOrProperty("DateTime", nodeDef.NameOfCreatedUtcProperty, nodeDef.ModelType));
        }
        if (!string.IsNullOrEmpty(nodeDef.NameOfChangedUtcProperty) && isFirstClassUsingName_NameOfChangedUtcProperty(nodeDef, datamodel)) {
            if (addAttributes) sb.AppendLine("        [" + nameAtt<ChangedUtcPropertyAttribute>() + "()]");
            sb.Append("        ");
            sb.AppendLine(fieldOrProperty("DateTime", nodeDef.NameOfChangedUtcProperty, nodeDef.ModelType));
        }
        foreach (var p in nodeDef.Properties.Values.Where(p => !p.Private)) {
            if (addAttributes) addPropertyAttribute(p, datamodel, sb);
            var typeName = CodeGeneratorForValueMappers.GetTypeName(p, datamodel);
            typeName = TypeAndNamespace(nodeDef.Namespace, typeName);
            sb.Append("        ");
            sb.AppendLine(fieldOrProperty(typeName, p.CodeName, nodeDef.ModelType, getDefaultDeclaration(nodeDef.Namespace, p, datamodel)));
        }
        sb.AppendLine("    }"); // end class
    }
    static string? getDefaultDeclaration(string? currentNamespace, PropertyModel p, Datamodel dm) {
        if (p is not RelationPropertyModel rp) return p.GetDefaultDeclaration();
        if (rp.RelationValueType != RelationValueType.Native) return p.GetDefaultDeclaration();
        // For native relation properties, we need to generate the code based on the relation type and this can only be done in the context of the NodeStore
        // as this is where the OneToMany, ManyToMany, etc. are defined. So we can use namof to get the type name of the relation property.
        var relation = dm.Relations[rp.RelationId];
        var code = TypeAndNamespace(currentNamespace, relation.FullName());
        switch (relation.RelationType) {
            case RelationType.OneOne:
                code += "." + nameof(OneOne<object>.Empty);
                break;
            case RelationType.OneToOne:
                if (rp.FromTargetToSource) code += "." + nameof(OneToOne<object, object>.EmptyFrom);
                else code += "." + nameof(OneToOne<object, object>.EmptyTo);
                break;
            case RelationType.OneToMany:
                if (rp.FromTargetToSource) code += "." + nameof(OneToMany<object, object>.EmptyFrom);
                else code += "." + nameof(OneToMany<object, object>.EmptyTo);
                break;
            case RelationType.ManyMany:
                code += "." + nameof(ManyMany<object>.Empty);
                break;
            case RelationType.ManyToMany:
                if (rp.FromTargetToSource) code += "." + nameof(ManyToMany<object, object>.EmptyFrom);
                else code += "." + nameof(ManyToMany<object, object>.EmptyTo);
                break;
            default:
                throw new NotSupportedException("The relation type " + relation.RelationType + " is not supported by the code generator.");
        }
        return code;
    }
    static bool isFirstClassUsingName_NameOfInternalIdProperty(NodeTypeModel nodeDef, Datamodel datamodel) {
        return isFirstClassInParentsThatUseThisName(nodeDef.NameOfInternalIdProperty!, nodeDef, datamodel, n => n.NameOfInternalIdProperty!);
    }
    static bool isFirstClassUsingName_NameOfPublicIdProperty(NodeTypeModel nodeDef, Datamodel datamodel) {
        return isFirstClassInParentsThatUseThisName(nodeDef.NameOfPublicIdProperty!, nodeDef, datamodel, n => n.NameOfPublicIdProperty!);
    }
    static bool isFirstClassUsingName_NameOfChangedUtcProperty(NodeTypeModel nodeDef, Datamodel datamodel) {
        return isFirstClassInParentsThatUseThisName(nodeDef.NameOfChangedUtcProperty!, nodeDef, datamodel, n => n.NameOfChangedUtcProperty!);
    }
    static bool isFirstClassUsingName_NameOfCreatedUtcProperty(NodeTypeModel nodeDef, Datamodel datamodel) {
        return isFirstClassInParentsThatUseThisName(nodeDef.NameOfCreatedUtcProperty!, nodeDef, datamodel, n => n.NameOfCreatedUtcProperty!);
    }
    static bool isFirstClassInParentsThatUseThisName(string propName, NodeTypeModel nodeDef, Datamodel datamodel, Func<NodeTypeModel, string> getPropName) {
        if (nodeDef.Parents.Count == 0) return true;
        foreach (var parentId in nodeDef.Parents) {
            var parent = datamodel.NodeTypes[parentId];
            if (getPropName(parent) == propName) return false;
            if (!isFirstClassInParentsThatUseThisName(propName, parent, datamodel, getPropName)) return false;
        }
        return true;
    }
    static void addBaseAttributes<T>(PropertyModel p, Datamodel dm, StringBuilder sb, string? attributeName = null) where T : PropertyAttribute {
        if (attributeName == null) attributeName = nameAtt<T>();
        sb.Append("        [" + attributeName + "(");
        sb.Append(nameof(PropertyAttribute.Id) + " = \"" + p.Id + "\"");
        if (p.ExcludeFromTextIndex) sb.Append(", " + nameof(PropertyAttribute.ExcludeFromTextIndex) + " = true");
        if (p.DisplayName) sb.Append(", " + nameof(PropertyAttribute.DisplayName) + " = true");
        if (p.ReadAccess != Guid.Empty) sb.Append(", " + nameof(PropertyAttribute.ReadAccess) + " = \"" + p.ReadAccess + "\"");
        if (p.WriteAccess != Guid.Empty) sb.Append(", " + nameof(PropertyAttribute.WriteAccess) + " = \"" + p.WriteAccess + "\"");
    }
    static string addAttributeBool(BoolValue b) => nameof(BoolValue) + "." + b.ToString();
    static string nameAtt<T>() {
        var t = typeof(T);
        var s = t.Namespace + "." + t.Name;
        return s.Remove(s.Length - "Attribute".Length);
    }
    static void addPropertyAttribute(PropertyModel p, Datamodel dm, StringBuilder sb) {
        var nodeType = dm.NodeTypes[p.NodeType];
        if (nodeType.NameOfChangedUtcProperty == p.CodeName) sb.AppendLine("        [" + nameAtt<ChangedUtcPropertyAttribute>() + "()]");
        if (nodeType.NameOfCreatedUtcProperty == p.CodeName) sb.AppendLine("        [" + nameAtt<CreatedUtcPropertyAttribute>() + "()]");
        //if(nodeType.NameOfIsDerivedProperty == p.CodeName) {
        //    sb.AppendLine("[" + nameAtt<IsDerivedPropertyAttribute>() + "()]");
        //}
        //if(nodeType.NameOfLCIDProperty == p.CodeName) {
        //    sb.AppendLine("[" + nameAtt<LCIDPropertyAttribute>() + "()]");
        //}
        //if(nodeType.NameOfDerivedFromLCID == p.CodeName) {
        //    sb.AppendLine("[" + nameAtt<DerivedFromLCIDPropertyAttribute>() + "()]");
        //}
        switch (p.PropertyType) {
            case PropertyType.Boolean: {
                    addBaseAttributes<BooleanPropertyAttribute>(p, dm, sb);
                    var b = (BooleanPropertyModel)p;
                    if (b.Indexed) sb.Append(", " + nameof(BooleanPropertyAttribute.Indexed) + " = " + addAttributeBool(BoolValue.True));
                    if (b.DefaultValue) sb.Append(", " + nameof(BooleanPropertyAttribute.DefaultValue) + " = true");
                    sb.AppendLine(")]");
                }
                break;
            case PropertyType.Guid: {
                    addBaseAttributes<GuidPropertyAttribute>(p, dm, sb);
                    var b = (GuidPropertyModel)p;
                    if (b.Indexed) sb.Append(", " + nameof(GuidPropertyAttribute.Indexed) + " = " + addAttributeBool(BoolValue.True));
                    if (b.DefaultValue != Guid.Empty) sb.Append(", " + nameof(GuidPropertyAttribute.DefaultValue) + " = \"" + b.DefaultValue + "\"");
                    sb.AppendLine(")]");
                }
                break;
            case PropertyType.Integer: {
                    addBaseAttributes<IntegerPropertyAttribute>(p, dm, sb);
                    var i = (IntegerPropertyModel)p;
                    if (i.Indexed) sb.Append(", " + nameof(IntegerPropertyAttribute.Indexed) + " = " + addAttributeBool(BoolValue.True));
                    if (i.DefaultValue != 0) sb.Append(", " + nameof(IntegerPropertyAttribute.DefaultValue) + " = " + i.DefaultValue);
                    if (i.MinValue != int.MinValue) sb.Append(", " + nameof(IntegerPropertyAttribute.MinValue) + " = " + i.MinValue);
                    if (i.MaxValue != int.MaxValue) sb.Append(", " + nameof(IntegerPropertyAttribute.MaxValue) + " = " + i.MaxValue);
                    if (i.UniqueValues) sb.Append(", " + nameof(IntegerPropertyAttribute.MaxValue) + " = " + addAttributeBool(BoolValue.True));
                    if (i.IsEnum) {
                        sb.Append(", " + nameof(IntegerPropertyAttribute.IsEnum) + " = true");
                        if (!string.IsNullOrEmpty(i.FullEnumTypeName)) sb.Append(", " + nameof(IntegerPropertyAttribute.FullEnumTypeName) + " = \"" + i.FullEnumTypeName + "\"");
                    }
                    if (i.LegalValues != null) {
                        var legalValues = i.LegalValues.Select(v => v.ToString());
                        sb.Append(", " + nameof(IntegerPropertyAttribute.LegalValues) + " = new int[] {" + string.Join(", ", legalValues) + "}");
                    }
                    sb.AppendLine(")]");
                }
                break;
            case PropertyType.Double: {
                    addBaseAttributes<DoublePropertyAttribute>(p, dm, sb);
                    var i = (DoublePropertyModel)p;
                    if (i.Indexed) sb.Append(", " + nameof(DoublePropertyAttribute.Indexed) + " = " + addAttributeBool(BoolValue.True));
                    if (i.DefaultValue != 0) sb.Append(", " + nameof(DoublePropertyAttribute.DefaultValue) + " = " + i.DefaultValue);
                    if (i.MinValue != int.MinValue) sb.Append(", " + nameof(DoublePropertyAttribute.MinValue) + " = " + i.MinValue);
                    if (i.MaxValue != int.MaxValue) sb.Append(", " + nameof(DoublePropertyAttribute.MaxValue) + " = " + i.MaxValue);
                    sb.AppendLine(")]");
                }
                break;
            case PropertyType.DateTime: {
                    addBaseAttributes<DateTimePropertyAttribute>(p, dm, sb);
                    var d = (DateTimePropertyModel)p;
                    if (d.Indexed) sb.Append(", " + nameof(DateTimePropertyAttribute.Indexed) + " = " + addAttributeBool(BoolValue.True));
                    if (d.DefaultValue != DateTime.MinValue) sb.Append(", " + nameof(DateTimePropertyAttribute.DefaultValue) + " = \"" + d.DefaultValue + "\"");
                    if (d.MinValue != DateTime.MinValue) sb.Append(", " + nameof(DateTimePropertyAttribute.MinValue) + " = \"" + d.MinValue + "\"");
                    if (d.MaxValue != DateTime.MaxValue) sb.Append(", " + nameof(DateTimePropertyAttribute.MaxValue) + " = \"" + d.MaxValue + "\"");
                    sb.AppendLine(")]");
                }
                break;
            case PropertyType.Decimal: {
                    addBaseAttributes<DecimalPropertyAttribute>(p, dm, sb);
                    var i = (DecimalPropertyModel)p;
                    if (i.Indexed) sb.Append(", " + nameof(DecimalPropertyAttribute.Indexed) + " = " + addAttributeBool(BoolValue.True));
                    if (i.DefaultValue != 0) sb.Append(", " + nameof(DecimalPropertyAttribute.DefaultValue) + " = " + i.DefaultValue);
                    if (i.MinValue != int.MinValue) sb.Append(", " + nameof(DecimalPropertyAttribute.MinValue) + " = " + i.MinValue);
                    if (i.MaxValue != int.MaxValue) sb.Append(", " + nameof(DecimalPropertyAttribute.MaxValue) + " = " + i.MaxValue);
                    sb.AppendLine(")]");
                }
                break;
            case PropertyType.TimeSpan: {
                    addBaseAttributes<TimeSpanPropertyAttribute>(p, dm, sb);
                    var i = (TimeSpanPropertyModel)p;
                    if (i.Indexed) sb.Append(", " + nameof(TimeSpanPropertyAttribute.Indexed) + " = " + addAttributeBool(BoolValue.True));
                    if (i.DefaultValue != TimeSpan.Zero) sb.Append(", " + nameof(TimeSpanPropertyAttribute.DefaultValue) + " = \"" + i.DefaultValue + "\"");
                    if (i.MinValue != TimeSpan.MinValue) sb.Append(", " + nameof(TimeSpanPropertyAttribute.MinValue) + " = \"" + i.MinValue + "\"");
                    if (i.MaxValue != TimeSpan.MaxValue) sb.Append(", " + nameof(TimeSpanPropertyAttribute.MaxValue) + " = \"" + i.MaxValue + "\"");
                    sb.AppendLine(")]");
                }
                break;
            case PropertyType.Float: {
                    addBaseAttributes<FloatPropertyAttribute>(p, dm, sb);
                    var i = (FloatPropertyModel)p;
                    if (i.Indexed) sb.Append(", " + nameof(FloatPropertyAttribute.Indexed) + " = " + addAttributeBool(BoolValue.True));
                    if (i.DefaultValue != 0) sb.Append(", " + nameof(FloatPropertyAttribute.DefaultValue) + " = " + i.DefaultValue);
                    if (i.MinValue != int.MinValue) sb.Append(", " + nameof(FloatPropertyAttribute.MinValue) + " = " + i.MinValue);
                    if (i.MaxValue != int.MaxValue) sb.Append(", " + nameof(FloatPropertyAttribute.MaxValue) + " = " + i.MaxValue);
                    sb.AppendLine(")]");
                }
                break;
            case PropertyType.String: {
                    addBaseAttributes<StringPropertyAttribute>(p, dm, sb);
                    var s = (StringPropertyModel)p;
                    if (s.IndexedBySemantic) sb.Append(", " + nameof(StringPropertyAttribute.IndexedBySemantic) + " = " + addAttributeBool(BoolValue.True));
                    if (s.Indexed) sb.Append(", " + nameof(StringPropertyAttribute.Indexed) + " = " + addAttributeBool(BoolValue.True));
                    if (s.IndexedByWords) sb.Append(", " + nameof(StringPropertyAttribute.IndexedByWords) + " = " + addAttributeBool(BoolValue.True));
                    if (s.UniqueValues) sb.Append(", " + nameof(StringPropertyAttribute.UniqueValues) + " = true");
                    if (s.MinWordLength != StringPropertyModel.DefaultMinWordLength) sb.Append(", " + nameof(StringPropertyAttribute.MinWordLength) + " = " + s.MinWordLength);
                    if (s.MaxWordLength != StringPropertyModel.DefaultMaxWordLength) sb.Append(", " + nameof(StringPropertyAttribute.MaxWordLength) + " = " + s.MaxLength);
                    if (s.IgnoreDuplicateEmptyValues) sb.Append(", " + nameof(StringPropertyAttribute.IgnoreDuplicateEmptyValues) + " = true");
                    if (s.DefaultValue != null) sb.Append(", " + nameof(StringPropertyAttribute.DefaultValue) + " = \"" + s.DefaultValue + "\"");
                    if (s.StringType != StringValueType.AnyString) sb.Append(", " + nameof(StringPropertyAttribute.StringType) + " = " + typeof(StringValueType).Namespace + "." + nameof(StringValueType) + "." + s.StringType);
                    sb.AppendLine(")]");
                }
                break;
            case PropertyType.StringArray: {
                    addBaseAttributes<StringArrayPropertyAttribute>(p, dm, sb);
                    var s = (StringArrayPropertyModel)p;
                    if (s.Indexed) sb.Append(", " + nameof(StringArrayPropertyAttribute.Indexed) + " = true");
                    sb.AppendLine(")]");
                }
                break;
            case PropertyType.ByteArray: {
                    addBaseAttributes<ByteArrayPropertyAttribute>(p, dm, sb);
                    var s = (ByteArrayPropertyModel)p;
                    sb.AppendLine(")]");
                }
                break;
            case PropertyType.FloatArray: {
                    addBaseAttributes<FloatArrayPropertyAttribute>(p, dm, sb);
                    var s = (FloatArrayPropertyModel)p;
                    sb.AppendLine(")]");
                }
                break;
            case PropertyType.Relation: {
                    var attributeName = nameAtt<RelationPropertyAttribute>();
                    attributeName += "<";
                    var r = (RelationPropertyModel)p;
                    attributeName += dm.Relations[r.RelationId].ToString();
                    attributeName += ">";
                    addBaseAttributes<RelationPropertyAttribute>(p, dm, sb, attributeName);
                    if (r.FromTargetToSource) sb.Append(", " + nameof(RelationPropertyAttribute.RightToLeft) + " = true");
                    if (r.TextIndexRelatedContent) sb.Append(", " + nameof(RelationPropertyAttribute.TextIndexRelatedContent) + " = true");
                    if (r.TextIndexRelatedDisplayName) sb.Append(", " + nameof(RelationPropertyAttribute.TextIndexRelatedDisplayName) + " = true");
                    if (r.TextIndexRecursiveLevelLimit != 1) sb.Append(", " + nameof(RelationPropertyAttribute.TextIndexRecursiveLevelLimit) + " = " + r.TextIndexRecursiveLevelLimit);
                    sb.AppendLine(")]");
                }
                break;
            case PropertyType.File: {
                    addBaseAttributes<FilePropertyAttribute>(p, dm, sb);
                    var f = (FilePropertyModel)p;
                    if (f.FileStorageProviderId != Guid.Empty) sb.Append(", " + nameof(FilePropertyAttribute.FileStorageProviderId) + " = \"" + f.FileStorageProviderId + "\"");
                    sb.AppendLine(")]");
                }
                break;
            default:
                throw new NotImplementedException();
        }

    }
    static void appendRelationCode(RelationModel relation, Datamodel dm, StringBuilder sb, bool addAttributes) {
        if (addAttributes) {
            var attributeName = nameof(RelationAttribute);
            attributeName = typeof(RelationAttribute).Namespace + "." + attributeName.Remove(attributeName.Length - "Attribute".Length);
            attributeName = TypeAndNamespace(relation.Namespace, attributeName);
            sb.Append("    [" + attributeName + "(");
            sb.Append(nameof(RelationAttribute.Id) + " = \"" + relation.Id + "\"");
            if (relation.SourceTypes.Count > 0) {
                var guidStrings = relation.SourceTypes.Select(t => "\"" + t.ToString() + "\"");
                sb.Append(", " + nameof(RelationAttribute.SourceTypes) + " = [" + string.Join(", ", guidStrings) + "]");
            }
            if (relation.TargetTypes.Count > 0) {
                var guidStrings = relation.TargetTypes.Select(t => "\"" + t.ToString() + "\"");
                sb.Append(", " + nameof(RelationAttribute.TargetTypes) + " = [" + string.Join(", ", guidStrings) + "]");
            }
            sb.AppendLine(")]");
        }
        var inheritance = " : " + TypeAndNamespace(relation.Namespace, typeof(IRelation).Namespace, relation.RelationType.ToString()) + "<" + (relation.RelationType switch {
            RelationType.OneToMany =>
                TypeAndNamespace(relation.Namespace, dm.FindFirstCommonBase(relation.SourceTypes).FullName) + ", " +
                TypeAndNamespace(relation.Namespace, dm.FindFirstCommonBase(relation.TargetTypes).FullName),
            RelationType.OneToOne =>
                TypeAndNamespace(relation.Namespace, dm.FindFirstCommonBase(relation.SourceTypes).FullName) + ", " +
                TypeAndNamespace(relation.Namespace, dm.FindFirstCommonBase(relation.TargetTypes).FullName),
            RelationType.ManyToMany =>
                TypeAndNamespace(relation.Namespace, dm.FindFirstCommonBase(relation.SourceTypes).FullName) + ", " +
                TypeAndNamespace(relation.Namespace, dm.FindFirstCommonBase(relation.TargetTypes).FullName),
            RelationType.OneOne =>
            TypeAndNamespace(relation.Namespace, dm.FindFirstCommonBase(relation.SourceTypes).FullName),
            RelationType.ManyMany =>
                TypeAndNamespace(relation.Namespace, dm.FindFirstCommonBase(relation.SourceTypes).FullName),
            _ => throw new Exception("Unknown relation type " + relation.RelationType),
        });
        inheritance += ", " + relation.CodeName + ">"; // self reference
        sb.AppendLine("    public class " + relation.CodeName + inheritance + " { }");
    }
    public static string TypeAndNamespace(string currentNameSpace, Type type) {
        if (type.IsGenericType) {
            var typeName = type.Name.Substring(0, type.Name.IndexOf('`')); // remove the generic type parameter count
            var typeNamespace = type.Namespace ?? string.Empty;
            return TypeAndNamespace(currentNameSpace, typeNamespace, typeName);
        }
        return TypeAndNamespace(currentNameSpace, type.Namespace, type.Name);
    }
    public static string TypeAndNamespace(string? currentNameSpace, string fullTypeName) {
        var typeName = fullTypeName;
        string? typeNamespace = null;
        if (fullTypeName.Contains('.')) {
            var lastDotIndex = fullTypeName.LastIndexOf('.');
            typeNamespace = fullTypeName[..lastDotIndex];
            typeName = fullTypeName[(lastDotIndex + 1)..];
        }
        return TypeAndNamespace(currentNameSpace, typeNamespace, typeName);
    }
    public static string TypeAndNamespace(string? currentNameSpace, string? typeNamespace, string typeName) {
        if (string.IsNullOrEmpty(typeNamespace)) return typeName; // no namespace, just the type name
        if (string.IsNullOrEmpty(currentNameSpace)) return typeNamespace + "." + typeName; // no current namespace, use the type namespace
        //use relative namespace:
        if (typeNamespace.StartsWith(currentNameSpace)) {
            var relativeNamespace = typeNamespace == currentNameSpace ? "" : typeNamespace.Substring(currentNameSpace.Length + 1);
            return relativeNamespace.Length > 0 ? relativeNamespace + "." + typeName : typeName;
        }
        return typeNamespace + "." + typeName; // use the full namespace
    }
}
