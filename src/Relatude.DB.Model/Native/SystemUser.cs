namespace Relatude.DB.Native;
public class SystemUser {
    public Guid Id { get; set; }
    public SystemUserType UserType { get; set; } = SystemUserType.Anonymous;
    public Guid[] Memberships { get; set; } = [];
}
public class SystemUserGroup {
    public Guid Id { get; set; }
    public string? GroupName { get; set; }
    public Guid[] Members { get; set; } = [];
    public Guid[] Memberships { get; set; } = [];
}
public enum SystemUserType {
    Anonymous,
    System,
    Admin,
}
public class SystemCollection {
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public Guid[] Cultures { get; set; } = [];
}
public class SystemCulture {
    public Guid Id { get; set; }
    public string CultureCode{ get; set; }
    public string NativeName { get; set; }
    public string EnglishName { get; set; }
}