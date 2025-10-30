namespace Relatude.DB.Native;
public enum SystemUserType {
    Anonymous = 0, // unauthenticated user
    System = 1, // authenticated user part of the system
    Admin = 2, // authenticated user with access to everything
}
public class NativeSystemUser {
    public Guid Id { get; set; }
    public SystemUserType UserType { get; set; } = SystemUserType.Anonymous;
    public Guid[] Memberships { get; set; } = [];
}
public class NativeSystemUserGroup {
    public Guid Id { get; set; }
    public string? GroupName { get; set; }
    public Guid[] Members { get; set; } = [];
    public Guid[] Memberships { get; set; } = [];
}
public class NativeSystemCollection {
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public Guid[] Cultures { get; set; } = [];
}
public class NativeSystemCulture {
    public Guid Id { get; set; }
    public string CultureCode{ get; set; } = string.Empty;
    public string NativeName { get; set; } = string.Empty;
    public string EnglishName { get; set; } = string.Empty;
}
public class NativeModelStore {
    public Dictionary<Guid, NativeSystemUser> SystemUsers { get; set; } = [];
    public Dictionary<Guid, NativeSystemUserGroup> SystemUserGroups { get; set; } = [];
    public Dictionary<Guid, NativeSystemCollection> SystemCollections { get; set; } = [];
    public Dictionary<Guid, NativeSystemCulture> SystemCultures { get; set; } = [];
}