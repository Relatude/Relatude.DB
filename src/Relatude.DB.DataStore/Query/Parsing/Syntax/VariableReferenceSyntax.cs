using Relatude.DB.Query.Parsing.Syntax;
namespace Relatude.DB.Query.Parsing;
public class VariableReferenceSyntax : SyntaxUnit {
    public VariableReferenceSyntax(string name, string code, int pos1, int pos2) : base(code, pos1, pos2) {
        Name = name;
    }
    public string Name { get; }
    static public SyntaxUnit Parse(string code, int pos, out int newPos, IEnumerable<Parameter> parameters) {
        var startPos = SkipWhiteSpace(code, pos);
        pos = SkipUntilAfterReferenceIncludingGenerics(code, pos);
        newPos = pos;
        pos = SkipWhiteSpace(code, pos);
        var name = code[startPos..pos].Trim();
        if (pos < code.Length) {
            var nextChar = code[pos];
            if (nextChar == '(') {
                // last part of name is method, so remove method name
                var lastDot = name.LastIndexOf('.');
                if (lastDot == -1) throw new SyntaxException("No object reference is found for method call. ", code, pos);
                name = name.Substring(0, lastDot);
                newPos = startPos + name.Length + 1; // subtract length of method and moving past last dot
            }
        }
        if (parameters != null) {
            // check if this is a parameter
            foreach (var p in parameters) {
                if (p.Name == name) {
                    return new ValueConstantSyntax(p.Value, ParsedTypes.FromParameter, code, startPos, newPos);
                }
            }
        }
        return new VariableReferenceSyntax(name, code, startPos, newPos);
    }
    public override SyntaxUnitTypes SyntaxType => SyntaxUnitTypes.Variable;
    public string MemberName => Name.Split('.').Last();
    public override string ToString() {
        return base.ToString() + Name;
    }
}
