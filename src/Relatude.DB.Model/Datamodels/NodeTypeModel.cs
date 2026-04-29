using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Native;

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
    public bool IsInnerNode { get; set; } = false;

    public bool Hidden { get; set; } = false;

    public Guid DefaultReadAccess { get; set; } = NodeConstants.UserGroupUnspecified;
    public Guid DefaultEditAccess { get; set; } = NodeConstants.UserGroupUnspecified;
    public Guid DefaultEditViewAccess { get; set; } = NodeConstants.UserGroupUnspecified;
    public Guid DefaultPublishAccess { get; set; } = NodeConstants.UserGroupUnspecified;

    public ModelType ModelType { get; set; }

    public string? NameOfPublicIdProperty { get; set; }
    public string? NameOfInternalIdProperty { get; set; }
    public DataTypePublicId? DataTypeOfPublicId { get; set; }
    public DataTypeInternalId? DataTypeOfInternalId { get; set; }

    public string? NameOfMetaProperty { get; set; }

    public string? NameOfCreatedUtcProperty { get; set; }
    public string? NameOfChangedUtcProperty { get; set; }

    public string? NameOfDisplayNameProperty { get; set; }
    public string? NameOfAddressProperty { get; set; }

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
