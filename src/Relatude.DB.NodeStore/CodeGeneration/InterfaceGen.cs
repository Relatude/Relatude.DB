using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Query;
using System.Text;
using Relatude.DB.Nodes;
using System.Xml.Linq;

namespace Relatude.DB.CodeGeneration;

internal static class InterfaceGen {
    public static List<(string className, string code)> GetImplementations(Datamodel datamodel) {
        return datamodel.NodeTypes.Values.Where(c => c.IsInterface && c.Id != NodeConstants.BaseNodeTypeId)
            .Select(c => (c.FullName + "InterfaceImplementation", getImplementationCode(c, datamodel))).ToList();
    }
    static string getImplementationCode(NodeTypeModel nodeDef, Datamodel datamodel) {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using System.Collections.Generic;");
        var nsp = nodeDef.Namespace ?? string.Empty;
        sb.AppendLine("namespace " + nsp + ";");

        sb.Append("public sealed class __" + nodeDef.CodeName + " :");

        var inheritance = nodeDef.FullName + " ," + typeof(INodeShellAccess).Namespace + "." + nameof(INodeShellAccess);
        sb.AppendLine(inheritance + " {");

        sb.AppendLine("public " + typeof(NodeDataShell).Namespace + "." + nameof(NodeDataShell) + " " + nameof(INodeShellAccess.__NodeDataShell) + " { get; }");


        var shellName = nameof(INodeShellAccess.__NodeDataShell);
        // constructor:
        sb.Append("public __" + nodeDef.CodeName + "(" + typeof(NodeDataShell).Namespace + "." + nameof(NodeDataShell) + " shell){");
        sb.AppendLine("    this." + shellName + " = shell;");
        sb.AppendLine("}");

        // system properties:
        if (!string.IsNullOrEmpty(nodeDef.NameOfPublicIdProperty) && CodeUtils.IsFirstClassUsingName_NameOfPublicIdProperty(nodeDef, datamodel)) {
            string typeName = nodeDef.DataTypeOfPublicId switch {
                DataTypePublicId.Guid => "Guid",
                DataTypePublicId.String => "string",
                _ => throw new Exception("Unknown datatype of public id: " + nodeDef.DataTypeOfPublicId),
            };
            sb.AppendLine("public " + typeName + " " + nodeDef.NameOfPublicIdProperty + "{ ");
            sb.AppendLine("get { return " + shellName + "." + nameof(NodeDataShell.NodeData) + "." + nameof(NodeDataShell.NodeData.Id) + "; } ");
            sb.AppendLine("set { " + shellName + "." + nameof(NodeDataShell.NodeData) + "." + nameof(NodeDataShell.NodeData.Id) + " = value; } ");
            sb.AppendLine(" }");
        }
        if (!string.IsNullOrEmpty(nodeDef.NameOfInternalIdProperty) && CodeUtils.IsFirstClassUsingName_NameOfInternalIdProperty(nodeDef, datamodel)) {
            sb.AppendLine("public int " + nodeDef.NameOfInternalIdProperty + "{ ");
            sb.AppendLine("get { return " + shellName + "." + nameof(NodeDataShell.NodeData) + "." + nameof(NodeDataShell.NodeData.__Id) + "; } ");
            sb.AppendLine("set { " + shellName + "." + nameof(NodeDataShell.NodeData) + "." + nameof(NodeDataShell.NodeData.__Id) + " = value; } ");
            sb.AppendLine(" }");
        }
        if (!string.IsNullOrEmpty(nodeDef.NameOfCreatedUtcProperty) && CodeUtils.IsFirstClassUsingName_NameOfCreatedUtcProperty(nodeDef, datamodel)) {
            sb.AppendLine("public DateTime " + nodeDef.NameOfCreatedUtcProperty + "{ ");
            sb.AppendLine("get { return " + shellName + "." + nameof(NodeDataShell.NodeData) + "." + nameof(NodeDataShell.NodeData.CreatedUtc) + "; } ");
            sb.AppendLine("set { " + shellName + "." + nameof(NodeDataShell.NodeData) + "." + nameof(NodeDataShell.NodeData.CreatedUtc) + " = value; } ");
            sb.AppendLine(" }");
        }
        if (!string.IsNullOrEmpty(nodeDef.NameOfChangedUtcProperty) && CodeUtils.IsFirstClassUsingName_NameOfChangedUtcProperty(nodeDef, datamodel)) {
            sb.AppendLine("public DateTime " + nodeDef.NameOfChangedUtcProperty + "{ ");
            sb.AppendLine("get { return " + shellName + "." + nameof(NodeDataShell.NodeData) + "." + nameof(NodeDataShell.NodeData.ChangedUtc) + "; } ");
            sb.AppendLine("set { " + shellName + "." + nameof(NodeDataShell.NodeData) + "." + nameof(NodeDataShell.NodeData.ChangedUtc) + " = value; } ");
            sb.AppendLine(" }");
        }

        // regular properties:
        CodeUtils.Generate_CreateStaticGuids(sb, nodeDef, datamodel);
        foreach (var p in nodeDef.Properties.Values.Where(p => !p.Private)) {
            var typeName = CodeUtils.GetTypeName(p, datamodel);
            var pIdName = CodeUtils.GuidName(p.Id);
            if (p.PropertyType == PropertyType.Relation) {
                if (p is not RelationPropertyModel rp) throw new Exception("Expected relation property model for property: " + p.CodeName);

                sb.AppendLine("" + typeName + " _" + p.CodeName + " = null;");
                sb.AppendLine("public " + typeName + " " + p.CodeName + "{ ");
                sb.AppendLine("set{ throw new Exception(\"Relations properties cannot be set. \"); }");
                sb.AppendLine("get{");
                sb.AppendLine("if(_" + p.CodeName + " == null) {");
                sb.AppendLine("var nodeData = this." + nameof(INodeShellAccess.__NodeDataShell) + "." + nameof(NodeDataShell.NodeData) + ";");
                sb.AppendLine("var store = this." + nameof(INodeShellAccess.__NodeDataShell) + "." + nameof(NodeDataShell.Store) + ";");
                sb.AppendLine("var relations = nodeData.Relations;");

                var relation = datamodel.Relations[rp.RelationId];
                var nodeType = datamodel.FindFirstCommonBase(rp.FromTargetToSource ? relation.SourceTypes : relation.TargetTypes);
                if (rp.RelationValueType == RelationValueType.Native) {
                    sb.AppendLine("_" + p.CodeName + " = new ();");
                    if (rp.IsMany) {
                        sb.AppendLine("if(relations." + nameof(IRelations.TryGetManyRelation) + "(" + CodeUtils.GuidName(p.Id) + ", out var v" + CodeUtils.GuidName(p.Id) + ")){");
                    } else {
                        sb.AppendLine(typeof(NodeDataWithRelations).Namespace + "." + nameof(NodeDataWithRelations) + " v" + CodeUtils.GuidName(p.Id) + " = null;");
                        sb.AppendLine("bool? v" + CodeUtils.GuidName(p.Id) + "_IsSet = false;");
                        sb.AppendLine("relations." + nameof(IRelations.LookUpOneRelation) + "(" + CodeUtils.GuidName(p.Id) + ", out var v" + CodeUtils.GuidName(p.Id) + "_Included, ref v" + CodeUtils.GuidName(p.Id) + ", ref v" + CodeUtils.GuidName(p.Id) + "_IsSet);");
                        sb.AppendLine("if(v" + CodeUtils.GuidName(p.Id) + "_Included){");
                    }
                    { // If included:
                        sb.Append("_" + p.CodeName + ".");
                        if (rp.IsMany) {
                            sb.Append(nameof(IManyProperty.Initialize));
                            var relVal = "(" + typeof(NodeDataWithRelations).Namespace + "." + nameof(NodeDataWithRelations) + "[])v" + CodeUtils.GuidName(p.Id);
                            sb.AppendLine("(store, nodeData." + nameof(INodeData.__Id) + ", nodeData." + nameof(INodeData.Id) + ", " + CodeUtils.GuidName(p.Id) + ", " + relVal + ");");
                        } else {
                            sb.Append(nameof(IOneProperty.Initialize));
                            var relVal = "(" + typeof(NodeDataWithRelations).Namespace + "." + nameof(NodeDataWithRelations) + ")v" + CodeUtils.GuidName(p.Id);
                            sb.AppendLine("(store, nodeData." + nameof(INodeData.__Id) + ", nodeData." + nameof(INodeData.Id) + ", " + CodeUtils.GuidName(p.Id) + ", " + relVal + ", true);"); // Fix: isSet is always true.... 
                        }
                        sb.AppendLine("");
                    }
                    sb.AppendLine("}else{");
                    { // If not included:
                        sb.Append("_" + p.CodeName + ".");
                        if (rp.IsMany) {
                            sb.Append(nameof(IManyProperty.Initialize));
                            sb.AppendLine("(store, nodeData." + nameof(INodeData.__Id) + ", nodeData." + nameof(INodeData.Id) + ", " + CodeUtils.GuidName(p.Id) + ", null);");
                        } else {
                            sb.Append(nameof(IOneProperty.Initialize));
                            sb.AppendLine("(store, nodeData." + nameof(INodeData.__Id) + ", nodeData." + nameof(INodeData.Id) + ", " + CodeUtils.GuidName(p.Id) + ", null, null);");
                        }
                        sb.AppendLine("");
                    }
                    sb.AppendLine("}");



                } else if (rp.IsMany) {  // but not native
                    sb.AppendLine("if(relations." + nameof(IRelations.TryGetManyRelation) + "(" + CodeUtils.GuidName(p.Id) + ", out var v" + CodeUtils.GuidName(p.Id) + ")) {");
                    sb.Append("_" + p.CodeName + " = ");
                    //var input = typeof(List<>).Namespace + ".List<" + typeof(NodeDataWithRelations).Namespace + "." + nameof(NodeDataWithRelations) + ">)v" + CodeUtils.guidName(p.Id);
                    var input = typeof(NodeDataWithRelations).Namespace + "." + nameof(NodeDataWithRelations) + "[])v" + CodeUtils.GuidName(p.Id);
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
                } else { // single relation, not native
                    sb.Append("if(relations." + nameof(IRelations.TryGetOneRelation) + "(" + CodeUtils.GuidName(p.Id) + ", out var v" + CodeUtils.GuidName(p.Id) + ") && v" + CodeUtils.GuidName(p.Id) + "!=null) ");
                    sb.Append("_" + p.CodeName + " = ");
                    sb.Append("store.Get<" + nodeType + ">((" + typeof(INodeData).Namespace + "." + nameof(INodeData) + ")v" + CodeUtils.GuidName(p.Id) + ");");
                }

                sb.AppendLine(" }");
                sb.AppendLine("return _" + p.CodeName + ";");
                sb.AppendLine(" }");
                sb.AppendLine(" }");
            } else {
                sb.AppendLine("public " + typeName + " " + p.CodeName + "{ ");
                sb.Append("get { return " + shellName + "." + nameof(NodeDataShell.GetValue) + "<" + typeName + ">(" + pIdName + ")");
                if (p.IsReferenceTypeAndMustCopy()) sb.Append(".Copy()");
                sb.AppendLine("; } ");

                sb.AppendLine("set { " + shellName + "." + nameof(NodeDataShell.SetValue) + "(" + pIdName + ",value); } ");
                sb.AppendLine(" }");
            }
        }

        sb.AppendLine(" }"); // end of interface

        return sb.ToString();
    }
}