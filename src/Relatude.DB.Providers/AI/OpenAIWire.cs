using System.Text;
using System.Text.Json;
using Relatude.DB.Http;

namespace Relatude.DB.AI;
/// <summary>
/// Request building and response parsing for the OpenAI wire format (chat completions and embeddings),
/// shared by the OpenAI, Azure OpenAI and Anthropic (embeddings only) providers.
/// </summary>
internal static class OpenAIWire {
    public static string BuildChatRequest(string? model, string prompt, int? maxOutputTokens) {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) {
            w.WriteStartObject();
            if (model != null) w.WriteString("model", model);
            if (maxOutputTokens.HasValue) w.WriteNumber("max_tokens", maxOutputTokens.Value);
            w.WriteStartArray("messages");
            w.WriteStartObject();
            w.WriteString("role", "user");
            w.WriteString("content", prompt);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
    public static string ParseChatResponse(string json) {
        using var doc = JsonDocument.Parse(json);
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0) return "";
        var content = choices[0].GetProperty("message").GetProperty("content");
        return content.ValueKind == JsonValueKind.String ? content.GetString() ?? "" : "";
    }
    public static string BuildEmbeddingsRequest(string? model, string[] input) {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) {
            w.WriteStartObject();
            if (model != null) w.WriteString("model", model);
            w.WriteStartArray("input");
            foreach (var text in input) w.WriteStringValue(text);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
    public static float[][] ParseEmbeddingsResponse(string json, int expectedCount) {
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");
        var result = new float[expectedCount][];
        var received = 0;
        foreach (var item in data.EnumerateArray()) {
            var index = item.GetProperty("index").GetInt32();
            if (index < 0 || index >= expectedCount) throw new Exception($"Embedding response contained an unexpected index: {index}. ");
            var embedding = item.GetProperty("embedding");
            var vector = new float[embedding.GetArrayLength()];
            var i = 0;
            foreach (var value in embedding.EnumerateArray()) vector[i++] = value.GetSingle();
            result[index] = vector;
            received++;
        }
        if (received != expectedCount) throw new Exception($"Embedding response contained {received} vectors, expected {expectedCount}. ");
        return result;
    }
    public static async Task<string> PostAsync(HttpClient client, string url, string jsonBody, Action<HttpRequestMessage> setAuthHeaders) {
        using var response = await HttpRetry.SendAsync(client, () => {
            var request = new HttpRequestMessage(HttpMethod.Post, url) {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            };
            setAuthHeaders(request);
            return request;
        });
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) {
            throw new Exception($"The AI service at {url} returned {(int)response.StatusCode} {response.StatusCode}: {Truncate(body, 500)}");
        }
        return body;
    }
    public static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength] + "...";
}
