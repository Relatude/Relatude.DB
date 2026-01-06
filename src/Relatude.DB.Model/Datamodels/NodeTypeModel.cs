using Relatude.DB.Datamodels.Properties;

namespace Relatude.DB.Datamodels;
public enum DataTypePublicId {
    Guid,
    String,
}
public enum ModelType {
    Interface,
    Class,
    Record,
    Struct,
}
public enum DataTypeInternalId {
    Int,
    Long,
    String,
}
public partial class NodeTypeModel { // with default values

    readonly public static string DefaultPublicIdPropertyName = "Id";
    readonly public static string DefaultInternalIdPropertyName = "Id";

    public Guid Id { get; set; }
    public bool IsInterface { get { return ModelType == ModelType.Interface; } }
    public bool CanInherit { get { return ModelType != ModelType.Struct; } }
    public ModelType ModelType { get; set; }

    public bool DefaultAdminAccess { get; set; }

    public bool AccessControl { get; set; }
    public bool Cultures { get; set; }
    public bool Collections { get; set; }
    public bool Revisions { get; set; }

    public string? NameOfPublicIdProperty { get; set; }
    public string? NameOfInternalIdProperty { get; set; }
    public DataTypePublicId? DataTypeOfPublicId { get; set; }//= DataTypePublicId.Guid;
    public DataTypeInternalId? DataTypeOfInternalId { get; set; }// = DataTypeInternalId.Int;

    public string? NameOfCollectionProperty { get; set; }
    public string? NameOfLCIDProperty { get; set; }
    public string? NameOfDerivedFromLCID { get; set; }
    public string? NameOfIsDerivedProperty { get; set; }
    public string? NameOfReadAccessProperty { get; set; }
    public string? NameOfWriteAccessProperty { get; set; }
    public string? NameOfCreatedUtcProperty { get; set; }
    public string? NameOfChangedUtcProperty { get; set; }

    public List<Guid> Parents { get; set; } = new();
    public int MinNoInstances { get; set; } = int.MinValue;
    public int MaxNoInstances { get; set; } = int.MaxValue;
    public string? Namespace { get; set; } = string.Empty;
    public string CodeName { get; set; } = string.Empty;
    public bool? InstantTextIndexing { get; set; }
    public bool? TextIndex { get; set; }
    public double TextIndexBoost { get; set; } = 0;
    public bool? SemanticIndex { get; set; }
    public Dictionary<Guid, PropertyModel> Properties { get; } = new();
    public string FullName => String.IsNullOrEmpty(Namespace) ? CodeName : $"{Namespace}.{CodeName}";
}
