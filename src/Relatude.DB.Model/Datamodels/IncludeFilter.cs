namespace Relatude.DB.Datamodels;
/// <summary>
/// Marker for a filter attached to an include branch. The concrete implementation lives in the
/// query layer; the engine evaluates it when expanding the branch during eager loading.
/// </summary>
public interface IIncludeFilter { }
/// <summary>
/// AND combination of several include filters, produced when merging include branches
/// that target the same property with different filters.
/// </summary>
public sealed class CompositeIncludeFilter : IIncludeFilter {
    public CompositeIncludeFilter(IEnumerable<IIncludeFilter> filters) {
        Filters = filters.ToList();
    }
    public List<IIncludeFilter> Filters { get; }
    /// <summary>Combines two filters into one that requires both (AND). </summary>
    public static IIncludeFilter And(IIncludeFilter a, IIncludeFilter b) {
        List<IIncludeFilter> all = [];
        if (a is CompositeIncludeFilter ca) all.AddRange(ca.Filters); else all.Add(a);
        if (b is CompositeIncludeFilter cb) all.AddRange(cb.Filters); else all.Add(b);
        return new CompositeIncludeFilter(all);
    }
}
