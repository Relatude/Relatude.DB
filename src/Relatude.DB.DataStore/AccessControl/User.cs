
namespace Relatude.DB.AccessControl;
internal class User {
    public int __Id { get; set; }
    public Guid Id { get; set; }
    public UserType UserType { get; set; } = UserType.Anonymous;
    public int[] Memberships { get; set; } = [];
}
