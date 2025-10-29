using Relatude.DB.Nodes;

namespace Relatude.DB.Native.Models;
public class SystemUser {
    public Guid Id { get; set; }
    public SystemUserType UserType { get; set; }
    public UsersToGroups.Groups Memberships { get; set; } = new();
}
public class SystemUserGroup {
    public Guid Id { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public UsersToGroups.Users UserMembers { get; set; } = new();
    public GroupsToGroups.Memberships GroupMemberships { get; set; } = new();
    public GroupsToGroups.Members GroupMembers { get; set; } = new();
}
public class SystemCollection {
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public CollectionsToCultures.Cultures Cultures { get; set; } = new();
}
public class SystemCulture {
    public Guid Id { get; set; }
    public string CultureCode { get; set; } = string.Empty;
    public string NativeName { get; set; } = string.Empty;
    public string EnglishName { get; set; } = string.Empty;
    public CollectionsToCultures.Collections Collections { get; set; } = new();
}
public class UsersToGroups
    : ManyToMany<SystemUser, SystemUserGroup> {
    public class Users : Many1 { }
    public class Groups : Many2 { }
}
public class GroupsToGroups
    : ManyToMany<SystemUser, SystemUserGroup> {
    public class Memberships : Many1 { }
    public class Members : Many2 { }
}
public class CollectionsToCultures
    : ManyToMany<SystemCollection, SystemCulture> {
    public class Collections : Many1 { }
    public class Cultures : Many2 { }
}









