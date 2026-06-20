namespace Relatude.DB.Nodes;

public enum LinkType {
    NodeLink,
    LocalAddress,
    ExternalUrl,
    EmailAddress,
}
public interface ILink {
    LinkType LinkType { get; }
}
public class Link<T> : ILink where T : notnull {
    public LinkType LinkType => throw new NotImplementedException();
}