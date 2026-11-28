using System.Text.Json;

namespace Relatude.DB.NodeServer.Operations;

/// <summary>The domains operations are registered in, each a set of names of its own.</summary>
public static class OperationDomains {
    /// <summary>The database's own: the admin UI's commands.</summary>
    public const string Db = "db";
    /// <summary>The CMS's: its API's commands.</summary>
    public const string Cms = "cms";
}

/// <summary>Who may call an operation.</summary>
public static class OperationCallers {
    /// <summary>The database's admin UI, signed in to the server.</summary>
    public const string AdminUI = "admin-ui";
    /// <summary>The CMS interface over its API, and code in the same process running a command as a user.</summary>
    public const string CmsInterface = "cms-interface";
    /// <summary>AI agents: the CMS's own agent and those over MCP.</summary>
    public const string Agent = "agent";
}

/// <summary>
/// One thing a caller can ask of the server, registered once in an <see cref="OperationRegistry"/>: its name within its
/// domain, who may call it, and its handler. One with no description is undescribed, and only its own interface calls it.
/// </summary>
public sealed class Operation {
    public required string Domain { get; init; }
    public required string Name { get; init; }
    public required Func<OperationContext, Task<object?>> Handler { get; init; }
    public IReadOnlyCollection<string> Callers { get; init; } = [];
    public string? Title { get; init; }
    /// <summary>What it does and when to use it, for people and models; null for an undescribed operation.</summary>
    public string? Description { get; init; }
    public bool IsDescribed => Description != null;
    /// <summary>The JSON Schema of its input, an object schema; null where its callers need none.</summary>
    public JsonElement? InputSchema { get; init; }
    /// <summary>What it belongs to, and so what a caller may be limited to, as an agent's key is.</summary>
    public string? Area { get; init; }
    /// <summary>The module that brought it, which its domain may leave it out for while the module is not active.</summary>
    public string? Module { get; init; }
    /// <summary>It changes nothing: the CMS API serves only these to an anonymous caller.</summary>
    public bool ReadOnly { get; init; }
    /// <summary>It can take away or overwrite what was there.</summary>
    public bool Destructive { get; init; }
    /// <summary>Calling it again with the same input changes nothing more.</summary>
    public bool Idempotent { get; init; }
    /// <summary>
    /// What a call with this input would do, worked out without doing any of it, as one operation can write a draft with
    /// some input and publish with others. Left out, one that is <see cref="ReadOnly"/> reads, and any other is taken to
    /// be <see cref="OperationEffect.Live"/>.
    /// </summary>
    public Func<OperationContext, Task<OperationCallEffect>>? Effect { get; init; }
    /// <summary>
    /// Its answer may go compressed to a browser that takes it. Opted into per operation, as an answer holding a secret
    /// beside text a request put there is what BREACH reads.
    /// </summary>
    public bool Compress { get; init; }
    public bool Allows(string caller) => Callers.Contains(caller);
}
