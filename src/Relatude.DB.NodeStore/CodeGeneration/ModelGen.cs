using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Nodes;

namespace Relatude.DB.CodeGeneration;

public static class ModelGen {
    public static string GenerateCSharpModelCode(Datamodel datamodel, bool addAttributes = true)
        => GenerateCSharpModelCode(datamodel, addAttributes, null, null);
    /// <summary>
    /// Generates model code for the node types and relations the filters accept, all of them when a filter
    /// is null. The filters let tooling write one file per type, or leave the native model out of the
    /// generated code. Types that are filtered out are still resolved as parents, property types and
    /// relation endpoints, so the emitted code refers to them by their full name.
    /// </summary>
    public static string GenerateCSharpModelCode(Datamodel datamodel, bool addAttributes,
            Func<NodeTypeModel, bool>? includeNodeType, Func<RelationModel, bool>? includeRelation) {
        var sb = new StringBuilder();
        datamodel.EnsureInitalization();
        sb.AppendLine("using " + typeof(object).Namespace + ";");
        sb.AppendLine("using System.Collections.Generic;"); // List<T>, IEnumerable<T> etc. in generated declarations
        sb.AppendLine("");
        var nodeTypesByNamespace = datamodel.NodeTypes.Values
            .Where(n => n.Id != NodeConstants.BaseNodeTypeId)
            .Where(n => includeNodeType == null || includeNodeType(n))
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
            .Where(r => includeRelation == null || includeRelation(r))
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
    static void appendModelCode(NodeTypeModel nodeDef, Datamodel datamodel, StringBuilder sb, bool addAttributes) {
        if (addAttributes) {
            sb.Append("    [" + nameAtt<NodeAttribute>() + "(" + nameof(NodeAttribute.Id) + " = \"" + nodeDef.Id + "\"");
            if (nodeDef.TextIndex.HasValue) sb.Append(", " + nameof(NodeAttribute.TextIndex) + " = " + addAttributeBool(nodeDef.TextIndex.Value ? BoolValue.True : BoolValue.False));
            if (nodeDef.SemanticIndex.HasValue) sb.Append(", " + nameof(NodeAttribute.SemanticIndex) + " = " + addAttributeBool(nodeDef.SemanticIndex.Value ? BoolValue.True : BoolValue.False));
            if (nodeDef.InstantTextIndexing.HasValue) sb.Append(", " + nameof(NodeAttribute.InstantTextIndexing) + " = " + addAttributeBool(nodeDef.InstantTextIndexing.Value ? BoolValue.True : BoolValue.False));
            if (nodeDef.TextIndexBoost != 0) sb.Append(", " + nameof(NodeAttribute.TextIndexBoost) + " = " + nodeDef.TextIndexBoost.ToString(CultureInfo.InvariantCulture));
            // instance limits are only written when set: the attribute and the model disagree on what
            // "unset" is (0 and int.MinValue), and the model builder treats an unset attribute as no limit
            if (nodeDef.MinNoInstances != int.MinValue && nodeDef.MinNoInstances != 0) sb.Append(", " + nameof(NodeAttribute.MinNoInstances) + " = " + nodeDef.MinNoInstances.ToString(CultureInfo.InvariantCulture));
            if (nodeDef.MaxNoInstances != int.MaxValue) sb.Append(", " + nameof(NodeAttribute.MaxNoInstances) + " = " + nodeDef.MaxNoInstances.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine(")]");
        }
        var inheritance = string.Join(", ", nodeDef.Parents
            .Where(id => id != NodeConstants.BaseNodeTypeId)
            .Select(id => typeAndNamespace(nodeDef.Namespace, datamodel.NodeTypes[id].FullName)));
        if (!string.IsNullOrEmpty(inheritance)) inheritance = " : " + inheritance;
        sb.AppendLine("    public " + nodeDef.ModelType.ToString().ToLower() + " " + nodeDef.CodeName + inheritance + " {");
        if (!string.IsNullOrEmpty(nodeDef.NameOfPublicIdProperty) && CodeUtils.IsFirstClassUsingName_NameOfPublicIdProperty(nodeDef, datamodel)) {
            if (addAttributes) sb.AppendLine("        [" + nameAtt<PublicIdPropertyAttribute>() + "()]");
            string typeName = nodeDef.DataTypeOfPublicId switch {
                DataTypePublicId.Guid => "Guid",
                DataTypePublicId.String => "string",
                _ => throw new Exception("Unknown datatype of public id: " + nodeDef.DataTypeOfPublicId),
            };
            sb.Append("        ");
            sb.AppendLine(CodeUtils.FieldOrProperty(typeName, nodeDef.NameOfPublicIdProperty, nodeDef.ModelType));
        }
        if (!string.IsNullOrEmpty(nodeDef.NameOfInternalIdProperty) && CodeUtils.IsFirstClassUsingName_NameOfInternalIdProperty(nodeDef, datamodel)) {
            if (addAttributes) sb.AppendLine("        [" + nameAtt<InternalIdPropertyAttribute>() + "()]");
            sb.Append("        ");
            sb.AppendLine(CodeUtils.FieldOrProperty(nodeDef.DataTypeOfInternalId?.ToString().ToLower() + "", nodeDef.NameOfInternalIdProperty, nodeDef.ModelType));
        }
        if (!string.IsNullOrEmpty(nodeDef.NameOfCreatedUtcProperty) && CodeUtils.IsFirstClassUsingName_NameOfCreatedUtcProperty(nodeDef, datamodel)) {
            if (addAttributes) sb.AppendLine("        [" + nameAtt<CreatedUtcPropertyAttribute>() + "()]");
            sb.Append("        ");
            sb.AppendLine(CodeUtils.FieldOrProperty("DateTime", nodeDef.NameOfCreatedUtcProperty, nodeDef.ModelType));
        }
        if (!string.IsNullOrEmpty(nodeDef.NameOfChangedUtcProperty) && CodeUtils.IsFirstClassUsingName_NameOfChangedUtcProperty(nodeDef, datamodel)) {
            if (addAttributes) sb.AppendLine("        [" + nameAtt<ChangedUtcPropertyAttribute>() + "()]");
            sb.Append("        ");
            sb.AppendLine(CodeUtils.FieldOrProperty("DateTime", nodeDef.NameOfChangedUtcProperty, nodeDef.ModelType));
        }
        if (!string.IsNullOrEmpty(nodeDef.NameOfDisplayNameProperty) && CodeUtils.IsFirstClassUsingName_NameOfDisplayNameProperty(nodeDef, datamodel)) {
            if (addAttributes) sb.AppendLine("        [" + nameAtt<DisplayNamePropertyAttribute>() + "()]");
            sb.Append("        ");
            sb.AppendLine(CodeUtils.FieldOrProperty("string", nodeDef.NameOfDisplayNameProperty, nodeDef.ModelType));
        }
        if (!string.IsNullOrEmpty(nodeDef.NameOfAddressProperty) && CodeUtils.IsFirstClassUsingName_NameOfAddressProperty(nodeDef, datamodel)) {
            if (addAttributes) sb.AppendLine("        [" + nameAtt<AddressPropertyAttribute>() + "()]");
            sb.Append("        ");
            sb.AppendLine(CodeUtils.FieldOrProperty("string", nodeDef.NameOfAddressProperty, nodeDef.ModelType));
        }
        if (!string.IsNullOrEmpty(nodeDef.NameOfMetaProperty) && CodeUtils.IsFirstClassUsingName_NameOfMetaProperty(nodeDef, datamodel)) {
            // any member of type NodeMeta becomes the meta property, no attribute needed
            sb.Append("        ");
            sb.AppendLine(CodeUtils.FieldOrProperty(typeof(NodeMeta).FullName!, nodeDef.NameOfMetaProperty, nodeDef.ModelType));
        }
        foreach (var p in nodeDef.Properties.Values.Where(p => !p.Internal)) {
            if (addAttributes) addPropertyAttribute(p, datamodel, sb);
            var typeName = CodeUtils.GetTypeName(p, datamodel);
            typeName = typeAndNamespace(nodeDef.Namespace, typeName);
            // embedded (list) properties must be getter-only, the model builder rejects a setter
            var getterOnly = p is EmbeddedPropertyModel ep && ep.EmbeddedValueType == EmbeddedValueType.InnerNodeList;
            sb.Append("        ");
            sb.AppendLine(CodeUtils.FieldOrProperty(typeName, p.CodeName, nodeDef.ModelType, CodeUtils.getDefaultDeclaration(nodeDef.Namespace, p, datamodel), getterOnly));
        }
        // a class or record implementing a model interface must physically implement the members
        // that the interface's model owns. They are emitted without attributes: the model builder
        // assigns a member to its first declaring interface and ignores the class side.
        if (nodeDef.ModelType == ModelType.Class || nodeDef.ModelType == ModelType.Record) {
            var classParents = nodeDef.Parents.Select(id => datamodel.NodeTypes[id]).Where(t => !t.IsInterface).ToList();
            var needsImplementation = nodeDef.AllProperties.Values
                .Where(p => !p.Internal)
                .Where(p => !nodeDef.Properties.ContainsKey(p.Id))
                .Where(p => !classParents.Any(cp => cp.AllProperties.ContainsKey(p.Id))); // class parents already implement these
            foreach (var p in needsImplementation) {
                var typeName = typeAndNamespace(nodeDef.Namespace, CodeUtils.GetTypeName(p, datamodel));
                var getterOnly = p is EmbeddedPropertyModel ep && ep.EmbeddedValueType == EmbeddedValueType.InnerNodeList;
                sb.Append("        ");
                sb.AppendLine(CodeUtils.FieldOrProperty(typeName, p.CodeName, nodeDef.ModelType, CodeUtils.getDefaultDeclaration(nodeDef.Namespace, p, datamodel), getterOnly));
            }
        }
        sb.AppendLine("    }"); // end class
    }
    static void addBaseAttributes<T>(PropertyModel p, Datamodel dm, StringBuilder sb, string? attributeName = null) where T : PropertyAttribute {
        if (attributeName == null) attributeName = nameAtt<T>();
        sb.Append("        [" + attributeName + "(");
        sb.Append(nameof(PropertyAttribute.Id) + " = \"" + p.Id + "\"");
        if (p.ExcludeFromTextIndex) sb.Append(", " + nameof(PropertyAttribute.ExcludeFromTextIndex) + " = true");
        if (p.DisplayName) sb.Append(", " + nameof(PropertyAttribute.DisplayName) + " = true");
        if (p.IndexBoost != 0) sb.Append(", " + nameof(PropertyAttribute.TextIndexBoost) + " = " + p.IndexBoost);
        if (p.ReadAccess != Guid.Empty) sb.Append(", " + nameof(PropertyAttribute.ReadAccess) + " = \"" + p.ReadAccess + "\"");
        if (p.WriteAccess != Guid.Empty) sb.Append(", " + nameof(PropertyAttribute.WriteAccess) + " = \"" + p.WriteAccess + "\"");
    }
    static string addAttributeBool(BoolValue b) => typeof(BoolValue).Namespace + "." + nameof(BoolValue) + "." + b.ToString();
    static string stringArrayArg(IEnumerable<string> values) => "new string[] {" + string.Join(", ", values) + "}"; // attribute arguments cannot use collection expressions
    // user supplied strings must be escaped to form valid C# string literals (includes the quotes):
    static string stringLiteral(string value) => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, quote: true);
    static string nameAtt<T>() {
        var t = typeof(T);
        var s = t.Namespace + "." + t.Name;
        return s.Remove(s.Length - "Attribute".Length);
    }
    static void addPropertyAttribute(PropertyModel p, Datamodel dm, StringBuilder sb) {
        var nodeType = dm.NodeTypes[p.NodeType];
        if (nodeType.NameOfChangedUtcProperty == p.CodeName) sb.AppendLine("        [" + nameAtt<ChangedUtcPropertyAttribute>() + "()]");
        if (nodeType.NameOfCreatedUtcProperty == p.CodeName) sb.AppendLine("        [" + nameAtt<CreatedUtcPropertyAttribute>() + "()]");
        if (nodeType.NameOfDisplayNameProperty == p.CodeName) sb.AppendLine("        [" + nameAtt<DisplayNamePropertyAttribute>() + "()]");
        if (nodeType.NameOfAddressProperty == p.CodeName) sb.AppendLine("        [" + nameAtt<AddressPropertyAttribute>() + "()]");
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
                    if (b.Indexed) sb.Append(", " + nameof(BooleanPropertyAttribute.Indexed) + " = true"); // the attribute member is a plain bool
                    if (b.NotFacet) sb.Append(", " + nameof(BooleanPropertyAttribute.NotFacet) + " = true");
                    if (b.DefaultValue) sb.Append(", " + nameof(BooleanPropertyAttribute.DefaultValue) + " = true");
                    sb.AppendLine(")]");
                }
                break;
            case PropertyType.Guid: {
                    addBaseAttributes<GuidPropertyAttribute>(p, dm, sb);
                    var b = (GuidPropertyModel)p;
                    if (b.Indexed) sb.Append(", " + nameof(GuidPropertyAttribute.Indexed) + " = true"); // the attribute member is a plain bool
                    if (b.DefaultValue != Guid.Empty) sb.Append(", " + nameof(GuidPropertyAttribute.DefaultValue) + " = \"" + b.DefaultValue + "\"");
                    if (b.UniqueValues) sb.Append(", " + nameof(GuidPropertyAttribute.UniqueValues) + " = true");
                    sb.AppendLine(")]");
                }
                break;
            case PropertyType.Integer: {
                    addBaseAttributes<IntegerPropertyAttribute>(p, dm, sb);
                    var i = (IntegerPropertyModel)p;
                    if (i.Indexed) sb.Append(", " + nameof(IntegerPropertyAttribute.Indexed) + " = true"); // the attribute member is a plain bool
                    if (i.NotFacet) sb.Append(", " + nameof(IntegerPropertyAttribute.NotFacet) + " = true");
                    if (i.DefaultValue != 0) sb.Append(", " + nameof(IntegerPropertyAttribute.DefaultValue) + " = " + i.DefaultValue);
                    if (i.MinValue != int.MinValue) sb.Append(", " + nameof(IntegerPropertyAttribute.MinValue) + " = " + i.MinValue);
                    if (i.MaxValue != int.MaxValue) sb.Append(", " + nameof(IntegerPropertyAttribute.MaxValue) + " = " + i.MaxValue);
                    if (i.UniqueValues) sb.Append(", " + nameof(IntegerPropertyAttribute.UniqueValues) + " = true");
                    if (i.IsEnum) {
                        sb.Append(", " + nameof(IntegerPropertyAttribute.IsEnum) + " = true");
                        if (!string.IsNullOrEmpty(i.FullEnumTypeName)) sb.Append(", " + nameof(IntegerPropertyAttribute.FullEnumTypeName) + " = \"" + i.FullEnumTypeName + "\"");
                    }
                    if (i.LegalValues != null) {
                        var legalValues = i.LegalValues.Select(v => v.ToString());
                        sb.Append(", " + nameof(IntegerPropertyAttribute.LegalValues) + " = new int[] {" + string.Join(", ", legalValues) + "}");
                    }
                    if (i.LegalValueNames != null) sb.Append(", " + nameof(IntegerPropertyAttribute.LegalValueNames) + " = " + stringArrayArg(i.LegalValueNames.Select(stringLiteral)));
                    if (i.FacetRangePowerBase != 0) sb.Append(", " + nameof(IntegerPropertyAttribute.FacetRangePowerBase) + " = " + i.FacetRangePowerBase.ToString(CultureInfo.InvariantCulture));
                    if (i.FacetRangeCount != 0) sb.Append(", " + nameof(IntegerPropertyAttribute.FacetRangeCount) + " = " + i.FacetRangeCount);
                    sb.AppendLine(")]");
                }
                break;
            case PropertyType.Long: {
                    addBaseAttributes<LongPropertyAttribute>(p, dm, sb);
                    var i = (LongPropertyModel)p;
                    if (i.Indexed) sb.Append(", " + nameof(LongPropertyAttribute.Indexed) + " = true"); // the attribute member is a plain bool
                    if (i.NotFacet) sb.Append(", " + nameof(LongPropertyAttribute.NotFacet) + " = true");
                    if (i.DefaultValue != 0) sb.Append(", " + nameof(LongPropertyAttribute.DefaultValue) + " = " + i.DefaultValue.ToString(CultureInfo.InvariantCulture));
                    if (i.MinValue != long.MinValue) sb.Append(", " + nameof(LongPropertyAttribute.MinValue) + " = " + i.MinValue.ToString(CultureInfo.InvariantCulture));
                    if (i.MaxValue != long.MaxValue) sb.Append(", " + nameof(LongPropertyAttribute.MaxValue) + " = " + i.MaxValue.ToString(CultureInfo.InvariantCulture));
                    if (i.UniqueValues) sb.Append(", " + nameof(LongPropertyAttribute.UniqueValues) + " = true");
                    if (i.FacetRangePowerBase != 0) sb.Append(", " + nameof(LongPropertyAttribute.FacetRangePowerBase) + " = " + i.FacetRangePowerBase.ToString(CultureInfo.InvariantCulture));
                    if (i.FacetRangeCount != 0) sb.Append(", " + nameof(LongPropertyAttribute.FacetRangeCount) + " = " + i.FacetRangeCount);
                    sb.AppendLine(")]");
                }
                break;
            case PropertyType.Double: {
                    addBaseAttributes<DoublePropertyAttribute>(p, dm, sb);
                    var i = (DoublePropertyModel)p;
                    if (i.Indexed) sb.Append(", " + nameof(DoublePropertyAttribute.Indexed) + " = true"); // the attribute member is a plain bool
                    if (i.NotFacet) sb.Append(", " + nameof(DoublePropertyAttribute.NotFacet) + " = true");
                    if (i.DefaultValue != 0) sb.Append(", " + nameof(DoublePropertyAttribute.DefaultValue) + " = " + i.DefaultValue.ToString(CultureInfo.InvariantCulture));
                    if (i.MinValue != double.MinValue) sb.Append(", " + nameof(DoublePropertyAttribute.MinValue) + " = " + i.MinValue.ToString(CultureInfo.InvariantCulture));
                    if (i.MaxValue != double.MaxValue) sb.Append(", " + nameof(DoublePropertyAttribute.MaxValue) + " = " + i.MaxValue.ToString(CultureInfo.InvariantCulture));
                    if (i.FacetRangePowerBase != 0) sb.Append(", " + nameof(DoublePropertyAttribute.FacetRangePowerBase) + " = " + i.FacetRangePowerBase.ToString(CultureInfo.InvariantCulture));
                    if (i.FacetRangeCount != 0) sb.Append(", " + nameof(DoublePropertyAttribute.FacetRangeCount) + " = " + i.FacetRangeCount);
                    sb.AppendLine(")]");
                }
                break;
            case PropertyType.DateTime: {
                    addBaseAttributes<DateTimePropertyAttribute>(p, dm, sb);
                    var d = (DateTimePropertyModel)p;
                    if (d.Indexed) sb.Append(", " + nameof(DateTimePropertyAttribute.Indexed) + " = true"); // the attribute member is a plain bool
                    if (d.NotFacet) sb.Append(", " + nameof(DateTimePropertyAttribute.NotFacet) + " = true");
                    if (d.DefaultValue != DateTime.MinValue) sb.Append(", " + nameof(DateTimePropertyAttribute.DefaultValue) + " = \"" + d.DefaultValue.ToString("O") + "\"");
                    if (d.MinValue != DateTime.MinValue) sb.Append(", " + nameof(DateTimePropertyAttribute.MinValue) + " = \"" + d.MinValue.ToString("O") + "\"");
                    if (d.MaxValue != DateTime.MaxValue) sb.Append(", " + nameof(DateTimePropertyAttribute.MaxValue) + " = \"" + d.MaxValue.ToString("O") + "\"");
                    if (d.UniqueValues) sb.Append(", " + nameof(DateTimePropertyAttribute.UniqueValues) + " = true");
                    if (d.FacetRangePowerBase != 0) sb.Append(", " + nameof(DateTimePropertyAttribute.FacetRangePowerBase) + " = " + d.FacetRangePowerBase.ToString(CultureInfo.InvariantCulture));
                    if (d.FacetRangeCount != 0) sb.Append(", " + nameof(DateTimePropertyAttribute.FacetRangeCount) + " = " + d.FacetRangeCount);
                    sb.AppendLine(")]");
                }
                break;
            case PropertyType.DateTimeOffset: {
                    addBaseAttributes<DateTimeOffsetPropertyAttribute>(p, dm, sb);
                    var d = (DateTimeOffsetPropertyModel)p;
                    if (d.Indexed) sb.Append(", " + nameof(DateTimeOffsetPropertyAttribute.Indexed) + " = true"); // the attribute member is a plain bool
                    if (d.NotFacet) sb.Append(", " + nameof(DateTimeOffsetPropertyAttribute.NotFacet) + " = true");
                    if (d.DefaultValue != DateTimeOffset.MinValue) sb.Append(", " + nameof(DateTimeOffsetPropertyAttribute.DefaultValue) + " = \"" + d.DefaultValue.ToString("O") + "\"");
                    if (d.MinValue != DateTimeOffset.MinValue) sb.Append(", " + nameof(DateTimeOffsetPropertyAttribute.MinValue) + " = \"" + d.MinValue.ToString("O") + "\"");
                    if (d.MaxValue != DateTimeOffset.MaxValue) sb.Append(", " + nameof(DateTimeOffsetPropertyAttribute.MaxValue) + " = \"" + d.MaxValue.ToString("O") + "\"");
                    if (d.UniqueValues) sb.Append(", " + nameof(DateTimeOffsetPropertyAttribute.UniqueValues) + " = true");
                    if (d.FacetRangePowerBase != 0) sb.Append(", " + nameof(DateTimeOffsetPropertyAttribute.FacetRangePowerBase) + " = " + d.FacetRangePowerBase.ToString(CultureInfo.InvariantCulture));
                    if (d.FacetRangeCount != 0) sb.Append(", " + nameof(DateTimeOffsetPropertyAttribute.FacetRangeCount) + " = " + d.FacetRangeCount);
                    sb.AppendLine(")]");
                }
                break;
            case PropertyType.GeoCoordinate: {
                    addBaseAttributes<GeoCoordinatePropertyAttribute>(p, dm, sb);
                    var g = (GeoCoordinatePropertyModel)p;
                    if (g.Indexed) sb.Append(", " + nameof(GeoCoordinatePropertyAttribute.Indexed) + " = true"); // the attribute member is a plain bool
                    sb.AppendLine(")]");
                }
                break;
            case PropertyType.Decimal: {
                    addBaseAttributes<DecimalPropertyAttribute>(p, dm, sb);
                    var i = (DecimalPropertyModel)p;
                    if (i.Indexed) sb.Append(", " + nameof(DecimalPropertyAttribute.Indexed) + " = true"); // the attribute member is a plain bool
                    if (i.NotFacet) sb.Append(", " + nameof(DecimalPropertyAttribute.NotFacet) + " = true");
                    if (i.DefaultValue != 0) sb.Append(", " + nameof(DecimalPropertyAttribute.DefaultValue) + " = \"" + i.DefaultValue.ToString(CultureInfo.InvariantCulture) + "\"");
                    if (i.MinValue != decimal.MinValue) sb.Append(", " + nameof(DecimalPropertyAttribute.MinValue) + " = \"" + i.MinValue.ToString(CultureInfo.InvariantCulture) + "\"");
                    if (i.MaxValue != decimal.MaxValue) sb.Append(", " + nameof(DecimalPropertyAttribute.MaxValue) + " = \"" + i.MaxValue.ToString(CultureInfo.InvariantCulture) + "\"");
                    if (i.UniqueValues) sb.Append(", " + nameof(DecimalPropertyAttribute.UniqueValues) + " = true");
                    if (i.FacetRangePowerBase != 0) sb.Append(", " + nameof(DecimalPropertyAttribute.FacetRangePowerBase) + " = " + i.FacetRangePowerBase.ToString(CultureInfo.InvariantCulture));
                    if (i.FacetRangeCount != 0) sb.Append(", " + nameof(DecimalPropertyAttribute.FacetRangeCount) + " = " + i.FacetRangeCount);
                    sb.AppendLine(")]");
                }
                break;
            case PropertyType.TimeSpan: {
                    addBaseAttributes<TimeSpanPropertyAttribute>(p, dm, sb);
                    var i = (TimeSpanPropertyModel)p;
                    if (i.Indexed) sb.Append(", " + nameof(TimeSpanPropertyAttribute.Indexed) + " = true"); // the attribute member is a plain bool
                    if (i.NotFacet) sb.Append(", " + nameof(TimeSpanPropertyAttribute.NotFacet) + " = true");
                    if (i.DefaultValue != TimeSpan.Zero) sb.Append(", " + nameof(TimeSpanPropertyAttribute.DefaultValue) + " = \"" + i.DefaultValue.ToString("c") + "\"");
                    if (i.MinValue != TimeSpan.MinValue) sb.Append(", " + nameof(TimeSpanPropertyAttribute.MinValue) + " = \"" + i.MinValue.ToString("c") + "\"");
                    if (i.MaxValue != TimeSpan.MaxValue) sb.Append(", " + nameof(TimeSpanPropertyAttribute.MaxValue) + " = \"" + i.MaxValue.ToString("c") + "\"");
                    if (i.UniqueValues) sb.Append(", " + nameof(TimeSpanPropertyAttribute.UniqueValues) + " = true");
                    if (i.FacetRangePowerBase != 0) sb.Append(", " + nameof(TimeSpanPropertyAttribute.FacetRangePowerBase) + " = " + i.FacetRangePowerBase.ToString(CultureInfo.InvariantCulture));
                    if (i.FacetRangeCount != 0) sb.Append(", " + nameof(TimeSpanPropertyAttribute.FacetRangeCount) + " = " + i.FacetRangeCount);
                    sb.AppendLine(")]");
                }
                break;
            case PropertyType.Float: {
                    addBaseAttributes<FloatPropertyAttribute>(p, dm, sb);
                    var i = (FloatPropertyModel)p;
                    if (i.Indexed) sb.Append(", " + nameof(FloatPropertyAttribute.Indexed) + " = true"); // the attribute member is a plain bool
                    if (i.NotFacet) sb.Append(", " + nameof(FloatPropertyAttribute.NotFacet) + " = true");
                    if (i.DefaultValue != 0) sb.Append(", " + nameof(FloatPropertyAttribute.DefaultValue) + " = " + i.DefaultValue.ToString(CultureInfo.InvariantCulture) + "f");
                    if (i.MinValue != float.MinValue) sb.Append(", " + nameof(FloatPropertyAttribute.MinValue) + " = " + i.MinValue.ToString(CultureInfo.InvariantCulture) + "f");
                    if (i.MaxValue != float.MaxValue) sb.Append(", " + nameof(FloatPropertyAttribute.MaxValue) + " = " + i.MaxValue.ToString(CultureInfo.InvariantCulture) + "f");
                    if (i.FacetRangePowerBase != 0) sb.Append(", " + nameof(FloatPropertyAttribute.FacetRangePowerBase) + " = " + i.FacetRangePowerBase.ToString(CultureInfo.InvariantCulture));
                    if (i.FacetRangeCount != 0) sb.Append(", " + nameof(FloatPropertyAttribute.FacetRangeCount) + " = " + i.FacetRangeCount);
                    sb.AppendLine(")]");
                }
                break;
            case PropertyType.String: {
                    addBaseAttributes<StringPropertyAttribute>(p, dm, sb);
                    var s = (StringPropertyModel)p;
                    if (s.IndexedBySemantic) sb.Append(", " + nameof(StringPropertyAttribute.IndexedBySemantic) + " = true"); // the attribute member is a plain bool
                    if (s.Indexed) sb.Append(", " + nameof(StringPropertyAttribute.Indexed) + " = true"); // the attribute member is a plain bool
                    if (s.NotFacet) sb.Append(", " + nameof(StringPropertyAttribute.NotFacet) + " = true");
                    if (s.IndexedByWords) sb.Append(", " + nameof(StringPropertyAttribute.IndexedByWords) + " = true"); // the attribute member is a plain bool
                    if (s.UniqueValues) sb.Append(", " + nameof(StringPropertyAttribute.UniqueValues) + " = true");
                    if (s.MinWordLength != StringPropertyModel.DefaultMinWordLength) sb.Append(", " + nameof(StringPropertyAttribute.MinWordLength) + " = " + s.MinWordLength);
                    if (s.MaxWordLength != StringPropertyModel.DefaultMaxWordLength) sb.Append(", " + nameof(StringPropertyAttribute.MaxWordLength) + " = " + s.MaxWordLength);
                    if (s.MinLength != 0) sb.Append(", " + nameof(StringPropertyAttribute.MinLength) + " = " + s.MinLength);
                    if (s.MaxLength != int.MaxValue) sb.Append(", " + nameof(StringPropertyAttribute.MaxLength) + " = " + s.MaxLength);
                    if (s.PrefixSearch) sb.Append(", " + nameof(StringPropertyAttribute.PrefixSearch) + " = true");
                    if (s.InfixSearch) sb.Append(", " + nameof(StringPropertyAttribute.InfixSearch) + " = true");
                    if (s.IgnoreDuplicateEmptyValues) sb.Append(", " + nameof(StringPropertyAttribute.IgnoreDuplicateEmptyValues) + " = true");
                    if (s.DefaultValue != null) sb.Append(", " + nameof(StringPropertyAttribute.DefaultValue) + " = " + stringLiteral(s.DefaultValue));
                    if (s.StringType != StringValueType.AnyString) sb.Append(", " + nameof(StringPropertyAttribute.StringType) + " = " + typeof(StringValueType).Namespace + "." + nameof(StringValueType) + "." + s.StringType);
                    sb.AppendLine(")]");
                }
                break;
            case PropertyType.StringArray: {
                    addBaseAttributes<StringArrayPropertyAttribute>(p, dm, sb);
                    var s = (StringArrayPropertyModel)p;
                    if (s.Indexed) sb.Append(", " + nameof(StringArrayPropertyAttribute.Indexed) + " = true"); // the attribute member is a plain bool
                    if (s.NotFacet) sb.Append(", " + nameof(StringArrayPropertyAttribute.NotFacet) + " = true");
                    if (s.UniqueValues) sb.Append(", " + nameof(StringArrayPropertyAttribute.UniqueValues) + " = true");
                    sb.AppendLine(")]");
                }
                break;
            case PropertyType.GuidArray: {
                    addBaseAttributes<GuidArrayPropertyAttribute>(p, dm, sb);
                    var s = (GuidArrayPropertyModel)p;
                    if (s.Indexed) sb.Append(", " + nameof(GuidArrayPropertyAttribute.Indexed) + " = true"); // the attribute member is a plain bool
                    if (s.NotFacet) sb.Append(", " + nameof(GuidArrayPropertyAttribute.NotFacet) + " = true");
                    if (s.UniqueValues) sb.Append(", " + nameof(GuidArrayPropertyAttribute.UniqueValues) + " = true");
                    sb.AppendLine(")]");
                }
                break;
            case PropertyType.EnumArray: {
                    addBaseAttributes<EnumArrayPropertyAttribute>(p, dm, sb);
                    var s = (EnumArrayPropertyModel)p;
                    if (s.Indexed) sb.Append(", " + nameof(EnumArrayPropertyAttribute.Indexed) + " = true"); // the attribute member is a plain bool
                    if (s.NotFacet) sb.Append(", " + nameof(EnumArrayPropertyAttribute.NotFacet) + " = true");
                    if (s.UniqueValues) sb.Append(", " + nameof(EnumArrayPropertyAttribute.UniqueValues) + " = true");
                    if (!string.IsNullOrEmpty(s.FullEnumTypeName)) sb.Append(", " + nameof(EnumArrayPropertyAttribute.FullEnumTypeName) + " = \"" + s.FullEnumTypeName + "\"");
                    if (s.LegalValues != null) sb.Append(", " + nameof(EnumArrayPropertyAttribute.LegalValues) + " = new int[] {" + string.Join(", ", s.LegalValues.Select(v => v.ToString())) + "}");
                    if (s.LegalValueNames != null) sb.Append(", " + nameof(EnumArrayPropertyAttribute.LegalValueNames) + " = " + stringArrayArg(s.LegalValueNames.Select(stringLiteral)));
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
                    if (r.Facet) sb.Append(", " + nameof(RelationPropertyAttribute.Facet) + " = true");
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
            case PropertyType.Embedded: {
                    var e = (EmbeddedPropertyModel)p;
                    var isMap = e.EmbeddedValueType == EmbeddedValueType.InnerNodeMap;
                    if (isMap) addBaseAttributes<EmbeddedMapPropertyAttribute>(p, dm, sb);
                    else addBaseAttributes<EmbeddedPropertyAttribute>(p, dm, sb);
                    if (e.IncludeTypes != IncludeTypeOptions.ThisTypeAndDescending) sb.Append(", " + nameof(EmbeddedPropertyAttribute.IncludeTypes) + " = " + typeof(IncludeTypeOptions).Namespace + "." + nameof(IncludeTypeOptions) + "." + e.IncludeTypes);
                    if (e.InnerNodeTypes.Count > 0) {
                        var guidStrings = e.InnerNodeTypes.Select(t => "\"" + t.ToString() + "\"");
                        sb.Append(", " + nameof(EmbeddedPropertyAttribute.InnerTypeIds) + " = " + stringArrayArg(guidStrings));
                    }
                    if (isMap) {
                        if (e.KeyProperty == InnerNodeDataMap<object>.PropertyIdNodeIntId) {
                            sb.Append(", " + nameof(EmbeddedMapPropertyAttribute.KeyType) + " = " + typeof(KeyPropertyType).Namespace + "." + nameof(KeyPropertyType) + "." + nameof(KeyPropertyType.NodeIntegerId));
                        } else if (e.KeyProperty != InnerNodeDataMap<object>.PropertyIdNodeGuidId) {
                            sb.Append(", " + nameof(EmbeddedMapPropertyAttribute.KeyType) + " = " + typeof(KeyPropertyType).Namespace + "." + nameof(KeyPropertyType) + "." + nameof(KeyPropertyType.NodeProperty));
                            sb.Append(", " + nameof(EmbeddedMapPropertyAttribute.KeyProperty) + " = \"" + e.KeyProperty + "\"");
                        }
                    }
                    sb.AppendLine(")]");
                }
                break;
                case PropertyType.Reference: {
                    addBaseAttributes<ReferencePropertyAttribute>(p, dm, sb);
                    var r = (ReferencePropertyModel)p;
                    if (r.NodeTypes.Count > 0) {
                        var guidStrings = r.NodeTypes.Select(t => "\"" + t.ToString() + "\"");
                        sb.Append(", " + nameof(ReferencePropertyAttribute.TypeIds) + " = " + stringArrayArg(guidStrings));
                    }
                    if (r.Indexed) sb.Append(", " + nameof(ReferencePropertyAttribute.Indexed) + " = true"); // the attribute member is a plain bool
                    if (r.NotFacet) sb.Append(", " + nameof(ReferencePropertyAttribute.NotFacet) + " = true");
                    if (r.IncludeTypes != IncludeTypeOptions.ThisTypeAndDescending) sb.Append(", " + nameof(ReferencePropertyAttribute.IncludeTypes) + " = " + typeof(IncludeTypeOptions).Namespace + "." + nameof(IncludeTypeOptions) + "." + r.IncludeTypes);
                    sb.AppendLine(")]");
                } break;
                case PropertyType.References: {
                    addBaseAttributes<ReferencesPropertyAttribute>(p, dm, sb);
                    var r = (ReferencesPropertyModel)p;
                    if (r.NodeTypes.Count > 0) {
                        var guidStrings = r.NodeTypes.Select(t => "\"" + t.ToString() + "\"");
                        sb.Append(", " + nameof(ReferencesPropertyAttribute.TypeIds) + " = " + stringArrayArg(guidStrings));
                    }
                    if (r.Indexed) sb.Append(", " + nameof(ReferencesPropertyAttribute.Indexed) + " = true"); // the attribute member is a plain bool
                    if (r.NotFacet) sb.Append(", " + nameof(ReferencesPropertyAttribute.NotFacet) + " = true");
                    if (r.UniqueValues) sb.Append(", " + nameof(ReferencesPropertyAttribute.UniqueValues) + " = true");
                    if (r.IncludeTypes != IncludeTypeOptions.ThisTypeAndDescending) sb.Append(", " + nameof(ReferencesPropertyAttribute.IncludeTypes) + " = " + typeof(IncludeTypeOptions).Namespace + "." + nameof(IncludeTypeOptions) + "." + r.IncludeTypes);
                    sb.AppendLine(")]");
                } break;
            default:
                throw new NotSupportedException();
        }

    }
    static void appendRelationCode(RelationModel relation, Datamodel dm, StringBuilder sb, bool addAttributes) {
        if (addAttributes) {
            var attributeName = nameof(RelationAttribute);
            attributeName = typeof(RelationAttribute).Namespace + "." + attributeName.Remove(attributeName.Length - "Attribute".Length);
            attributeName = typeAndNamespace(relation.Namespace, attributeName);
            sb.Append("    [" + attributeName + "(");
            sb.Append(nameof(RelationAttribute.Id) + " = \"" + relation.Id + "\"");
            if (relation.SourceTypes.Count > 0) {
                var guidStrings = relation.SourceTypes.Select(t => "\"" + t.ToString() + "\"");
                sb.Append(", " + nameof(RelationAttribute.SourceTypes) + " = " + stringArrayArg(guidStrings));
            }
            if (relation.TargetTypes.Count > 0) {
                var guidStrings = relation.TargetTypes.Select(t => "\"" + t.ToString() + "\"");
                sb.Append(", " + nameof(RelationAttribute.TargetTypes) + " = " + stringArrayArg(guidStrings));
            }
            if (relation.DisallowCircularReferences) {
                sb.Append(", " + nameof(RelationAttribute.DisallowCircularReferences) + " = true");
            }
            sb.AppendLine(")]");
        }
        var inheritance = " : " + typeAndNamespace(relation.Namespace, typeof(IRelation).Namespace, relation.RelationType.ToString()) + "<" + (relation.RelationType switch {
            RelationType.OneToMany =>
                typeAndNamespace(relation.Namespace, dm.FindFirstCommonBase(relation.SourceTypes).FullName) + ", " +
                typeAndNamespace(relation.Namespace, dm.FindFirstCommonBase(relation.TargetTypes).FullName),
            RelationType.OneToOne =>
                typeAndNamespace(relation.Namespace, dm.FindFirstCommonBase(relation.SourceTypes).FullName) + ", " +
                typeAndNamespace(relation.Namespace, dm.FindFirstCommonBase(relation.TargetTypes).FullName),
            RelationType.ManyToMany =>
                typeAndNamespace(relation.Namespace, dm.FindFirstCommonBase(relation.SourceTypes).FullName) + ", " +
                typeAndNamespace(relation.Namespace, dm.FindFirstCommonBase(relation.TargetTypes).FullName),
            RelationType.OneOne =>
            typeAndNamespace(relation.Namespace, dm.FindFirstCommonBase(relation.SourceTypes).FullName),
            RelationType.ManyMany =>
                typeAndNamespace(relation.Namespace, dm.FindFirstCommonBase(relation.SourceTypes).FullName),
            _ => throw new Exception("Unknown relation type " + relation.RelationType),
        });
        inheritance += ">";
        // inheritance += ", " + relation.CodeName + ">"; // self reference
        sb.AppendLine("    public class " + relation.CodeName + inheritance + " { ");
        if (!string.IsNullOrEmpty(relation.CodeNameSources)) {
            switch (relation.RelationType) {
                case RelationType.OneOne:
                    sb.AppendLine("        public class " + relation.CodeNameSources + " : " + nameof(OneOne<object>.One) + "{ }");
                    break;
                case RelationType.OneToOne:
                    sb.AppendLine("        public class " + relation.CodeNameSources + " : " + nameof(OneToOne<object, object>.OneFrom) + "{ }");
                    sb.AppendLine("        public class " + relation.CodeNameTargets + " : " + nameof(OneToOne<object, object>.OneTo) + "{ }");
                    break;
                case RelationType.OneToMany:
                    sb.AppendLine("        public class " + relation.CodeNameSources + " : " + nameof(OneToMany<object, object>.One) + "{ }");
                    sb.AppendLine("        public class " + relation.CodeNameTargets + " : " + nameof(OneToMany<object, object>.Many) + "{ }");
                    break;
                case RelationType.ManyMany:
                    sb.AppendLine("        public class " + relation.CodeNameSources + " : " + nameof(ManyMany<object>.Many) + "{ }");
                    break;
                case RelationType.ManyToMany:
                    sb.AppendLine("        public class " + relation.CodeNameSources + " : " + nameof(ManyToMany<object, object>.ManyFrom) + "{ }");
                    sb.AppendLine("        public class " + relation.CodeNameTargets + " : " + nameof(ManyToMany<object, object>.ManyTo) + "{ }");
                    break;
                default:
                    break;
            }
        }
        sb.AppendLine("    }"); // end class
    }
    public static string TypeAndNamespace(string currentNameSpace, Type type) {
        if (type.IsGenericType) {
            var typeName = type.Name.Substring(0, type.Name.IndexOf('`')); // remove the generic type parameter count
            var typeNamespace = type.Namespace ?? string.Empty;
            return typeAndNamespace(currentNameSpace, typeNamespace, typeName);
        }
        return typeAndNamespace(currentNameSpace, type.Namespace, type.Name);
    }
    static string typeAndNamespace(string? currentNameSpace, string fullTypeName) {
        var typeName = fullTypeName;
        string? typeNamespace = null;
        if (fullTypeName.Contains('.')) {
            var lastDotIndex = fullTypeName.LastIndexOf('.');
            typeNamespace = fullTypeName[..lastDotIndex];
            typeName = fullTypeName[(lastDotIndex + 1)..];
        }
        return typeAndNamespace(currentNameSpace, typeNamespace, typeName);
    }
    static string typeAndNamespace(string? currentNameSpace, string? typeNamespace, string typeName) {
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
