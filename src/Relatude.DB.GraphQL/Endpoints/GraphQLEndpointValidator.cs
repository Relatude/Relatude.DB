using Relatude.DB.Datamodels;
using Relatude.DB.GraphQL.Schema;

namespace Relatude.DB.GraphQL.Endpoints;

public sealed class GraphQLEndpointIssue {
    /// <summary>"error" blocks saving; "warning" is advice.</summary>
    public required string Severity { get; init; }
    public required string Message { get; init; }
    public bool IsError => Severity == "error";
}

/// <summary>Checks an endpoint definition before it is saved: names, url, limits, and what it refers to in the datamodel.</summary>
public static class GraphQLEndpointValidator {

    public static List<GraphQLEndpointIssue> Validate(GraphQLEndpointDefinition def, Datamodel? dm, IEnumerable<GraphQLEndpointDefinition> others) {
        var issues = new List<GraphQLEndpointIssue>();
        void error(string m) => issues.Add(new GraphQLEndpointIssue { Severity = "error", Message = m });
        void warning(string m) => issues.Add(new GraphQLEndpointIssue { Severity = "warning", Message = m });

        if (string.IsNullOrWhiteSpace(def.Name)) error("The endpoint needs a name.");
        var url = GraphQLEndpointDefinition.NormalizeUrl(def.Url);
        if (url == null) error("The url must be a path such as /graphql.");
        else {
            foreach (var other in others) {
                if (other.Id == def.Id) continue;
                if (string.Equals(GraphQLEndpointDefinition.NormalizeUrl(other.Url), url, StringComparison.OrdinalIgnoreCase)) {
                    error($"The url {url} is already used by the endpoint \"{other.Name}\".");
                }
            }
        }
        if (def.DefaultPageSize < 1) error("The default page size must be at least 1.");
        if (def.MaxPageSize < 1) error("The maximum page size must be at least 1.");
        if (def.MaxPageSize < def.DefaultPageSize) error("The maximum page size is smaller than the default page size.");
        if (def.MaxQueryDepth < 1) error("The maximum query depth must be at least 1.");
        if (def.MaxIncludeDepth < 0) error("The maximum relation depth cannot be negative.");
        if (!string.IsNullOrEmpty(def.ApiKey) && def.ApiKey.Trim().Length < 8) warning("The API key is short; use at least 8 characters.");
        if (def.EnableExplorer && !def.EnableIntrospection) warning("The explorer reads the schema through introspection, which is switched off: the page will say so and stay empty.");
        if (def.AllowMutations && string.IsNullOrEmpty(def.ApiKey)) warning("Mutations are allowed without an API key: anyone who can reach the url can change data.");

        if (def.Mode == GraphQLEndpointMode.Selected) {
            if (def.Types.Count == 0) warning("No types are selected, so the endpoint exposes nothing.");
            var seen = new HashSet<Guid>();
            foreach (var t in def.Types) {
                if (!seen.Add(t.NodeTypeId)) error($"The type {t.Name ?? t.NodeTypeId.ToString()} is listed twice.");
                checkName(t.Name, "type name");
                checkName(t.SingleName, "root field name");
                checkName(t.ListName, "root field name");
                if (dm != null) {
                    if (!dm.NodeTypes.TryGetValue(t.NodeTypeId, out var model)) {
                        warning($"The type {t.Name ?? t.NodeTypeId.ToString()} is not in the datamodel and will be left out.");
                        continue;
                    }
                    if (t.Properties == null) continue;
                    var seenProps = new HashSet<Guid>();
                    foreach (var p in t.Properties) {
                        if (!seenProps.Add(p.PropertyId)) error($"{model.CodeName}: the property {p.Name ?? p.PropertyId.ToString()} is listed twice.");
                        if (!model.AllProperties.ContainsKey(p.PropertyId)) warning($"{model.CodeName}: the property {p.Name ?? p.PropertyId.ToString()} is not in the datamodel and will be left out.");
                        checkName(p.Name, "field name");
                    }
                }
            }
        }

        var viewNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in def.Views) {
            var label = string.IsNullOrWhiteSpace(v.Name) ? "(unnamed)" : v.Name.Trim();
            if (string.IsNullOrWhiteSpace(v.Name)) error("A view needs a name.");
            else {
                if (NameRegistry.Sanitize(v.Name.Trim()) != v.Name.Trim()) error($"The view name {v.Name} is not a valid GraphQL name (letters, digits and underscore, not starting with a digit).");
                if (!viewNames.Add(v.Name.Trim())) error($"The view name {v.Name} is used twice.");
            }
            if (string.IsNullOrWhiteSpace(v.Query)) error($"View {label}: the query is empty.");
            else if (dm != null) {
                var info = ViewQueries.Inspect(v.Query, dm);
                if (info.Error != null) error($"View {label}: {info.Error}");
            }
        }
        return issues;

        void checkName(string? name, string what) {
            if (string.IsNullOrWhiteSpace(name)) return;
            var trimmed = name.Trim();
            if (NameRegistry.Sanitize(trimmed) != trimmed) warning($"The {what} {name} is not a valid GraphQL name; {NameRegistry.Sanitize(trimmed)} will be used.");
        }
    }
}
