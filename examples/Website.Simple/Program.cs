using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Stores;
using Relatude.DB.Demo.Models;
using Relatude.DB.Native.Models;
using Relatude.DB.NodeServer;
using Relatude.DB.Query;
using System.Diagnostics;

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

    var db2 = ctx.Database.Context.Culture("en-US").Create();

});

app.MapGet("/cult", (RelatudeDBContext ctx) => {
    var db = ctx.Database;


    var culture = db.Create<ISystemCulture>();
    culture.CultureCode = "en-US";
    db.Insert(culture);
    var c = db.CreateAndInsert<ISystemCulture>(c => {
        c.CultureCode = "no-NO";
    });

});
app.MapGet("/Test2", (RelatudeDBContext ctx, HttpContext httpCtx) => {

    var db = ctx.Database;

    db = ctx.Database.Context.Culture("en-US").Hidden(true).CultureFallbacks(true).Create();

    db.Insert(new DemoArticle() {
        Title = "English article",
    }, out var id1, "en-US", RevisionType.Published);

    db.CreateRevision(id1, Guid.Empty, RevisionType.Published, out var rId1, "no-NO");
    db.UpdateMeta(id1, rId1, nameof(NodeMeta.EditAccess), DateTime.UtcNow.AddHours(1));
    var revision = db.GetRevisions<DemoArticle>(id1);

    var aNO = db.Get<DemoArticle>(id1);

    foreach (var rev in revision) {
        var meta = rev.Meta;
        var node = rev.Node;

    }

    db.Query<DemoArticle>()
    .WhereCulture("en-Us")
    .WhereCultureFallback(false)
    .WhereHidden(false).Execute();



    var html = new System.Text.StringBuilder();

    db.Insert(new DemoArticle() {
        Title = "Norwegian article",
    }, out var id, "no-NO", RevisionType.Preliminary);


    db.UpdateMeta(id, nameof(NodeMeta.CultureId), Guid.Empty);

    db.EnableRevisions(id, out var rid);

    db.ChangeRevisionCulture(id, rid, "no-NO");

    html.AppendLine("<h1>Article created with no culture, then culture set to no-NO</h1>");

    return html.ToString();
});
app.MapGet("/ttt", (RelatudeDBContext ctx, HttpContext httpCtx) => {
    var cIsd = Guid.NewGuid();
    var now = DateTime.UtcNow;
    var ctx1 = new InnerNodeMetaFull(
        10,
        cIsd,
        cIsd,
        cIsd,
        cIsd, cIsd,
        false,
        true,
        cIsd,
        cIsd,
        cIsd,
        now,
        now);
    var ctx2 = new InnerNodeMetaFull(
        10,
        cIsd,
        cIsd,
        cIsd,
        cIsd, cIsd,
        false,
        true,
        cIsd,
        cIsd,
        cIsd,
        now,
        now);




    var set = new HashSet<metaAndType>();
    var typeId = Guid.NewGuid();
    var mt1 = new metaAndType(ctx1, typeId);
    var mt2 = new metaAndType(ctx2, typeId);
    set.Add(mt1);
    set.Add(mt2);

    return mt1.Equals(mt2).ToString() + " - " + set.Count.ToString() + " - " + mt1.GetHashCode() + " - " + mt2.GetHashCode();
});
app.MapGet("/Test", (RelatudeDBContext ctx, HttpContext httpCtx) => {
    try {
        var sw = Stopwatch.StartNew();
        var html = new System.Text.StringBuilder();
        var iterations = 1000;
        for (var i = 0; i < iterations; i++) {

            html = new System.Text.StringBuilder();
            var db = ctx.Database;

            var article = db.Create<DemoArticle>();
            db.Insert(article);
            var rId = Guid.NewGuid();
            db.EnableRevisions(article.Id, rId);
            db.ChangeRevisionCulture(article.Id, rId, "en-US");

            //db.CreateRevision(article.Id, rId, RevisionType.Published);
            //db.CreateRevision(article.Id, rId, RevisionType.Published);
            db.CreateRevision(article.Id, rId, RevisionType.Preliminary);
            db.CreateRevision(article.Id, rId, RevisionType.Preliminary);
            var r2 = Guid.NewGuid();
            db.CreateRevision(article.Id, rId, RevisionType.Preliminary, r2);

            db.ChangeRevisionType(article.Id, r2, RevisionType.Archived);
            db.DeleteRevision(article.Id, r2);

            var r3 = Guid.NewGuid();
            db.CreateRevision(article.Id, rId, RevisionType.AwaitingPublicationApproval, r3, "no-NO");

            //db.DisableRevisions(article.Id, r3);

            //db.UpdateMeta(article.Id, nameof(NodeMeta.EditAccess), Guid.NewGuid());
            db.UpdateMeta(article.Id, r3, nameof(NodeMeta.Hidden), true);
            //db.UpdateMeta(article.Id, r3, nameof(NodeMeta.Hidden), false);


            var revisions = db.GetRevisions<DemoArticle>(article.Id);

            // creating HTML table of revisions:
            html.AppendLine("<table><tr>");
            html.Append($"<th>Revision ID</th>");
            html.Append($"<th>Revision Type</th>");
            html.Append($"<th>Culture</th>");
            html.Append($"<th>EditAccess UTC</th>");
            html.Append($"<th>Hidden</th>");
            html.Append("</tr>");
            foreach (var rev in revisions) {
                html.Append($"<tr>");
                html.Append($"<td>{rev.Meta.RevisionId}</td>");
                html.Append($"<td>{rev.Meta.RevisionType}</td>");
                html.Append($"<td>{rev.Meta.CultureId}</td>");
                html.Append($"<td>{rev.Meta.EditAccess}</td>");
                html.Append($"<td>{rev.Meta.Hidden}</td>");
                html.Append($"</tr>");
            }
            html.AppendLine("</table>");

            var noObjects = db.Count();

            httpCtx.Response.ContentType = "text/html";

            var dbNone = ctx.Database.Context.Culture(null).Create();
            var dbUs = ctx.Database.Context.Culture("en-US").Create();
            var dbNo = ctx.Database.Context.Culture("no-NO").Create();
            var dbFallbacks = ctx.Database.Context.CultureFallbacks(true).Hidden(true).Create();

            html.AppendLine("<br/>Count in no culture: " + dbNone.Query<DemoArticle>().Count());
            html.AppendLine("<br/>Count in en-US: " + dbUs.Query<DemoArticle>().Count());
            html.AppendLine("<br/>Count in no-NO: " + dbNo.Query<DemoArticle>().Count());
            html.AppendLine("<br/>Count in fallbacks: " + dbFallbacks.Query<DemoArticle>().Count());

            html.AppendLine("<br/>Count in no culture: " + db.Query<DemoArticle>().WhereCulture(null).Count());
            html.AppendLine("<br/>Count in en-US: " + db.Query<DemoArticle>().WhereCulture("en-US").Count());
            html.AppendLine("<br/>Count in no-NO: " + db.Query<DemoArticle>().WhereCulture("no-NO").Count());
            html.AppendLine("<br/>Count in fallbacks: " + db.Query<DemoArticle>().WhereCultureFallback(true).WhereHidden(true).Count());
            sw.Stop();

            html.AppendLine("<h1>Test time per iteration: " + (sw.Elapsed.TotalMilliseconds / iterations).ToString("F2") + " ms</h1>");
            html.AppendLine("<h1>Test time total: " + sw.Elapsed.TotalMilliseconds.ToString("F2") + " ms</h1>");
        }

        return html.ToString();

    } catch (Exception ex) {
        return "Error: " + ex.Message;
    }
    //db.CreateRevision(u.Id, Guid.NewGuid(), rId, RevisionType.Published);
    //db.UPSER



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
