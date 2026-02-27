using Relatude.DB.Datamodels;
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
    var noObjects = ctx.Database.Count();//.Query<DemoArticle>().Count();
    return "Open. Total objects: " + noObjects.ToString("N0");
});
app.MapGet("/Del", (RelatudeDBContext ctx) => {

    ctx.Database.DeleteMany<DemoArticle>();

});


app.MapGet("/Test", (RelatudeDBContext ctx) => {

    var u = ctx.Database.Create<DemoArticle>();
    
    ctx.Database.Insert(u);

    var db = ctx.Database;
    db.UPSER
    


    // RelatudeDbRuntime - A static access to running server and default database
    // RelatudeDbEndpointContext - A transient context for the API request, access to db user query options ( typically used in a endpoint )

    // RelatudeCmsContext - A transient context for the page or any other request to the system. Part of Relatude CMS



    ////ctx = ctx.Culture("en").CultureFallbacks();

    //var dnAdminNo = db.NewContext(StireCintectec.Norsk);
    //var dnAdminNo=  ctx.Admin(db);

    //var dnAdminNo = db.NewContext<AdminContent>();








    //var articlesAdminNo = dnAdminNo.Query<DemoArticle>().Page(0, 10).Execute();

    //var articles = db.Query<DemoArticle>(ctx.Admin().Culture("no")).Page(0, 10).Execute();

    //var articles2 = db.Query<DemoArticle>(ctx.Culture("en")).Page(0, 10).Execute();

    //var articles2 = db.Query<DemoArticle>().Page(0, 10).Execute();



    //var dbCtxEng = db.AsContext(ctx.Admin().Culture("en"));

    //var dbCtxNor = db.AsContext(ctx.Admin().Culture("no"));

    //var articles = db.Query<DemoArticle>(ctx.Culture("no")).Page(0, 10).Execute();



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
