using Relatude.DB.NodeServer.Settings;
using System.Text.Json;

namespace Relatude.DB.NodeServer;
public interface ISettingsLoader {
    Task<RelatudeDBServerSettings> ReadAsync();
    Task WriteAsync(RelatudeDBServerSettings settings);
}
public class LocalSettingsLoaderFile(string filePath) : ISettingsLoader {
    static JsonSerializerOptions? _options = null;
    static JsonSerializerOptions getOptions() {
        if (_options == null) {
            _options = new JsonSerializerOptions {
                PropertyNamingPolicy = null,
                WriteIndented = true,
                PropertyNameCaseInsensitive = true,
                //DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };
            _options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        }
        return _options;
    }
    public async Task<RelatudeDBServerSettings> ReadAsync() {
        var path = Path.Combine(filePath);
        var json = File.Exists(path) ? await File.ReadAllTextAsync(path) : "";
        if (json == "") {
            var settings = RelatudeDBServerSettings.CreateDefault();
            await WriteAsync(settings);
            return settings;
        }
        return JsonSerializer.Deserialize<RelatudeDBServerSettings>(json, getOptions()) ?? new RelatudeDBServerSettings() {
            Id = Guid.NewGuid(),
            ContainerSettings = [],
            Name = "Relatude.DB Server"
        };
    }
    public Task WriteAsync(RelatudeDBServerSettings settings) {
        var json = JsonSerializer.Serialize(settings, getOptions());
        var path = Path.Combine(filePath);
        return File.WriteAllTextAsync(path, json);
    }
}