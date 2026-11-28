namespace Relatude.DB.NodeServer.Operations;

/// <summary>How far a call of an operation reaches. Each includes the ones before it.</summary>
public enum OperationEffect {
    /// <summary>It changes nothing.</summary>
    Read = 0,
    /// <summary>It writes drafts, which visitors do not see and a person can look at first, or throw away.</summary>
    Draft = 1,
    /// <summary>
    /// It takes effect at once, or takes something away: publishing, deleting, moving and linking content, a template
    /// outside a project, the datamodel, content of a type that has no drafts.
    /// </summary>
    Live = 2,
}

/// <summary>
/// What one call would do, said before it is made (<see cref="Operation.Effect"/>): how far it reaches, a sentence for a
/// person asked to allow it, and the credits it spends where it calls a service that charges.
/// </summary>
public sealed record OperationCallEffect(OperationEffect Effect, string Summary) {
    public int Credits { get; init; }
    /// <summary>A person is asked before this call whatever its effect: it spends credits or sends messages.</summary>
    public bool Confirm { get; init; }
    public static readonly OperationCallEffect Reads = new(OperationEffect.Read, "Reads, and changes nothing.");
}
