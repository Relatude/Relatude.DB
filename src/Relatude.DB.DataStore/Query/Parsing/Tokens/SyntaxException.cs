namespace Relatude.DB.Query.Parsing.Tokens;
public class SyntaxException : Exception {
    string _code;
    int _pos;
    string? _message;
    public SyntaxException(string code, int pos) {
        _pos = pos;
        _code = code;
    }
    public SyntaxException(string message, string code, int pos) {
        _pos = pos;
        _code = code;
        _message = message;
    }
    string extract(int from, int to, string text) {
        if (from < 0) from = 0;
        if (to > text.Length) to = text.Length;
        return text[from..to];
    }
    public override string Message {
        get {
            var padding = 30;
            if (_pos <= 0) _pos = 0;
            else if (_pos >= _code.Length) _pos = _code.Length - 1;
            return (_message == null ? "" : _message)
                + "Unexpected syntax at postion " + _pos + ": "
                + (_pos - padding > 0 ? "..." : "")
                + extract(_pos - padding, _pos, _code)
                //+ _code[_pos] + "\u0333"
                + " ==> " + _code[_pos] + " <== "
                + extract(_pos + 1, _pos + padding, _code)
                + (_pos + padding < _code.Length ? "..." : "");
        }
    }
}

