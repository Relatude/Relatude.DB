namespace WAF.Datamodels;
public static class NodeConstants {
    const string BaseNodeTypeIdString = "ac6515ae-3ca5-43fa-8045-7a5a1bb11830";
    const string TextIndexPropertyIdString = "b835577e-84a2-4fa3-a850-44ab2112e6cf";
    const string VectorIndexPropertyIdString = "1e282f9f-3bd2-4230-abcb-f9e840145159";
    public static readonly Guid BaseNodeTypeId = new(BaseNodeTypeIdString);
    public static readonly Guid SystemTextIndexPropertyId = new(TextIndexPropertyIdString);
    public static readonly Guid SystemVectorIndexPropertyId = new(VectorIndexPropertyIdString);
    public static readonly string SystemTextIndexPropertyName = "_textIndex";
    public static readonly string SystemVectorIndexPropertyName = "_vectorIndex";
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
