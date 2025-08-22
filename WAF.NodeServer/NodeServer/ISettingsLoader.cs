using System.Text.Json;

namespace WAF.NodeServer;
public interface ISettingsLoader {
    Task<WAFServerSettings> ReadAsync();
    Task WriteAsync(WAFServerSettings settings);
}
public class LocalSettingsLoaderFile(string filePath) : ISettingsLoader {
    static JsonSerializerOptions? _options = null;
    static JsonSerializerOptions getOptions() {
        if (_options == null) {
            _options = new JsonSerializerOptions {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                //DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };
            _options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        }
        return _options;
    }
    public async Task<WAFServerSettings> ReadAsync() {
        var path = Path.Combine(filePath);
        var json = File.Exists(path) ? await File.ReadAllTextAsync(path) : "";
        if (json == "") {
            var settings = WAFServerSettings.CreateDefault();
            await WriteAsync(settings);
            return settings;
        }
        return JsonSerializer.Deserialize<WAFServerSettings>(json, getOptions()) ?? new WAFServerSettings() {
            Id = Guid.NewGuid(),
            ContainerSettings = [],
            Name = "WAF Server"
        };
    }
    public Task WriteAsync(WAFServerSettings settings) {
        var json = JsonSerializer.Serialize(settings, getOptions());
        var path = Path.Combine(filePath);
        return File.WriteAllTextAsync(path, json);
    }
}