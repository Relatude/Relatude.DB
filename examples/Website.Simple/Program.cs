using Relatude.DB.Demo.Models;
using Relatude.DB.Native.Models;
using Relatude.DB.NodeServer;

var builder = WebApplication.CreateBuilder(args);
builder.AddRelatudeDB();

builder.Services.AddCors(options => {
    options.AddPolicy(name: "AllowALL", builder => {
        builder.AllowAnyHeader().AllowAnyMethod().AllowCredentials().SetIsOriginAllowed(origin => true);
    });
});

var app = builder.Build();

app.UseCors("AllowALL");

app.MapGet("/", (RelatudeDBContext ctx) => {
    var noObjects = ctx.Database.Query<DemoArticle>().Count();
    return "Open. Total objects: " + noObjects.ToString("N0");
});
app.MapGet("/Del", (RelatudeDBContext ctx) => {

    ctx.Database.DeleteMany<DemoArticle>();

});
app.MapGet("/Add", (RelatudeDBContext ctx) => {
    var db = ctx.Database;
    var transaction = db.CreateTransaction();
    var culture = db.Create<ISystemCulture>();

    transaction.Insert(culture);
    for (int i = 0; i < 10; i++) {
        var collection = db.Create<ISystemCollection>();
        transaction.Insert(collection);
        transaction.Relate(collection, c => c.Cultures, culture);
    }

    var userGroup1 = db.Create<ISystemUserGroup>();
    transaction.Insert(userGroup1);

    var user10 = db.CreateAndInsert<ISystemUser>((u, t) => {
        u.Memberships.Relate(userGroup1, t);
    });

    var userGroup2 = db.Create<ISystemUserGroup>();
    transaction.Insert(userGroup2);

    var userGroup3 = db.Create<ISystemUserGroup>();
    transaction.Insert(userGroup3);

    transaction.Relate(userGroup2, u => u.GroupMembers, userGroup1);
    ISystemUser u = db.Create<ISystemUser>();

    transaction.Relation.Relate<UsersToGroups, ISystemUser, ISystemUserGroup>(u, userGroup3);



    for (int i = 0; i < 100; i++) {
        var user = db.Create<ISystemUser>();
        transaction.Insert(user);
        transaction.Relate(user, u => u.Memberships, userGroup1);
    }

    transaction.Execute();

});

app.UseRelatudeDB();

app.Run();
