namespace Relatude.DB.Datamodels;

public static class NodeConstants {

    public static readonly Guid BaseNodeTypeId = new("ac6515ae-3ca5-43fa-8045-7a5a1bb11830");
    public static readonly Guid SystemTextIndexPropertyId = new("b835577e-84a2-4fa3-a850-44ab2112e6cf");
    public static readonly Guid SystemVectorIndexPropertyId = new("1e282f9f-3bd2-4230-abcb-f9e840145159");

    public static readonly Guid SystemAddressPropertyId = new("57c752bf-e364-43e1-9163-d8ffea004bad");
    public static readonly Guid SystemAutoAddressPropertyId = new("cf885adf-1121-41d8-85e6-70c553345dd0");
    public static readonly Guid SystemDisplayNamePropertyId = new("c1ea2c8a-dbe8-4fa0-a020-ae05507305b6");


    public static readonly Guid MasterAdminUserId = Guid.Parse("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF");

    public static readonly Guid UserGroupAdmins = Guid.Parse("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF");
    public static readonly Guid UserGroupEveryone = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid UserGroupMember = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid UserGroupUnspecified = Guid.Empty;

    public static readonly string SystemTextIndexPropertyName = "_textIndex";
    public static readonly string SystemVectorIndexPropertyName = "_vectorIndex";
    public static readonly string SystemAddressPropertyName = "_address";
    public static readonly string SystemAutoAddressPropertyName = "_autoAddress";
    public static readonly string SystemDisplayNamePropertyName = "_displayName";

    public const string BaseUserIdString = "243f1514-46c3-4106-9c6a-4a25fb39238b";
    public const string BaseUserGroupIdString = "afd3b9e4-7565-49ae-ac3b-ed20b5ccfe6a";
    public const string BaseCollectionIdString = "be94c359-2b08-4f58-b116-bb5fef89a5cc";
    public const string BaseCultureIdString = "f51d3f3a-08d4-4b56-a00b-464e037f0009";
    public static readonly Guid BaseUserId = new(BaseUserIdString);
    public static readonly Guid BaseUserGroupId = new(BaseUserGroupIdString);
    public static readonly Guid BaseCollectionId = new(BaseCollectionIdString);
    public static readonly Guid BaseCultureId = new(BaseCultureIdString);

    public const string RelationUsersToGroupsString = "f161bb73-5434-4dd4-a7b4-558a12412ca6";
    public const string RelationGroupsToGroupsString = "df8e846d-d3e5-41a1-806e-fcd8159d1396";
    public const string RelationCollectionsToCulturesString = "39f5e3e6-56d3-4d63-8703-1eb0b8e75861";
    public static readonly Guid RelationUsersToGroups = new(RelationUsersToGroupsString);
    public static readonly Guid RelationGroupsToGroups = new(RelationGroupsToGroupsString);
    public static readonly Guid RelationCollectionsToCultures = new(RelationCollectionsToCulturesString);

    // Property ids of the engine's own model (Relatude.DB.Native.Models). They are pinned with an
    // explicit Id on every property there, so renaming a property does not change its id and orphan
    // the stored values. The values are the ids the properties already had when they were generated
    // from the type and member name, so pinning them changes no existing data.
    public const string NativeUserPropertyUserTypeString = "4f64452a-7dbc-f83f-ade1-c265a040b423";
    public const string NativeUserPropertyMembershipsString = "d476891e-f1d0-f541-283c-4abf258da8bd";

    public const string NativeUserGroupPropertyGroupNameString = "49fd9384-5f68-6aca-9472-92640550e9e2";
    public const string NativeUserGroupPropertyUserMembersString = "c845e4b4-7e1d-1991-c922-25f00eb5a5a2";
    public const string NativeUserGroupPropertyGroupMembershipsString = "6cf54ce8-ed2c-c7db-15c6-406c5a8810e3";
    public const string NativeUserGroupPropertyGroupMembersString = "d5d61b7f-9d28-c98e-4128-d476a5e0fa25";

    public const string NativeCollectionPropertyNameString = "decb9830-0b7f-c9cb-69a6-fe2b24f7a647";
    public const string NativeCollectionPropertyCulturesString = "9f38e583-0c9e-6e95-a42f-ca2f0ca1fbc7";

    public const string NativeCulturePropertyCultureCodeString = "f97c08b8-b851-fe8a-97cd-9b1dbec99f36";
    public const string NativeCulturePropertyNativeNameString = "bd210a8a-c007-1ff8-a807-050617ac98da";
    public const string NativeCulturePropertyEnglishNameString = "d9ddd7ab-5d21-f46e-2dea-dcd6d5abac97";
    public const string NativeCulturePropertyCollectionsString = "0f7523f9-ceac-32d1-8469-3ee204c91c05";

    public static readonly Guid NativeUserPropertyUserType = new(NativeUserPropertyUserTypeString);
    public static readonly Guid NativeCulturePropertyCultureCode = new(NativeCulturePropertyCultureCodeString);


}
