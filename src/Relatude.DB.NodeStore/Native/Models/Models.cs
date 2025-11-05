using Relatude.DB.Datamodels;
using Relatude.DB.Nodes;
namespace Relatude.DB.Native.Models;

[Node(Id = NodeConstants.BaseUserIdString, TextIndex = BoolValue.False, SemanticIndex = BoolValue.False)]
public interface ISystemUser {
    Guid Id { get; set; }
    SystemUserType UserType { get; set; }
    UsersToGroups.Groups Memberships { get; }
}
[Node(Id = NodeConstants.BaseUserGroupIdString, TextIndex = BoolValue.False, SemanticIndex = BoolValue.False)]
public interface ISystemUserGroup {
    Guid Id { get; set; }
    string GroupName { get; set; }
    UsersToGroups.Users UserMembers { get; }
    GroupsToGroups.Memberships GroupMemberships { get; }
    GroupsToGroups.Members GroupMembers { get; }
}
[Node(Id = NodeConstants.BaseCollectionIdString, TextIndex = BoolValue.False, SemanticIndex = BoolValue.False)]
public interface ISystemCollection {
    Guid Id { get; set; }
    string? Name { get; set; }
    CollectionsToCultures.Cultures Cultures { get; }
}
[Node(Id = NodeConstants.BaseCultureIdString, TextIndex = BoolValue.False, SemanticIndex = BoolValue.False)]
public interface ISystemCulture {
    Guid Id { get; set; }
    string CultureCode { get; set; }
    string NativeName { get; set; }
    string EnglishName { get; set; }
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









