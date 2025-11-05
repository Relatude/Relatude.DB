namespace Relatude.DB.Native;
public enum SystemUserType {
    Anonymous = 0, // unauthenticated user
    System = 1, // authenticated user part of the system
    Admin = 2, // authenticated user with access to everything
}
public class NativeSystemUser {
    public int Id;
    public SystemUserType UserType;
    public int[] Memberships = [];
}
public class NativeSystemUserGroup {
    public int Id;
    public HashSet<int> UserMembers = [];
    public int[] GroupMembers = [];
    public int[] GroupMemberships = [];
}
public class NativeSystemCollection {
    public int Id;
    public int[] Cultures = [];
}
public class NativeSystemCulture {
    public int Id;
    public string CultureCode = string.Empty;
    public int[] Collections = [];
}
public enum NativeNodeType {
    NotRelevant,
    SystemUser,
    SystemUserGroup,
    SystemCulture,
    Collection
}
public enum NativeRelationType {
    NotRelevant,
    UsersToGroups,
    GroupsToGroups,
    CollectionsToCultures
}
