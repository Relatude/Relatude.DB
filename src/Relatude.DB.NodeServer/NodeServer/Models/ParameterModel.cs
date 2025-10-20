using Relatude.DB.Query;

namespace Relatude.DB.NodeServer.Models;
public class ParameterModel {
    public string? Name { get; set; }
    public string? Value { get; set; }
    public string? DataType { get; set; }
    
    public static Parameter Convert(ParameterModel model) {
        if (model.Name == null) throw new InvalidOperationException("Parameter name cannot be null.");
        if (model.Value == null) throw new InvalidOperationException("Parameter value cannot be null.");
        // Invariant culture:
        var formatProvider = System.Globalization.CultureInfo.InvariantCulture;

        object? value = model.DataType switch {
            "null" => null,
            "string" => model.Value,
            "int" => int.Parse(model.Value, formatProvider),
            "long" => long.Parse(model.Value, formatProvider),
            "double" => double.Parse(model.Value, formatProvider), 
            "float" => float.Parse(model.Value, formatProvider),
            "bool" => bool.Parse(model.Value),
            "DateTime" => DateTime.Parse(model.Value, formatProvider),
            "TimeSpan" => TimeSpan.Parse(model.Value, formatProvider),
            "Guid" => Guid.Parse(model.Value),
            "string[]" => model.Value.Split(',').Select(s => s.Trim()).ToArray(),
            "Guid[]" => model.Value.Split(',').Select(s => Guid.Parse(s.Trim())).ToArray(),
            _ => throw new InvalidOperationException($"Unsupported data type: {model.DataType}")
        };
        return new Parameter(model.Name, value);
    }
}
