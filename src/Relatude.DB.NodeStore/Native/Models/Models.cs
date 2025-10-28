using Relatude.DB.Nodes;

namespace Relatude.DB.Native.Models;
public class SystemUserModel {
    public Guid Id { get; set; }
    public SystemUserType UserType { get; set; }
    public UserToGroup.Right Memberships { get; set; } = UserToGroup.Right.Empty;
}
public class SystemUserGroupModel {
    public Guid Id { get; set; }
    public string? GroupName { get; set; }
    public UserToGroup.Left UserMembers { get; set; } = UserToGroup.Left.Empty;
    public GroupToGroup.Right GroupMemberships { get; set; } = GroupToGroup.Right.Empty;
    public GroupToGroup.Left GroupMembers { get; set; } = GroupToGroup.Left.Empty;

}
public class SystemCollectionModel {
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public Guid[] Cultures { get; set; } = [];
}
public class SystemCultureModel {
    public Guid Id { get; set; }
    public string CultureCode { get; set; }
    public string NativeName { get; set; }
    public string EnglishName { get; set; }
}
public class UserToGroup : ManyToMany<SystemUserModel, SystemUserGroupModel, UserToGroup> { }
public class GroupToGroup : ManyToMany<SystemUserModel, SystemUserGroupModel, GroupToGroup> { }
