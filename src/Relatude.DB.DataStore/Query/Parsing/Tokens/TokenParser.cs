namespace Relatude.DB.Query.Parsing.Tokens;
public abstract class TokenParser {
    static public TokenBase Parse(string code, IEnumerable<Parameter> parameters) {
        if (code == null) throw new ArgumentNullException("Query code is null. ");
        code = code.Trim();
        if (code.Length == 0) throw new Exception("Query code is empty. ");
        var unit = TokenBase.ParseStatement(code, 0, out _, null, parameters);
        if (unit == null) throw new Exception("The result of the query parsing is empty. ");
        return unit;
    }
}
