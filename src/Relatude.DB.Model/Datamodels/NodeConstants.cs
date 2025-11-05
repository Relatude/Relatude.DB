namespace Relatude.DB.Datamodels;
public static class NodeConstants {
    const string BaseNodeTypeIdString = "ac6515ae-3ca5-43fa-8045-7a5a1bb11830";
    const string TextIndexPropertyIdString = "b835577e-84a2-4fa3-a850-44ab2112e6cf";
    const string VectorIndexPropertyIdString = "1e282f9f-3bd2-4230-abcb-f9e840145159";
    public static readonly Guid BaseNodeTypeId = new(BaseNodeTypeIdString);
    public static readonly Guid SystemTextIndexPropertyId = new(TextIndexPropertyIdString);
    public static readonly Guid SystemVectorIndexPropertyId = new(VectorIndexPropertyIdString);
    public static readonly string SystemTextIndexPropertyName = "_textIndex";
    public static readonly string SystemVectorIndexPropertyName = "_vectorIndex";

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

    const string NativeUserPropertyUserTypeString = "61bfa8ff-e8af-47d4-86e3-0b3f82510896";
    public static readonly Guid NativeUserPropertyUserType = new(NativeUserPropertyUserTypeString);

    const string NativeCulturePropertyCultureCodeString = "95dc7b8b-ac72-4759-9de1-9f81795090c2"; 
    public static readonly Guid NativeCulturePropertyCultureCode = new(NativeCulturePropertyCultureCodeString);


}
//public enum Revision {
//    Live = 0,
//    Approval = -2, // awaiting approval
//    Preliminary = -1,
//    Archived = 1, // rev > 0 && rev < 1000
//    Deleted = 2, // rev >= 1000
//}
//public enum RevisionState : int {
//    Archived = 1,
//    Published = 0,
//    Preliminary = -1,
//    Derived = -3,
//    Deleted = -4,
//    Variant = 10000,
//}
//public enum RevisionStateRequest : int {
//    None = 0,
//    AwaitingDeleteApproval = -1,
//    AwaitingPublicationApproval = -2,
//    AwaitingUnPublicationApproval = -3,
//}
