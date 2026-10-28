using Relatude.DB.Datamodels;
using Relatude.DB.Nodes;
namespace Relatude.DB.Native.Models;

// Every property below pins its id explicitly. Without it the id is derived from the node type id and
// the member name, so renaming a property would silently give it a new id and orphan the values already
// stored - and the engine looks some of these ids up by constant (NativeModelStore).


[Node(Id = "38796991-1c9b-4718-bc95-33281ab1beae")]
public interface IId {
    public Guid Id { get; set; }
}
[Node(Id = "43d71b39-09e7-497c-9095-60e9623806ea")]
public interface IMeta {
    public NodeMeta Meta { get; }
}
[Node(Id = "643071df-8b38-4ad2-9940-aae8282f032e", NameProperty = nameof(Name))]
public interface IName{
    [StringProperty(Id = NodeConstants.NativeCollectionPropertyNameString, Indexed = true)]
    public string Name { get; set; }
}

[Node(Id = NodeConstants.BaseUserIdString, TextIndex = BoolValue.False, SemanticIndex = BoolValue.False)]
public interface ISystemUser: IId {    
    [IntegerProperty(Id = NodeConstants.NativeUserPropertyUserTypeString)]
    SystemUserType UserType { get; set; }
    [RelationProperty(Id = NodeConstants.NativeUserPropertyMembershipsString)]
    UsersToGroups.Groups Memberships { get; }
}
[Node(Id = NodeConstants.BaseUserGroupIdString, TextIndex = BoolValue.False, SemanticIndex = BoolValue.False)]
public interface ISystemUserGroup : IId {
    [StringProperty(Id = NodeConstants.NativeUserGroupPropertyGroupNameString)]
    string GroupName { get; set; }
    [RelationProperty(Id = NodeConstants.NativeUserGroupPropertyUserMembersString)]
    UsersToGroups.Users UserMembers { get; }
    [RelationProperty(Id = NodeConstants.NativeUserGroupPropertyGroupMembershipsString)]
    GroupsToGroups.Memberships GroupMemberships { get; }
    [RelationProperty(Id = NodeConstants.NativeUserGroupPropertyGroupMembersString)]
    GroupsToGroups.Members GroupMembers { get; }
}
[Node(Id = NodeConstants.BaseCollectionIdString, TextIndex = BoolValue.False, SemanticIndex = BoolValue.False)]
public interface ISystemCollection : IId, IName {
    [RelationProperty(Id = NodeConstants.NativeCollectionPropertyCulturesString)]
    CollectionsToCultures.Cultures Cultures { get; }
}
[Node(Id = NodeConstants.BaseCultureIdString, TextIndex = BoolValue.False, SemanticIndex = BoolValue.False)]
public interface ISystemCulture : IId {    
    [StringProperty(Id = NodeConstants.NativeCulturePropertyCultureCodeString, UniqueValues = true)]
    string CultureCode { get; set; }
    [StringProperty(Id = NodeConstants.NativeCulturePropertyNativeNameString)]
    string NativeName { get; set; }
    [StringProperty(Id = NodeConstants.NativeCulturePropertyEnglishNameString)]
    string EnglishName { get; set; }
    [RelationProperty(Id = NodeConstants.NativeCulturePropertyCollectionsString)]
    CollectionsToCultures.Collections Collections { get; }
}
[Relation(Id = NodeConstants.RelationUsersToGroupsString)]
public class UsersToGroups : ManyToMany<ISystemUser, ISystemUserGroup> {
    public class Users : ManyFrom { }
    public class Groups : ManyTo { }
}
[Relation(Id = NodeConstants.RelationGroupsToGroupsString, DisallowCircularReferences = true)]
public class GroupsToGroups : ManyToMany<ISystemUserGroup, ISystemUserGroup> {
    public class Memberships : ManyFrom { }
    public class Members : ManyTo { }
}
[Relation(Id = NodeConstants.RelationCollectionsToCulturesString)]
public class CollectionsToCultures : ManyToMany<ISystemCollection, ISystemCulture> {
    public class Collections : ManyFrom { }
    public class Cultures : ManyTo { }
}
[Exclude]
public class SystemCulture {
    public SystemCulture(Guid id, string code) {
        if (string.IsNullOrWhiteSpace(code)) {
            throw new ArgumentException("Culture code cannot be null or whitespace.", nameof(code));
        }
        if (id == Guid.Empty) {
            throw new ArgumentException("Id cannot be empty.", nameof(id));
        }
        Id = id;
        Code = code;
    }
    public Guid Id { get; }
    public string Code { get; }
}








