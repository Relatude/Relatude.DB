using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Query;
using System.Text;
using Relatude.DB.Nodes;
using System.Xml.Linq;


namespace Relatude.DB.CodeGeneration;
internal static class CodeGeneratorForValueMappers {
    public static List<(string className, string code)> GenerateValueMappers(Datamodel datamodel) {
        return datamodel.NodeTypes.Values.Where(c => !c.IsInterface).Select(c => (c.FullName, getMapperSourceCode(c, datamodel))).ToList();
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
            PropertyType.Relation => GetTypeNameRelationCollection(p, datamodel),
            _ => throw new NotSupportedException("The type " + p.PropertyType + " is not supported by the code generator."),
        };
    }
    public static string GetTypeNameRelationCollection(PropertyModel p, Datamodel dm) {
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
                    case RelationType.OneToMany:
                        code += "." + nameof(OneToMany<object, object>.Right);
                        break;
                    case RelationType.ManyMany:
                        code += "." + nameof(ManyMany<object>.Many);
                        break;
                    case RelationType.ManyToMany:
                        if (rp.FromTargetToSource) code += "." + nameof(ManyToMany<object, object>.Left);
                        else code += "." + nameof(ManyToMany<object, object>.Right);
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
                        if (rp.FromTargetToSource) code += "." + nameof(OneToOne<object, object>.Left);
                        else code += "." + nameof(OneToOne<object, object>.Right);
                        break;
                    case RelationType.OneToMany:
                        code += "." + nameof(OneToMany<object, object>.Left);
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
    static string getMapperSourceCode(NodeTypeModel nodeDef, Datamodel datamodel) {
        // Using fully qualified names to reduce potential naming conflicts.
        // Making use of nameof() to make generation more resilient to refactoring.
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using System.Collections.Generic;");
        var nsp = nodeDef.Namespace ?? string.Empty;
        sb.AppendLine("namespace _" + nsp + ";");
        var attributeName = nameof(TypeGuidAttribute);
        attributeName = typeof(TypeGuidAttribute).Namespace + "." + attributeName.Remove(attributeName.Length - "Attribute".Length);
        sb.AppendLine("[" + attributeName + "(Guid = \"" + nodeDef.Id + "\")]");
        sb.Append("public sealed class _" + nodeDef.CodeName + " :");
        sb.AppendLine(typeof(IValueMapper).Namespace + "." + nameof(IValueMapper) + "{");
        generate_CreateStaticGuids(sb, nodeDef, datamodel);
        generate_CreateNodeDataFromObject(sb, nodeDef, datamodel);
        generate_NodeDataToObject(sb, nodeDef, datamodel);
        generate_TryGetId(sb, nodeDef, datamodel);
        sb.AppendLine("}"); // end class
        return sb.ToString();
    }
    static string guidName(Guid g) => "g" + g.ToString().Replace("-", "");
    static void generate_CreateStaticGuids(StringBuilder sb, NodeTypeModel nodeDef, Datamodel datamodel) {
        sb.AppendLine("static Guid " + guidName(nodeDef.Id) + " = Guid.Parse(\"" + nodeDef.Id + "\");");
        foreach (var p in nodeDef.AllProperties) {
            sb.AppendLine("static Guid " + guidName(p.Key) + " = Guid.Parse(\"" + p.Key + "\");");
        }
    }
    static void generate_CreateNodeDataFromObject(StringBuilder sb, NodeTypeModel nodeDef, Datamodel datamodel) {
        var nsp = nodeDef.Namespace ?? string.Empty;
        var classTypeName = string.IsNullOrEmpty(nsp) ? nodeDef.CodeName : nsp + "." + nodeDef.CodeName;
        sb.Append("public " + typeof(INodeData).Namespace + "." + nameof(INodeData) + " " + nameof(IValueMapper.CreateNodeDataFromObject) + "(object obj");
        sb.AppendLine(", " + typeof(RelatedCollection).Namespace + "." + nameof(RelatedCollection) + " related){");
        // sb.AppendLine("var values = new System.Collections.Generic.Dictionary<Guid, object>();");
        var noneRelProps = nodeDef.AllProperties.Values.Where(p => !p.Private && p.PropertyType != PropertyType.Relation);
        sb.AppendLine("var values = new " + typeof(Properties<>).FullName!.Replace("`1", "") + "<object>(" + noneRelProps.Count() + ");");
        sb.AppendLine("var node = (" + classTypeName + ")obj;");
        if (!string.IsNullOrEmpty(nodeDef.NameOfPublicIdProperty)) {
            sb.Append("Guid gid = ");
            switch (nodeDef.DataTypeOfPublicId) {
                case DataTypePublicId.Guid: sb.AppendLine("node." + nodeDef.NameOfPublicIdProperty + ";"); break;
                case DataTypePublicId.String: sb.AppendLine("string.IsNullOrEmpty(node." + nodeDef.NameOfPublicIdProperty + ") ? Guid.Empty: Guid.Parse(node." + nodeDef.NameOfPublicIdProperty + ");"); break;
                default: throw new Exception("Unknown datatype of public id: " + nodeDef.DataTypeOfPublicId);
            }
        } else {
            sb.AppendLine("Guid gid = Guid.Empty;");
        }
        if (!string.IsNullOrEmpty(nodeDef.NameOfInternalIdProperty)) {
            sb.Append("int uid = ");
            switch (nodeDef.DataTypeOfInternalId) {
                case DataTypeInternalId.UInt: sb.AppendLine("node." + nodeDef.NameOfInternalIdProperty + ";"); break;
                case DataTypeInternalId.Int: sb.AppendLine("(int)node." + nodeDef.NameOfInternalIdProperty + ";"); break;
                case DataTypeInternalId.Long: sb.AppendLine("(int)node." + nodeDef.NameOfInternalIdProperty + ";"); break;
                case DataTypeInternalId.String: sb.AppendLine("int.Parse(node." + nodeDef.NameOfInternalIdProperty + ");"); break;
                default: throw new Exception("Unknown datatype of public id: " + nodeDef.DataTypeOfPublicId);
            }
        } else {
            sb.AppendLine("int uid = 0;");
        }
        sb.AppendLine("if(uid == 0 && gid == Guid.Empty) gid = Guid.NewGuid();"); // ensure that at least one gid is set
        foreach (var p in noneRelProps) {
            if (p is IntegerPropertyModel intP && intP.IsEnum) { // if enum need cast to int
                sb.AppendLine("values.Add(" + guidName(p.Id) + ", (int)node." + p.CodeName + ");");
            } else {
                sb.AppendLine("values.Add(" + guidName(p.Id) + ", node." + p.CodeName + ");");
            }
        }

        void helper(string name, string? prop, string val) =>
            sb.AppendLine($"var {name} = {(string.IsNullOrEmpty(prop) ? val : $"node.{prop}")};");

        //helper("collectionId", nodeDef.NameOfCollectionProperty, "Guid.Empty");
        //helper("lcid", nodeDef.NameOfLCIDProperty, "0");
        //helper("derivedFromLCID", nodeDef.NameOfDerivedFromLCID, "0");
        //helper("readAccess", nodeDef.NameOfReadAccessProperty, "Guid.Empty");
        //helper("writeAccess", nodeDef.NameOfWriteAccessProperty, "Guid.Empty");

        helper("createdUtc", nodeDef.NameOfCreatedUtcProperty, "DateTime.MinValue");
        helper("changedUtc", nodeDef.NameOfChangedUtcProperty, "DateTime.UtcNow");

        sb.Append("var nodeData = new " + typeof(NodeData).Namespace + "." + nameof(NodeData) + "(");
        sb.Append("gid, uid, " + guidName(nodeDef.Id));
        //sb.Append(", collectionId, lcid, derivedFromLCID, readAccess, writeAccess, ");
        sb.Append(", createdUtc, changedUtc, values");
        sb.AppendLine(");");

        sb.AppendLine("if(related!=null){");
        foreach (var p in nodeDef.AllProperties.Values.Where(p => !p.Private && p is RelationPropertyModel relProp && relProp.RelationValueType != RelationValueType.Native)) {
            sb.AppendLine("if(node." + p.CodeName + " != null) related.Add(" + guidName(p.Id) + ", node, node." + p.CodeName + ");");
        }
        sb.AppendLine("}");

        sb.AppendLine("return nodeData;");
        sb.AppendLine("}");

    }
    static void generate_NodeDataToObject(StringBuilder sb, NodeTypeModel nodeDef, Datamodel dm) {
        var nsp = nodeDef.Namespace ?? string.Empty;
        var classTypeName = string.IsNullOrEmpty(nsp) ? nodeDef.CodeName : nsp + "." + nodeDef.CodeName;
        sb.Append("public object " + nameof(IValueMapper.NodeDataToObject) + "(");
        sb.Append(typeof(INodeData).Namespace + "." + nameof(INodeData) + " nodeData, ");
        sb.Append(typeof(NodeStore).Namespace + "." + nameof(NodeStore) + " store");
        sb.AppendLine("){");
        sb.AppendLine("var obj = new " + classTypeName + "();");
        sb.AppendLine("var relations = nodeData." + nameof(INodeData.Relations) + ";");
        if (!string.IsNullOrEmpty(nodeDef.NameOfPublicIdProperty)) {
            sb.Append("obj." + nodeDef.NameOfPublicIdProperty + " = ");
            switch (nodeDef.DataTypeOfPublicId) {
                case DataTypePublicId.Guid: sb.AppendLine("nodeData." + nameof(INodeData.Id) + ";"); break;
                case DataTypePublicId.String: sb.AppendLine("nodeData." + nameof(INodeData.Id) + ".ToString();"); break;
                default: throw new Exception("Unknown datatype of public id: " + nodeDef.DataTypeOfPublicId);
            }
        }
        if (!string.IsNullOrEmpty(nodeDef.NameOfInternalIdProperty)) {
            sb.Append("obj." + nodeDef.NameOfInternalIdProperty + " = ");
            switch (nodeDef.DataTypeOfInternalId) {
                case DataTypeInternalId.UInt: sb.AppendLine("nodeData." + nameof(INodeData.__Id) + ";"); break;
                case DataTypeInternalId.Int: sb.AppendLine("(int)nodeData." + nameof(INodeData.__Id) + ";"); break;
                case DataTypeInternalId.Long: sb.AppendLine("(long)nodeData." + nameof(INodeData.__Id) + ";"); break;
                case DataTypeInternalId.String: sb.AppendLine("nodeData." + nameof(INodeData.__Id) + ".ToString();"); break;
                default: throw new Exception("Unknown datatype of public id: " + nodeDef.DataTypeOfPublicId);
            }
        }
        void h1(string? name, string? prop) {
            if (!string.IsNullOrEmpty(name))
                sb.AppendLine("obj." + name + " = nodeData." + prop + ";");
        }
        //h1(nodeDef.NameOfCollectionProperty, nameof(INodeData.CollectionId));
        //h1(nodeDef.NameOfLCIDProperty, nameof(INodeData.LCID));
        //h1(nodeDef.NameOfDerivedFromLCID, nameof(INodeData.DerivedFromLCID));
        //h1(nodeDef.NameOfReadAccessProperty, nameof(INodeData.ReadAccess));
        //h1(nodeDef.NameOfWriteAccessProperty, nameof(INodeData.WriteAccess));
        h1(nodeDef.NameOfCreatedUtcProperty, nameof(INodeData.CreatedUtc));
        h1(nodeDef.NameOfChangedUtcProperty, nameof(INodeData.ChangedUtc));
        //if (!string.IsNullOrEmpty(nodeDef.NameOfCollectionProperty)) sb.Append("obj." + nodeDef.NameOfCollectionProperty + " = nodeData." + nameof(INodeData.CollectionId) + ";");
        foreach (var p in nodeDef.AllProperties.Values.Where(p => !p.Private)) {
            if (p.PropertyType == PropertyType.Relation) {
                if (p is not RelationPropertyModel rp) throw new Exception("PropertyModel " + p.ToString() + " is not a RelationPropertyModel.");
                var relation = dm.Relations[rp.RelationId];
                var nodeType = dm.FindFirstCommonBase(rp.FromTargetToSource ? relation.SourceTypes : relation.TargetTypes);
                if (rp.RelationValueType == RelationValueType.Native) {
                    if (rp.IsMany) {
                        sb.AppendLine("if(relations." + nameof(IRelations.TryGetManyRelation) + "(" + guidName(p.Id) + ", out var v" + guidName(p.Id) + ")){");
                    } else {
                        sb.AppendLine(typeof(NodeDataWithRelations).Namespace + "." + nameof(NodeDataWithRelations) + " v" + guidName(p.Id) + " = null;");
                        sb.AppendLine("bool? v" + guidName(p.Id) + "_IsSet = false;");
                        sb.AppendLine("relations." + nameof(IRelations.LookUpOneRelation) + "(" + guidName(p.Id) + ", out var v" + guidName(p.Id) + "_Included, ref v" + guidName(p.Id) + ", ref v" + guidName(p.Id) + "_IsSet);");
                        sb.AppendLine("if(v" + guidName(p.Id) + "_Included){");
                    }
                    { // If included:
                        sb.Append("obj." + p.CodeName + ".");
                        if (rp.IsMany) {
                            sb.Append(nameof(IManyProperty.Initialize));
                            var relVal = "(" + typeof(NodeDataWithRelations).Namespace + "." + nameof(NodeDataWithRelations) + "[])v" + guidName(p.Id);
                            sb.AppendLine("(store, nodeData." + nameof(INodeData.Id) + ", " + guidName(p.Id) + ", " + relVal + ");");
                        } else {
                            sb.Append(nameof(IOneProperty.Initialize));
                            var relVal = "(" + typeof(NodeDataWithRelations).Namespace + "." + nameof(NodeDataWithRelations) + ")v" + guidName(p.Id);
                            sb.AppendLine("(store, nodeData." + nameof(INodeData.Id) + ", " + guidName(p.Id) + ", " + relVal + ", true);"); // Fix: isSet is always true.... 
                        }
                        sb.AppendLine("");
                    }
                    //{ // If included:
                    //    sb.Append("obj." + p.CodeName + " = ");
                    //    sb.Append("new " + relation.FullName() + ".");
                    //    var pOne = "(" + typeof(NodeDataWithRelations).Namespace + "." + nameof(NodeDataWithRelations) + ")v" + guidName(p.Id);
                    //    var pMany = "(" + typeof(NodeDataWithRelations).Namespace + "." + nameof(NodeDataWithRelations) + "[])v" + guidName(p.Id);
                    //    var oneConstructorParams = "(store, nodeData." + nameof(INodeData.Id) + ", " + guidName(p.Id) + ", " + pOne + ", true);";  // Fix: isSet is always true.... 
                    //    var manyConstructorParams = "(store, nodeData." + nameof(INodeData.Id) + ", " + guidName(p.Id) + ", " + pMany + ");";
                    //    switch (relation.RelationType) {
                    //        case RelationType.OneOne:
                    //            sb.Append(nameof(OneOne<object>.One));
                    //            sb.AppendLine(oneConstructorParams);
                    //            break;
                    //        case RelationType.OneToOne:
                    //            if (rp.FromTargetToSource) sb.Append(nameof(OneToOne<object, object>.Left));
                    //            else sb.Append(nameof(OneToOne<object, object>.Right));
                    //            sb.AppendLine(oneConstructorParams);
                    //            break;
                    //        case RelationType.OneToMany:
                    //            if (rp.FromTargetToSource) {
                    //                sb.Append(nameof(OneToMany<object, object>.Left));
                    //                sb.AppendLine(oneConstructorParams);
                    //            } else {
                    //                sb.Append(nameof(OneToMany<object, object>.Right));
                    //                sb.AppendLine(manyConstructorParams);
                    //            }
                    //            break;
                    //        case RelationType.ManyMany:
                    //            sb.Append(nameof(ManyMany<object>.Many));
                    //            sb.AppendLine(manyConstructorParams);
                    //            break;
                    //        case RelationType.ManyToMany:
                    //            if (rp.FromTargetToSource) sb.Append(nameof(ManyToMany<object, object>.Left));
                    //            else sb.Append(nameof(ManyToMany<object, object>.Right));
                    //            sb.AppendLine(manyConstructorParams);
                    //            break;
                    //        default:
                    //            throw new NotSupportedException("Unknown relation type: " + relation.RelationType);
                    //    }
                    //    sb.AppendLine("");
                    //}

                    sb.AppendLine("}else{");
                    
                    { // If not included:
                        sb.Append("obj." + p.CodeName + ".");
                        if (rp.IsMany) {
                            sb.Append(nameof(IManyProperty.Initialize));
                            sb.AppendLine("(store, nodeData." + nameof(INodeData.Id) + ", " + guidName(p.Id) + ", null);");
                        } else {
                            sb.Append(nameof(IOneProperty.Initialize));
                            sb.AppendLine("(store, nodeData." + nameof(INodeData.Id) + ", " + guidName(p.Id) + ", null, null);");
                        }
                        sb.AppendLine("");
                    }
                    //{ // If not included:
                    //    sb.Append("obj." + p.CodeName + " = ");
                    //    sb.Append("new " + relation.FullName() + ".");
                    //    var oneConstructorParams = "(store, nodeData." + nameof(INodeData.Id) + ", " + guidName(p.Id) + ", null, null);";
                    //    var manyConstructorParams = "(store, nodeData." + nameof(INodeData.Id) + ", " + guidName(p.Id) + ", null);";
                    //    switch (relation.RelationType) {
                    //        case RelationType.OneOne:
                    //            sb.Append(nameof(OneOne<object>.One));
                    //            sb.AppendLine(oneConstructorParams);
                    //            break;
                    //        case RelationType.OneToOne:
                    //            if (rp.FromTargetToSource) sb.Append(nameof(OneToOne<object, object>.Left));
                    //            else sb.Append(nameof(OneToOne<object, object>.Right));
                    //            sb.AppendLine(oneConstructorParams);
                    //            break;
                    //        case RelationType.OneToMany:
                    //            if (rp.FromTargetToSource) {
                    //                sb.Append(nameof(OneToMany<object, object>.Left));
                    //                sb.AppendLine(oneConstructorParams);
                    //            } else {
                    //                sb.Append(nameof(OneToMany<object, object>.Right));
                    //                sb.AppendLine(manyConstructorParams);
                    //            }
                    //            break;
                    //        case RelationType.ManyMany:
                    //            sb.Append(nameof(ManyMany<object>.Many));
                    //            sb.AppendLine(manyConstructorParams);
                    //            break;
                    //        case RelationType.ManyToMany:
                    //            if (rp.FromTargetToSource) sb.Append(nameof(ManyToMany<object, object>.Left));
                    //            else sb.Append(nameof(ManyToMany<object, object>.Right));
                    //            sb.AppendLine(manyConstructorParams);
                    //            break;
                    //        default:
                    //            break;
                    //    }
                    //}
                    sb.AppendLine("}");

                    //throw new NotSupportedException("Native collections should not be called by this function.");

                } else if (rp.IsMany) {
                    sb.AppendLine("if(relations." + nameof(IRelations.TryGetManyRelation) + "(" + guidName(p.Id) + ", out var v" + guidName(p.Id) + ")) {");
                    sb.Append("obj." + p.CodeName + " = ");
                    //var input = typeof(List<>).Namespace + ".List<" + typeof(NodeDataWithRelations).Namespace + "." + nameof(NodeDataWithRelations) + ">)v" + guidName(p.Id);
                    var input = typeof(NodeDataWithRelations).Namespace + "." + nameof(NodeDataWithRelations) + "[])v" + guidName(p.Id);
                    sb.Append("store." + nameof(NodeStore.GetRelated) + "<" + nodeType + ">(");
                    if (rp.RelationValueType == RelationValueType.Array) {
                        sb.Append("(" + input + ").ToArray();");
                    } else if (rp.RelationValueType == RelationValueType.List) {
                        sb.Append("(" + input + ").ToList();");
                    } else if (rp.RelationValueType == RelationValueType.Collection) {
                        sb.Append("(" + input + ").ToList();"); // special collection could be added here for better performance.... using Ienumerable but still with count....
                    } else {
                        sb.Append("(" + input + ");"); // IEnumerable
                    }
                    sb.AppendLine("");
                    sb.AppendLine("}");
                } else {
                    sb.Append("if(relations." + nameof(IRelations.TryGetOneRelation) + "(" + guidName(p.Id) + ", out var v" + guidName(p.Id) + ") && v" + guidName(p.Id) + "!=null) ");
                    sb.Append("obj." + p.CodeName + " = ");
                    sb.Append("store.Get<" + nodeType + ">((" + typeof(INodeData).Namespace + "." + nameof(INodeData) + ")v" + guidName(p.Id) + ");");
                }
            } else {
                sb.Append("if(nodeData." + nameof(INodeData.TryGetValue) + "(" + guidName(p.Id) + ", out var v" + guidName(p.Id) + ")) obj." + p.CodeName + " = ((" + GetTypeName(p, dm) + ")v" + guidName(p.Id) + ")");
                if (p.IsReferenceTypeAndMustCopy()) sb.Append(".Copy()");
                sb.AppendLine(";");
                sb.Append("else obj." + p.CodeName + " = " + p.GetDefaultValueAsCode() + ";");
            }
        }
        sb.AppendLine("return obj;");
        sb.AppendLine("}"); // end method
    }
    static void generate_TryGetId(StringBuilder sb, NodeTypeModel nodeDef, Datamodel datamodel) {
        var nsp = nodeDef.Namespace ?? string.Empty;
        var classTypeName = string.IsNullOrEmpty(nsp) ? nodeDef.CodeName : nsp + "." + nodeDef.CodeName;

        sb.AppendLine("public bool " + nameof(IValueMapper.TryGetIdGuidAndCreateIfPossible) + "(object obj, out Guid id){");
        sb.AppendLine("var node = (" + classTypeName + ")obj;");
        if (!string.IsNullOrEmpty(nodeDef.NameOfPublicIdProperty)) {
            sb.Append("id = ");
            switch (nodeDef.DataTypeOfPublicId) {
                case DataTypePublicId.Guid: sb.AppendLine("node." + nodeDef.NameOfPublicIdProperty + ";"); break;
                case DataTypePublicId.String: sb.AppendLine("string.IsNullOrEmpty(node." + nodeDef.NameOfPublicIdProperty + ") ? Guid.Empty: Guid.Parse(node." + nodeDef.NameOfPublicIdProperty + ");"); break;
                default: throw new Exception("Unknown datatype of public id: " + nodeDef.DataTypeOfPublicId);
            }
            sb.AppendLine("if(id == Guid.Empty){");
            sb.AppendLine("  id = Guid.NewGuid();");
            switch (nodeDef.DataTypeOfPublicId) {
                case DataTypePublicId.Guid: sb.AppendLine("  node." + nodeDef.NameOfPublicIdProperty + " = id;"); break;
                case DataTypePublicId.String: sb.AppendLine("  node." + nodeDef.NameOfPublicIdProperty + " = id.ToString();"); break;
                default: throw new Exception("Unknown datatype of public id: " + nodeDef.DataTypeOfPublicId);
            }
            sb.AppendLine("}");
            sb.AppendLine("return true;");
        } else {
            sb.AppendLine("id = Guid.Empty;");
            sb.AppendLine("return false;");
        }
        sb.AppendLine("}");

        sb.AppendLine("public bool " + nameof(IValueMapper.TryGetIdGuid) + "(object obj, out Guid id){");
        sb.AppendLine("var node = (" + classTypeName + ")obj;");
        if (!string.IsNullOrEmpty(nodeDef.NameOfPublicIdProperty)) {
            sb.Append("id = ");
            switch (nodeDef.DataTypeOfPublicId) {
                case DataTypePublicId.Guid: sb.AppendLine("node." + nodeDef.NameOfPublicIdProperty + ";"); break;
                case DataTypePublicId.String: sb.AppendLine("string.IsNullOrEmpty(node." + nodeDef.NameOfPublicIdProperty + ") ? Guid.Empty: Guid.Parse(node." + nodeDef.NameOfPublicIdProperty + ");"); break;
                default: throw new Exception("Unknown datatype of public id: " + nodeDef.DataTypeOfPublicId);
            }
            sb.AppendLine("if(id == Guid.Empty) return false;");
            sb.AppendLine("return true;");
        } else {
            sb.AppendLine("id = Guid.Empty;");
            sb.AppendLine("return false;");
        }
        sb.AppendLine("}");

        sb.AppendLine("public bool " + nameof(IValueMapper.TryGetIdUInt) + "(object obj, out int id){");
        sb.AppendLine("var node = (" + classTypeName + ")obj;");
        if (!string.IsNullOrEmpty(nodeDef.NameOfInternalIdProperty)) {
            sb.Append("id = ");
            switch (nodeDef.DataTypeOfInternalId) {
                case DataTypeInternalId.UInt: sb.AppendLine("node." + nodeDef.NameOfInternalIdProperty + ";"); break;
                case DataTypeInternalId.Int: sb.AppendLine("(int)node." + nodeDef.NameOfInternalIdProperty + ";"); break;
                case DataTypeInternalId.Long: sb.AppendLine("(int)node." + nodeDef.NameOfInternalIdProperty + ";"); break;
                case DataTypeInternalId.String: sb.AppendLine("int.Parse(node." + nodeDef.NameOfInternalIdProperty + ");"); break;
                default: throw new Exception("Unknown datatype of public id: " + nodeDef.DataTypeOfPublicId);
            }
            sb.AppendLine("return true;");
        } else {
            sb.AppendLine("id = 0;");
            sb.AppendLine("return false;");
        }
        sb.AppendLine("}");


    }
}