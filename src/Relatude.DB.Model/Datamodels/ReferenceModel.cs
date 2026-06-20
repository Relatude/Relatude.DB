namespace Relatude.DB.Datamodels;
public class ReferenceModel {
    public Guid Id { get; set; }
    public string? Namespace { get; set; } = string.Empty;
    public string CodeName { get; set; } = string.Empty;
    public string FullName()=> string.IsNullOrEmpty(Namespace) ? CodeName : Namespace + "." + CodeName;
    public List<Guid> TargetTypes { get; set; } = new();
    public bool CultureSpecific { get; set; }
    override public string ToString() => string.IsNullOrEmpty(Namespace) ? CodeName : Namespace + "." + CodeName;
}
