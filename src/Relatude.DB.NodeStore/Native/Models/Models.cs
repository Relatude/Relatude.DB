using Relatude.DB.Datamodels;
using Relatude.DB.Demo.Models;
using Relatude.DB.Nodes;
namespace Relatude.DB.Native.Models;

//public interface ISystemSettings {
//    Guid Id { get; set; }
//    [EmbeddedMapProperty(KeyProperty = nameof(ISystemSetting.Key))]
//    EmbeddedMap<string, ISystemSetting> DynamicSettings { get; }
//}
//public interface ISystemSetting {
//    Guid Id { get; set; }
//    string Key { get; set; }
//    string Value { get; set; }
//}

// Every property below pins its id explicitly. Without it the id is derived from the node type id and
// the member name, so renaming a property would silently give it a new id and orphan the values already
// stored - and the engine looks some of these ids up by constant (NativeModelStore).
[Node(Id = NodeConstants.BaseUserIdString, TextIndex = BoolValue.False, SemanticIndex = BoolValue.False)]
public interface ISystemUser {
    Guid Id { get; set; }
    [IntegerProperty(Id = NodeConstants.NativeUserPropertyUserTypeString)]
    SystemUserType UserType { get; set; }
    [RelationProperty(Id = NodeConstants.NativeUserPropertyMembershipsString)]
    UsersToGroups.Groups Memberships { get; }
}
[Node(Id = NodeConstants.BaseUserGroupIdString, TextIndex = BoolValue.False, SemanticIndex = BoolValue.False)]
public interface ISystemUserGroup {
    Guid Id { get; set; }
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
public interface ISystemCollection {
    Guid Id { get; set; }
    [StringProperty(Id = NodeConstants.NativeCollectionPropertyNameString)]
    string? Name { get; set; }
    [RelationProperty(Id = NodeConstants.NativeCollectionPropertyCulturesString)]
    CollectionsToCultures.Cultures Cultures { get; }
}
[Node(Id = NodeConstants.BaseCultureIdString, TextIndex = BoolValue.False, SemanticIndex = BoolValue.False)]
public interface ISystemCulture {
    Guid Id { get; set; }
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








