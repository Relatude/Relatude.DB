using System.Security.Cryptography;
using System.Text;
using Relatude.DB.IO;

namespace Relatude.DB.GraphQL.Endpoints;

/// <summary>One endpoint file as found in the graphql folder: its key, the definition, or why it could not be read.</summary>
public sealed class GraphQLEndpointFile {
    public required string[] Key { get; init; }
    public GraphQLEndpointDefinition? Definition { get; init; }
    public string? Error { get; init; }
    public string FileName => Key[^1];
}

/// <summary>
/// The endpoint definitions of one database: "graphql/*.json" on its storage provider. Any json file in the folder is
/// an endpoint, so definitions can be written by hand and kept in source control; a file without an id gets one
/// derived from its name.
/// </summary>
public sealed class GraphQLEndpointStore(IIOProvider io) {
    public static string FolderName => FileKeyUtility.GraphQLFolderName;

    public List<GraphQLEndpointFile> Load() {
        var result = new List<GraphQLEndpointFile>();
        List<string[]> keys;
        try {
            keys = io.Search([FolderName, "*.json"]);
        } catch {
            return result;
        }
        foreach (var key in keys.OrderBy(k => k.AsKeyString(), StringComparer.OrdinalIgnoreCase)) {
            try {
                var definition = GraphQLEndpointDefinition.FromJson(io.ReadAllTextUTF8(key));
                if (definition.Id == Guid.Empty) definition.Id = IdFromKey(key);
                if (string.IsNullOrWhiteSpace(definition.Name)) definition.Name = Path.GetFileNameWithoutExtension(key[^1]);
                result.Add(new GraphQLEndpointFile { Key = key, Definition = definition });
            } catch (Exception ex) {
                result.Add(new GraphQLEndpointFile { Key = key, Error = ex.Message });
            }
        }
        return result;
    }

    /// <summary>Writes the definition to its existing file, or to a new file named after the endpoint.</summary>
    public GraphQLEndpointFile Save(GraphQLEndpointDefinition definition, string[]? existingKey) {
        if (definition.Id == Guid.Empty) definition.Id = Guid.NewGuid();
        var key = existingKey ?? newKey(definition);
        io.WriteAllTextUTF8(key, definition.ToJson());
        return new GraphQLEndpointFile { Key = key, Definition = definition };
    }

    public void Delete(string[] key) => io.DeleteFileIfItExists(key);

    string[] newKey(GraphQLEndpointDefinition definition) {
        var slug = FileKeyUtility.FilterLegalCharInFileKey(definition.Name).Replace(' ', '-');
        if (slug.Length > 40) slug = slug[..40].Trim('-');
        if (slug.Length == 0 || slug == "unnamed") slug = definition.Id.ToString("N");
        var candidate = new[] { FolderName, slug + ".json" };
        for (var i = 2; io.Exists(candidate); i++) candidate = [FolderName, slug + "-" + i + ".json"];
        return candidate;
    }

    /// <summary>A stable id for a hand-written file that has none.</summary>
    public static Guid IdFromKey(string[] key) {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key.AsKeyString().ToLowerInvariant()));
        return new Guid(hash.AsSpan(0, 16));
    }
}
