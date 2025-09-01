using System.Text;

namespace Relatude.DB.GraphQL.Query.GraphQL;

//interface IGQLNode {
//    void Evaluate(StringBuilder sb);
//}
//public class GQLDocument {
//    public GQLField Source;
//}
//public class GQLField {
//    public string Name { get; set; }
//    public List<GQLArgument>? Arguments { get; set; }
//    public List<GQLField>? SubFields { get; set; }
//    public List<string>? Fragments { get; set; }
//}
//public class GQLArgument {
//    public string Name { get; set; }
//    public string Value { get; set; }
//}
//public class Result {
//}



public static class DataModelSDLExtenstion {
    //public static string ToSDL(this Datamodel dataModel) {
    //var sb = new StringBuilder();
    //sb.AppendLine("{");
    //sb.AppendLine("\"data:\"{");
    //sb.AppendLine("\"__schema:\"{");
    //sb.AppendLine("type Query {");
    //foreach (var type in dataModel.NodeTypes.Values) {
    //    sb.AppendLine($"  {type.ToString()}: [{type.Name}]");
    //}
    //sb.AppendLine("}");
    //foreach (var type in dataModel.Types) {
    //    sb.AppendLine($"type {type.Name} {{");
    //    foreach (var field in type.Fields) {
    //        sb.AppendLine($"  {field.Name}: {field.Type}");
    //    }
    //    sb.AppendLine("}");
    //}
    //return sb.ToString();
    //}
}
