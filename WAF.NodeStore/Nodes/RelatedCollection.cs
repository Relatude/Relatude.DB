using System.Collections;

namespace WAF.Nodes;
public class Single {
    public Single(Guid property, object from, object to) {
        PropertyId = property;
        From = from;
        To = to;
    }
    public Guid PropertyId { get; }
    public object From { get; }
    public object To { get; }
}
public class Multiple {
    public Multiple(Guid property, object from, IEnumerable to) {
        PropertyId = property;
        From = from;
        Tos = to;
    }
    public Guid PropertyId;
    public object From;
    public IEnumerable Tos;
}
public class RelatedCollection {
    public List<Single> Singles = new();
    public List<Multiple> Multiples = new();
    public virtual void Add(Guid property, object from, object to) {
        Singles.Add(new(property, from, to));
    }
    public virtual void Add(Guid property, object from, IEnumerable tos) {
        Multiples.Add(new(property, from, tos));        
    }
}
