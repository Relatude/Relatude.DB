
namespace Relatude.DB.AccessControl;
internal class UserGroup{
    public int __Id { get; set; }
    public Guid Id { get; set; }
    public string? GroupName { get; set; } 
    public int[] Members { get; set; } = [];
    public int[] Memberships { get; set; } = [];
}
