using System.Text.Json;

namespace Relatude.DB.NodeServer.Operations;

/// <summary>
/// One call of an operation: what it was called with, for whom, and what stops it. Each domain's context derives from it
/// with what its operations need: the admin UI's UICommandContext, the CMS's CmsApiContext.
/// </summary>
public abstract class OperationContext {
    protected OperationContext(JsonElement? payload, CancellationToken cancellation, Guid? userId = null, Operation? operation = null) {
        PayloadElement = payload;
        Cancellation = cancellation;
        UserId = userId;
        Operation = operation;
    }

    /// <summary>The operation called; null for a context made for work of its own, such as a process's.</summary>
    public Operation? Operation { get; }

    /// <summary>The raw payload, or null.</summary>
    public JsonElement? PayloadElement { get; }

    /// <summary>Cancelled when the caller stops the call (see <see cref="OperationRuns"/>) or gives up on it.</summary>
    public CancellationToken Cancellation { get; }

    /// <summary>The user the call runs as; null for an anonymous one, and for the admin UI's.</summary>
    public Guid? UserId { get; }
}
