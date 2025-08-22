namespace WAF.Query;
public class Parameter(string name, object value) {
    public string Name { get; } = name;
    public object Value { get; } = value;
    public override string ToString() {
        return $"{Name}={Value}";
    }
}
