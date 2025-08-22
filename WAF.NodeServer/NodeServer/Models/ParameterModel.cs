using WAF.Query;

namespace WAF.NodeServer.Models;
public class ParameterModel {
    public string? Name { get; set; }
    public string? Value { get; set; }
    public string? DataType { get; set; }
    public static Parameter Convert(ParameterModel model) {
        if (model.Name == null) throw new InvalidOperationException("Parameter name cannot be null.");
        if (model.Value == null) throw new InvalidOperationException("Parameter value cannot be null.");
        object value = model.DataType switch {
            "string" => model.Value,
            "int" => int.Parse(model.Value),
            "long" => long.Parse(model.Value),
            "double" => double.Parse(model.Value),
            "bool" => bool.Parse(model.Value),
            "DateTime" => DateTime.Parse(model.Value),
            "TimeSpan" => TimeSpan.Parse(model.Value),
            "Guid" => Guid.Parse(model.Value),
            "string[]" => model.Value.Split(',').Select(s => s.Trim()).ToArray(),
            "Guid[]" => model.Value.Split(',').Select(s => Guid.Parse(s.Trim())).ToArray(),
            _ => throw new InvalidOperationException($"Unsupported data type: {model.DataType}")
        };
        return new Parameter(model.Name, value);
    }
}
