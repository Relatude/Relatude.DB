using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Nodes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.DB.CodeGeneration;
internal static class CodeUtils {
    public static string FieldOrProperty(string type, string name, ModelType mType, string? defaultDeclaration = null) {
        switch (mType) {
            case ModelType.Interface: return type + " " + name + " { get; set; }";
            case ModelType.Class: return "public " + type + " " + name + " { get; set; }" + (string.IsNullOrEmpty(defaultDeclaration) ? "" : (" = " + defaultDeclaration + ";"));
            case ModelType.Record: return "public " + type + " " + name + " { get; set; }";
            case ModelType.Struct: return type + " " + name + ";";
            default: throw new Exception("Unknown model type " + mType);
        }
    }
    public static string GetTypeName(PropertyModel p, Datamodel datamodel) {
        if (p is IntegerPropertyModel intP && intP.IsEnum) {
            if (string.IsNullOrEmpty(intP.FullEnumTypeName))
                throw new Exception("Enum " + p.CodeName + " is missing FullEnumTypeName.");
            return intP.FullEnumTypeName;
        }
        return p.PropertyType switch {
            PropertyType.Boolean => "bool",
            PropertyType.Integer => "int",
            PropertyType.Double => "double",
            PropertyType.Float => "float",
            PropertyType.String => "string",
            PropertyType.StringArray => "string[]",
            PropertyType.Guid => "Guid",
            PropertyType.DateTime => "DateTime",
            PropertyType.DateTimeOffset => "DateTimeOffset",
            PropertyType.TimeSpan => "TimeSpan",
            PropertyType.Long => "long",
            PropertyType.ByteArray => "byte[]",
            PropertyType.FloatArray => "float[]",
            PropertyType.Decimal => "decimal",
            PropertyType.File => "Relatude.DB.Common.FileValue",
            PropertyType.Relation => getTypeNameRelationCollection(p, datamodel),
            _ => throw new NotSupportedException("The type " + p.PropertyType + " is not supported by the code generator."),
        };
    }
    static string getTypeNameRelationCollection(PropertyModel p, Datamodel dm) {
        if (p is not RelationPropertyModel rp) throw new Exception("PropertyModel " + p.ToString() + " is not a RelationPropertyModel.");
        var relation = dm.Relations[rp.RelationId];
        var nodeType = dm.FindFirstCommonBase(rp.FromTargetToSource ? relation.SourceTypes : relation.TargetTypes);
        if (rp.IsMany) {
            if (rp.RelationValueType == RelationValueType.Array) return nodeType + "[]";
            if (rp.RelationValueType == RelationValueType.List) return "List<" + nodeType + ">";
            if (rp.RelationValueType == RelationValueType.Collection) return "ICollection<" + nodeType + ">";
            if (rp.RelationValueType == RelationValueType.Enumerable) return "IEnumerable<" + nodeType + ">";
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
                        code += "." + nameof(OneOne<object>.One);
                        break;
                    case RelationType.OneToOne:
                        if (rp.FromTargetToSource) code += "." + nameof(OneToOne<object, object>.OneFrom);
                        else code += "." + nameof(OneToOne<object, object>.OneTo);
                        break;
                    case RelationType.OneToMany:
                        code += "." + nameof(OneToMany<object, object>.One);
                        break;
                    default:
                        throw new NotSupportedException("The relation type " + relation.RelationType + " is not supported by the code generator.");
                }
                return code;
            } else {
                return nodeType.ToString();
            }
        }
    }
    public static string GuidName(Guid g) => "g" + g.ToString().Replace("-", "");
    public static void Generate_CreateStaticGuids(StringBuilder sb, NodeTypeModel nodeDef, Datamodel datamodel) {
        sb.AppendLine("static Guid " + GuidName(nodeDef.Id) + " = Guid.Parse(\"" + nodeDef.Id + "\");");
        foreach (var p in nodeDef.AllProperties) {
            sb.AppendLine("static Guid " + GuidName(p.Key) + " = Guid.Parse(\"" + p.Key + "\");");
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
    static bool isFirstClassInParentsThatUseThisName(string propName, NodeTypeModel nodeDef, Datamodel datamodel, Func<NodeTypeModel, string> getPropName) {
        if (nodeDef.Parents.Count == 0) return true;
        foreach (var parentId in nodeDef.Parents) {
            var parent = datamodel.NodeTypes[parentId];
            if (getPropName(parent) == propName) return false;
            if (!isFirstClassInParentsThatUseThisName(propName, parent, datamodel, getPropName)) return false;
        }
        return true;
    }

}
