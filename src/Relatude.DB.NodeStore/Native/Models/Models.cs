using Relatude.DB.Nodes;

namespace Relatude.DB.Native.Models;
public class SystemUserModel {
    public Guid Id { get; set; }
    public SystemUserType UserType { get; set; }
    public UsersToGroups.Right Memberships { get; set; } = UsersToGroups.Right.Empty;
}
public class SystemUserGroupModel {
    public Guid Id { get; set; }
    public string? GroupName { get; set; }
    public UsersToGroups.Left UserMembers { get; set; } = UsersToGroups.Left.Empty;
    public GroupsToGroups.Right GroupMemberships { get; set; } = GroupsToGroups.Right.Empty;
    public GroupsToGroups.Left GroupMembers { get; set; } = GroupsToGroups.Left.Empty;

}
public class SystemCollectionModel {
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public Guid[] Cultures { get; set; } = [];
}
public class SystemCultureModel {
    public Guid Id { get; set; }
    public string CultureCode { get; set; } = string.Empty;
    public string NativeName { get; set; } = string.Empty;
    public string EnglishName { get; set; } = string.Empty;
}
public class UsersToGroups : ManyToMany<SystemUserModel, SystemUserGroupModel, UsersToGroups> {
}
public class GroupsToGroups : ManyToMany<SystemUserModel, SystemUserGroupModel, GroupsToGroups> { }
