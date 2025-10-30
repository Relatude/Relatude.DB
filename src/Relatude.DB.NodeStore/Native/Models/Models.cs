using Relatude.DB.Nodes;


namespace Relatude.DB.Native.Models;

public interface ISystemUser {
    Guid Id { get; set; }
    SystemUserType UserType { get; set; }
    UsersToGroups.Groups Memberships { get; set; }
}
public interface ISystemUserGroup {
    Guid Id { get; set; }
    string GroupName { get; set; }
    UsersToGroups.Users UserMembers { get; set; }
    GroupsToGroups.Memberships GroupMemberships { get; set; }
    GroupsToGroups.Members GroupMembers { get; set; }
}
public interface ISystemCollection {
    Guid Id { get; set; }
    string? Name { get; set; }
    CollectionsToCultures.Cultures Cultures { get; set; }
}
public interface ISystemCulture {
    Guid Id { get; set; }
    string CultureCode { get; set; }
    string NativeName { get; set; }
    string EnglishName { get; set; }
    CollectionsToCultures.Collections Collections { get; set; }
}

public class SystemUser : ISystemUser {
    public Guid Id { get; set; }
    public SystemUserType UserType { get; set; }
    public UsersToGroups.Groups Memberships { get; set; } = new();
}
public class SystemUserGroup : ISystemUserGroup {
    public Guid Id { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public UsersToGroups.Users UserMembers { get; set; } = new();
    public GroupsToGroups.Memberships GroupMemberships { get; set; } = new();
    public GroupsToGroups.Members GroupMembers { get; set; } = new();
}
public class SystemCollection : ISystemCollection {
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public CollectionsToCultures.Cultures Cultures { get; set; } = new();
}
public class SystemCulture : ISystemCulture {
    public Guid Id { get; set; }
    public string CultureCode { get; set; } = string.Empty;
    public string NativeName { get; set; } = string.Empty;
    public string EnglishName { get; set; } = string.Empty;
    public CollectionsToCultures.Collections Collections { get; set; } = new();
}

public class UsersToGroups : ManyToMany<ISystemUser, ISystemUserGroup> {
    public class Users : ManyFrom { }
    public class Groups : ManyTo { }
}
public class GroupsToGroups : ManyToMany<ISystemUser, ISystemUserGroup> {
    public class Memberships : ManyFrom { }
    public class Members : ManyTo { }
}
public class CollectionsToCultures : ManyToMany<ISystemCollection, ISystemCulture> {
    public class Collections : ManyFrom { }
    public class Cultures : ManyTo { }
}









