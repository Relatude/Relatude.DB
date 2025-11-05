using Relatude.DB.Demo.Models;
using Relatude.DB.Native.Models;
using Relatude.DB.Nodes;
using Relatude.DB.NodeServer;
using static Lucene.Net.Documents.Field;

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
    var noObjects = ctx.Database.Query<DemoArticle>().Execute().Count;
    return "Open. Total objects: " + noObjects.ToString("N0");
});
app.MapGet("/Del", (NodeStore db) => {

    var sys = db.Create<ISystemUser>();

});
app.MapGet("/Add", (NodeStore db) => {

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

    var userGroup2 = db.Create<ISystemUserGroup>();
    transaction.Insert(userGroup2);

    var userGroup3 = db.Create<ISystemUserGroup>();
    transaction.Insert(userGroup3);

    transaction.Relate(userGroup2, u => u.GroupMembers, userGroup1);
    transaction.Relate(userGroup3, u => u.GroupMembers, userGroup2);


    for (int i = 0; i < 10000; i++) {
        var user = db.Create<ISystemUser>();
        transaction.Insert(user);
        transaction.Relate(user, u => u.Memberships, userGroup1);
    }

    transaction.Execute();






});

app.UseRelatudeDB();

app.Run();
