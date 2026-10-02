using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Relatude.DB.Datamodels;

namespace Relatude.DB.GraphQL.Endpoints;

/// <summary>
/// GraphQL over HTTP for one executor: POST {"query","operationName","variables"} (or a raw application/graphql body),
/// GET ?query=... when the endpoint allows it, GET ?sdl for the schema text. Shared by the fixed route
/// and the endpoints defined in the admin UI.
/// </summary>
public static class GraphQLHttpHandler {
    public const string ApiKeyHeader = "X-Api-Key";

    public static async Task HandleAsync(HttpContext http, RelatudeGraphQL executor, QueryContext? queryContext) {
        var definition = executor.Definition;
        if (!IsAuthorized(http, definition)) {
            await writeErrors(http, StatusCodes.Status401Unauthorized, $"This endpoint requires an API key in the {ApiKeyHeader} header or as a bearer token.");
            return;
        }
        if (HttpMethods.IsOptions(http.Request.Method)) {
            http.Response.StatusCode = StatusCodes.Status204NoContent;
            http.Response.Headers.Allow = "GET, POST, OPTIONS";
            return;
        }
        GraphQLRequest? request;
        if (HttpMethods.IsGet(http.Request.Method)) {
            if (http.Request.Query.ContainsKey("sdl")) {
                http.Response.ContentType = "text/plain; charset=utf-8";
                await http.Response.WriteAsync(executor.ToSDL());
                return;
            }
            if (!definition.EnableGetRequests) {
                await writeErrors(http, StatusCodes.Status405MethodNotAllowed, "GET requests are disabled on this endpoint; POST a JSON body.");
                return;
            }
            string? query = http.Request.Query["query"];
            if (string.IsNullOrEmpty(query)) {
                await writeErrors(http, StatusCodes.Status400BadRequest, "Pass a GraphQL query via ?query=... or POST a JSON body.");
                return;
            }
            request = new GraphQLRequest { Query = query, OperationName = http.Request.Query["operationName"] };
            string? variables = http.Request.Query["variables"];
            if (!string.IsNullOrEmpty(variables)) {
                try {
                    request.Variables = JsonSerializer.Deserialize<JsonElement>(variables);
                } catch (JsonException) {
                    await writeErrors(http, StatusCodes.Status400BadRequest, "The variables parameter is not valid JSON.");
                    return;
                }
            }
        } else if (HttpMethods.IsPost(http.Request.Method)) {
            try {
                request = await readPostAsync(http);
            } catch (JsonException ex) {
                await writeErrors(http, StatusCodes.Status400BadRequest, "Invalid JSON request body: " + ex.Message);
                return;
            }
            if (request == null) {
                await writeErrors(http, StatusCodes.Status400BadRequest, "Empty request body.");
                return;
            }
        } else {
            await writeErrors(http, StatusCodes.Status405MethodNotAllowed, "Use GET or POST.");
            return;
        }
        var result = executor.Execute(request, queryContext);
        // a request that produced no data at all (syntax, validation, variables) is the client's mistake
        http.Response.StatusCode = result.Data == null && result.Errors != null ? StatusCodes.Status400BadRequest : StatusCodes.Status200OK;
        http.Response.ContentType = "application/graphql-response+json; charset=utf-8";
        await http.Response.WriteAsync(result.ToJson());
    }

    static async Task<GraphQLRequest?> readPostAsync(HttpContext http) {
        var contentType = http.Request.ContentType ?? "";
        if (contentType.StartsWith("application/graphql", StringComparison.OrdinalIgnoreCase) && !contentType.Contains("json", StringComparison.OrdinalIgnoreCase)) {
            using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
            var text = await reader.ReadToEndAsync();
            return string.IsNullOrWhiteSpace(text) ? null : new GraphQLRequest { Query = text };
        }
        return await http.Request.ReadFromJsonAsync<GraphQLRequest>(_requestJson);
    }

    static readonly JsonSerializerOptions _requestJson = new() { PropertyNameCaseInsensitive = true };

    public static bool IsAuthorized(HttpContext http, GraphQLEndpointDefinition definition) {
        if (string.IsNullOrEmpty(definition.ApiKey)) return true;
        string? provided = http.Request.Headers[ApiKeyHeader].FirstOrDefault();
        if (provided == null) {
            var auth = http.Request.Headers.Authorization.FirstOrDefault();
            if (auth != null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) provided = auth[7..].Trim();
        }
        if (string.IsNullOrEmpty(provided)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(definition.ApiKey));
    }

    public static Task WriteErrorsAsync(HttpContext http, int statusCode, string message) => writeErrors(http, statusCode, message);

    static async Task writeErrors(HttpContext http, int statusCode, string message) {
        http.Response.StatusCode = statusCode;
        http.Response.ContentType = "application/json; charset=utf-8";
        await http.Response.WriteAsync(JsonSerializer.Serialize(new { errors = new[] { new { message } } }));
    }
}
