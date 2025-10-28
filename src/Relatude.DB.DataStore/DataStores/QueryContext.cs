namespace Relatude.DB.AccessControl;
public class QueryContext {
    public Guid UserId { get; set; }
    public bool IncludeUnpublished { get; set; } = false;
    public bool IncludeHidden { get; set; } = false;
    public bool AnyCollection { get; set; } = false;
}
