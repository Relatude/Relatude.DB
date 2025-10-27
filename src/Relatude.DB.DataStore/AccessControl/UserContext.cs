namespace Relatude.DB.DataStores;
public class UserContext { // Immutable
    public Guid UserId { get; }
    public Guid CollectionId { get; }
    public Guid CultureId { get; }
    public Guid[] Memberships { get; }
    

    //readonly static Guid _adminId = new("356f00bd-e2d0-4c37-9db6-8f6ad5790039");
    //public static UserContext Anonymous(int lcid, Guid? collectionId = null) => new(null, lcid, collectionId);
    //public static UserContext Authenticated(Guid userId, int lcid, Guid? collectionId = null, Guid[]? previewing = null) => new(userId, lcid, collectionId, previewing);
    //public static UserContext Admin(int lcid, Guid? collectionId = null, Guid[]? previewing = null) => new(_adminId, lcid, collectionId, previewing);
    //UserContext(Guid? userId, string lcid, Guid? collectionId = null, Guid[]? previewing = null) {
    //    UserId = userId;
    //    CollectionId = collectionId;
    //    Previewing = previewing;
    //    //Originals = originals;
    //}

    //// public bool Originals { get; }
    //public Guid[]? Previewing { get; }
    //public bool IsPreviewing() => Previewing?.Length > 0;
    //public bool IsAdmin() => UserId == _adminId;
    //public bool IsAuthenticated() => UserId != null;
}