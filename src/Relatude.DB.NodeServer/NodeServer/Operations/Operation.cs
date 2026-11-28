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
    /// <summary>What it does and when to use it, for people and models; null for an undescribed operation.</summary>
    public string? Description { get; init; }
    public bool IsDescribed => Description != null;
    /// <summary>It changes nothing: the CMS API serves only these to an anonymous caller.</summary>
    public bool ReadOnly { get; init; }
    /// <summary>
    /// Its answer may go compressed to a browser that takes it. Opted into per operation, as an answer holding a secret
    /// beside text a request put there is what BREACH reads.
    /// </summary>
    public bool Compress { get; init; }
    public bool Allows(string caller) => Callers.Contains(caller);
}
