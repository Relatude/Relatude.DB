using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Native;
using Relatude.DB.Native.Models;
using Relatude.DB.Nodes;

namespace Relatude.Store;

#region test datamodel
// InstantTextIndexing so the search based tests can query right after the insert.
[Node(TextIndex = BoolValue.True, InstantTextIndexing = BoolValue.True)]
public class SecuredPage {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty(DisplayName = true, Indexed = true)]
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public SecuredPage? Parent { get; set; }
    public IEnumerable<SecuredPage> Children { get; set; } = [];
}
#endregion

/// <summary>
/// Access control through node meta data: the read access, edit view access, deleted flag, release and
/// expire dates and collection id stored in <see cref="IInnerNodeMeta"/> decide which nodes a query
/// returns for a given <see cref="QueryContext"/>. The filtering itself lives in
/// NodeTypesByIds.isMetaRelevantForContext (Relatude.DB.DataStoreLocal), and every query that starts from
/// "all nodes of a type" passes through it.
/// <para>
/// These tests cover what works today. The parts that do not are in
/// <see cref="NodeMetaAccessKnownIssuesTests"/> below, one failing test per defect.
/// </para>
/// </summary>
[TestClass]
public class NodeMetaAccessTests {

    // ------------------------------------------------------------------ helpers

    internal static Datamodel Model(Action<Datamodel>? tweak = null) {
        var dm = new Datamodel();
        dm.Add<SecuredPage>(autoDeduceRelations: true);
        dm.AddNamespace<ISystemUser>(); // the engine's own user / group / culture / collection model
        tweak?.Invoke(dm);
        return dm;
    }
    internal static NodeStore Open(IIOProvider? io = null, SettingsLocal? settings = null, Action<Datamodel>? tweak = null) {
        var data = new DataStoreLocal(Model(tweak), settings ?? new SettingsLocal(), io ?? new IOProviderMemory());
        data.Open(true, true);
        return new NodeStore(data);
    }
    // The context passed to Query<T>(ctx) is currently ignored (see PerQueryContext_IsUsedByTheQuery),
    // so the tests switch the store's default context instead.
    internal static string Titles(NodeStore db, QueryContext ctx) {
        db.SetQueryContext(ctx);
        return string.Join(",", db.Query<SecuredPage>().Execute().Select(p => p.Title).OrderBy(t => t));
    }
    internal static Guid Page(NodeStore db, string title, string? metaProperty = null, object? metaValue = null) {
        var page = db.CreateAndInsert<SecuredPage>(p => { p.Title = title; p.Body = "kanari " + title; });
        if (metaProperty != null) db.UpdateMeta(page.Id, metaProperty, metaValue!);
        return page.Id;
    }

    // ------------------------------------------------------------------ read access

    [TestMethod]
    public void ReadAccess_RestrictsWhoSeesTheNode() {
        using var db = Open();
        Page(db, "open");
        Page(db, "adminsOnly", nameof(IInnerNodeMeta.ReadAccess), NodeConstants.UserGroupAdmins);
        Page(db, "membersOnly", nameof(IInnerNodeMeta.ReadAccess), NodeConstants.UserGroupMember);

        Assert.AreEqual("open", Titles(db, QueryContext.Anonymous),
            "an anonymous reader must only see nodes open to everyone");
        Assert.AreEqual("adminsOnly,membersOnly,open", Titles(db, QueryContext.MasterAdmin),
            "the master admin is a member of every group and sees everything");
    }

    [TestMethod]
    public void ReadAccess_UnspecifiedMeansEveryone() {
        using var db = Open();
        Page(db, "noMetaAtAll");
        Assert.AreEqual("noMetaAtAll", Titles(db, QueryContext.Anonymous),
            "a node without meta falls back to the type default and then to the store default, which is Everyone");
    }

    [TestMethod]
    public void ReadAccess_ChangeTakesEffectOnTheNextQuery() {
        using var db = Open();
        var id = Page(db, "page");
        Assert.AreEqual("page", Titles(db, QueryContext.Anonymous));

        db.UpdateMeta(id, nameof(IInnerNodeMeta.ReadAccess), NodeConstants.UserGroupAdmins);
        Assert.AreEqual("", Titles(db, QueryContext.Anonymous),
            "closing a node must invalidate the cached id set of the anonymous context");

        db.UpdateMeta(id, nameof(IInnerNodeMeta.ReadAccess), NodeConstants.UserGroupEveryone);
        Assert.AreEqual("page", Titles(db, QueryContext.Anonymous),
            "and opening it again must bring it back");
    }

    [TestMethod]
    public void ReadAccess_TypeDefaultAppliesWhenMetaIsUnspecified() {
        using var db = Open(tweak: dm => dm.NodeTypes.Values
            .First(t => t.CodeName == nameof(SecuredPage)).DefaultReadAccess = NodeConstants.UserGroupAdmins);
        Page(db, "byTypeDefault");
        Assert.AreEqual("", Titles(db, QueryContext.Anonymous), "the node type default closes the node");
        Assert.AreEqual("byTypeDefault", Titles(db, QueryContext.MasterAdmin));
    }

    [TestMethod]
    public void ReadAccess_OnTheNodeOverridesTheTypeDefault() {
        using var db = Open(tweak: dm => dm.NodeTypes.Values
            .First(t => t.CodeName == nameof(SecuredPage)).DefaultReadAccess = NodeConstants.UserGroupAdmins);
        Page(db, "openedUp", nameof(IInnerNodeMeta.ReadAccess), NodeConstants.UserGroupEveryone);
        Assert.AreEqual("openedUp", Titles(db, QueryContext.Anonymous),
            "read access on the node itself wins over the node type default");
    }

    [TestMethod]
    public void ReadAccess_StoreDefaultAppliesWhenNothingElseIsSet() {
        using var db = Open(settings: new SettingsLocal() { DefaultReadAccess = SystemGroupType.Admins });
        Page(db, "bySettingsDefault");
        Assert.AreEqual("", Titles(db, QueryContext.Anonymous), "SettingsLocal.DefaultReadAccess closes the node");
        Assert.AreEqual("bySettingsDefault", Titles(db, QueryContext.MasterAdmin));
    }

    // ------------------------------------------------------------------ deleted, release and expire

    [TestMethod]
    public void Deleted_IsHiddenUnlessTheContextAsksForIt() {
        using var db = Open();
        Page(db, "live");
        Page(db, "binned", nameof(IInnerNodeMeta.Deleted), true);

        Assert.AreEqual("live", Titles(db, QueryContext.Anonymous));
        Assert.AreEqual("binned,live", Titles(db, QueryContext.Anonymous.Deleted()),
            "IncludeDeleted brings soft deleted nodes back into the result");
    }

    [TestMethod]
    public void ReleaseAndExpire_DefineThePublishedWindow() {
        using var db = Open();
        var now = DateTime.UtcNow;
        Page(db, "live");
        Page(db, "future", nameof(IInnerNodeMeta.ReleaseUtc), now.AddDays(1));
        Page(db, "expired", nameof(IInnerNodeMeta.ExpireUtc), now.AddDays(-1));

        Assert.AreEqual("live", Titles(db, QueryContext.Anonymous),
            "nodes outside their release/expire window are not published");
        Assert.AreEqual("expired,future,live", Titles(db, QueryContext.Anonymous.Unpublished()),
            "IncludeUnpublished ignores the window, which is what a preview needs");
    }

    [TestMethod]
    public void ReleaseAndExpire_AreEvaluatedAgainstTheContextClock() {
        using var db = Open();
        var now = DateTime.UtcNow;
        Page(db, "future", nameof(IInnerNodeMeta.ReleaseUtc), now.AddDays(1));
        Page(db, "expired", nameof(IInnerNodeMeta.ExpireUtc), now.AddDays(-1));

        Assert.AreEqual("future", Titles(db, QueryContext.Anonymous.Now(now.AddDays(2))),
            "moving the context clock past the release date publishes the node");
        Assert.AreEqual("expired", Titles(db, QueryContext.Anonymous.Now(now.AddDays(-2))),
            "and moving it back before the expiry date publishes the expired one");
    }

    // ------------------------------------------------------------------ collections

    [TestMethod]
    public void CollectionId_FiltersWhenTheContextSelectsCollections() {
        using var db = Open();
        var collectionA = Guid.NewGuid();
        var collectionB = Guid.NewGuid();
        Page(db, "inA", nameof(IInnerNodeMeta.CollectionId), collectionA);
        Page(db, "inB", nameof(IInnerNodeMeta.CollectionId), collectionB);
        Page(db, "inNone");

        Assert.AreEqual("inA,inB,inNone", Titles(db, QueryContext.Anonymous),
            "a context without collections does not filter on collection");
        Assert.AreEqual("inA", Titles(db, QueryContext.Anonymous.Collections([collectionA])),
            "selecting a collection excludes nodes in another one, and nodes with no collection");
        Assert.AreEqual("inA,inB", Titles(db, QueryContext.Anonymous.Collections([collectionA, collectionB])));
    }

    // ------------------------------------------------------------------ where the filter reaches

    [TestMethod]
    public void AccessFilter_AppliesToEveryQueryFormThatStartsFromATypeSet() {
        using var db = Open();
        var openId = Page(db, "open");
        var secretId = Page(db, "secret", nameof(IInnerNodeMeta.ReadAccess), NodeConstants.UserGroupAdmins);
        db.AddRelation<SecuredPage>(openId, p => p.Children, secretId);

        db.SetQueryContext(QueryContext.Anonymous);
        Assert.AreEqual(1, db.Query<SecuredPage>().Count(), "Count");
        Assert.AreEqual(0, db.Query<SecuredPage>().Where(p => p.Title == "secret").Count(), "Where on an indexed property");
        Assert.AreEqual(0, db.Query<SecuredPage>().WhereSearch("kanari secret").Count(), "full text search");
        Assert.AreEqual(0, db.Query<SecuredPage>().Where(p => p.Id == openId).Traverse(p => p.Children, maxLevel: 1).Count(), "graph traversal");
        Assert.AreEqual(0, db.Query<SecuredPage>().WhereRelates<SecuredPage, object>(p => p.Parent, openId).Count(), "relation filter");
        Assert.AreEqual(0, db.Get<SecuredPage>(openId).Children.Count(), "reading a relation property off a mapped node");

        db.SetQueryContext(QueryContext.MasterAdmin);
        Assert.AreEqual(1, db.Query<SecuredPage>().WhereSearch("kanari secret").Count(),
            "the admin still finds it, so the node really is in the text index");
    }

    [TestMethod]
    public void GetById_BypassesReadAccess() {
        // Deliberate, going by the comment in PickBestOuter: Get() trusts that whatever produced the id
        // did the filtering. Anything that turns an externally supplied id into a node therefore has to
        // check access itself.
        using var db = Open();
        var secretId = Page(db, "secret", nameof(IInnerNodeMeta.ReadAccess), NodeConstants.UserGroupAdmins);
        db.SetQueryContext(QueryContext.Anonymous);
        Assert.AreEqual("secret", db.Get<SecuredPage>(secretId).Title,
            "if this ever starts failing, Get() has become access checked and this test should be inverted");
    }

    // ------------------------------------------------------------------ persistence

    [TestMethod]
    public void AccessFilter_SurvivesAStateSaveAndReopen() {
        var io = new IOProviderMemory();
        using (var db = Open(io)) {
            Page(db, "open");
            Page(db, "secret", nameof(IInnerNodeMeta.ReadAccess), NodeConstants.UserGroupAdmins);
            db.Datastore.SaveIndexStates();
        }
        using (var db = Open(io)) {
            Assert.AreEqual("open", Titles(db, QueryContext.Anonymous), "read access must be restored from the saved index state");
            Assert.AreEqual("open,secret", Titles(db, QueryContext.MasterAdmin));
        }
    }

    [TestMethod]
    public void AccessFilter_SurvivesAReplayOfTheLog() {
        var io = new IOProviderMemory();
        using (var db = Open(io)) {
            Page(db, "open");
            Page(db, "secret", nameof(IInnerNodeMeta.ReadAccess), NodeConstants.UserGroupAdmins);
            // no SaveIndexStates: the meta index has to be rebuilt from the transaction log
        }
        using (var db = Open(io)) {
            Assert.AreEqual("open", Titles(db, QueryContext.Anonymous), "read access must be recovered from the log");
            Assert.AreEqual("open,secret", Titles(db, QueryContext.MasterAdmin));
        }
    }

    // ------------------------------------------------------------------ reading and writing the meta itself

    [TestMethod]
    public void UpdateMeta_RoundTripsEveryAccessField() {
        using var db = Open();
        var id = Page(db, "page");
        var collection = Guid.NewGuid();
        var read = Guid.NewGuid();
        var edit = Guid.NewGuid();
        var editView = Guid.NewGuid();
        var publish = Guid.NewGuid();
        var createdBy = Guid.NewGuid();
        var changedBy = Guid.NewGuid();
        var release = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var expire = new DateTime(2031, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        db.UpdateMeta(id, [
            new(nameof(IInnerNodeMeta.CollectionId), collection),
            new(nameof(IInnerNodeMeta.ReadAccess), read),
            new(nameof(IInnerNodeMeta.EditAccess), edit),
            new(nameof(IInnerNodeMeta.EditViewAccess), editView),
            new(nameof(IInnerNodeMeta.PublishAccess), publish),
            new(nameof(IInnerNodeMeta.CreatedBy), createdBy),
            new(nameof(IInnerNodeMeta.ChangedBy), changedBy),
            new(nameof(IInnerNodeMeta.ReleaseUtc), release),
            new(nameof(IInnerNodeMeta.ExpireUtc), expire),
            new(nameof(IInnerNodeMeta.Deleted), true),
        ]);

        Assert.IsTrue(db.Datastore.TryGetNodeMeta(id, out var meta));
        Assert.AreEqual(collection, meta.CollectionId);
        Assert.AreEqual(read, meta.ReadAccess);
        Assert.AreEqual(edit, meta.EditAccess);
        Assert.AreEqual(editView, meta.EditViewAccess);
        Assert.AreEqual(publish, meta.PublishAccess);
        Assert.AreEqual(createdBy, meta.CreatedBy);
        Assert.AreEqual(changedBy, meta.ChangedBy);
        Assert.AreEqual(release, meta.ReleaseUtc);
        Assert.AreEqual(expire, meta.ExpireUtc);
        Assert.IsTrue(meta.Deleted);
    }

    [TestMethod]
    public void UpdateMeta_RejectsCultureAndRevisionKey() {
        using var db = Open();
        var id = Page(db, "page");
        // the transaction wraps the ArgumentException, the point here is that the write is refused
        // rather than silently applied:
        var culture = Assert.ThrowsException<ExceptionWithoutIntegrityLoss>(() => db.UpdateMeta(id, nameof(IInnerNodeMeta.CultureId), Guid.NewGuid()));
        StringAssert.Contains(culture.Message, "Cannot update CultureId",
            "the culture of a revision is changed with the revision operations, not through meta");
        var revision = Assert.ThrowsException<ExceptionWithoutIntegrityLoss>(() => db.UpdateMeta(id, nameof(IInnerNodeMeta.RevisionKey), 1));
        StringAssert.Contains(revision.Message, "Cannot update RevisionKey",
            "the revision key is owned by the revision operations");
    }

    [TestMethod]
    public void InnerNodeMeta_SerializationRoundTripsTheAccessFields() {
        // The meta index and the node data both persist meta through these two methods, so a mistake here
        // silently changes who may read what after a restart.
        var full = new InnerNodeMetaFull(
            revisionKey: 7,
            collectionId: Guid.NewGuid(),
            readAccess: Guid.NewGuid(),
            editAccess: Guid.NewGuid(),
            editViewAccess: Guid.NewGuid(),
            publishAccess: Guid.NewGuid(),
            deleted: true,
            createdBy: Guid.NewGuid(),
            changedBy: Guid.NewGuid(),
            cultureId: Guid.NewGuid(),
            releaseUtc: new DateTime(2030, 5, 4, 3, 2, 1, DateTimeKind.Utc),
            expireUtc: new DateTime(2031, 5, 4, 3, 2, 1, DateTimeKind.Utc));
        var back = IInnerNodeMeta.FromBytes(IInnerNodeMeta.ToBytes(full))!;
        Assert.AreEqual(full.RevisionKey, back.RevisionKey);
        Assert.AreEqual(full.CollectionId, back.CollectionId);
        Assert.AreEqual(full.ReadAccess, back.ReadAccess);
        Assert.AreEqual(full.EditAccess, back.EditAccess);
        Assert.AreEqual(full.EditViewAccess, back.EditViewAccess);
        Assert.AreEqual(full.PublishAccess, back.PublishAccess);
        Assert.AreEqual(full.Deleted, back.Deleted);
        Assert.AreEqual(full.CreatedBy, back.CreatedBy);
        Assert.AreEqual(full.ChangedBy, back.ChangedBy);
        Assert.AreEqual(full.CultureId, back.CultureId);
        Assert.AreEqual(full.ReleaseUtc, back.ReleaseUtc);
        Assert.AreEqual(full.ExpireUtc, back.ExpireUtc);
        Assert.IsTrue(full.Equals(back), "and the round tripped meta must compare equal, as the index keys on it");

        var min = new InnerNodeMetaMin(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var minBack = IInnerNodeMeta.FromBytes(IInnerNodeMeta.ToBytes(min))!;
        Assert.AreEqual(min.CollectionId, minBack.CollectionId);
        Assert.AreEqual(min.ReadAccess, minBack.ReadAccess);
        Assert.AreEqual(min.EditAccess, minBack.EditAccess);
        Assert.IsTrue(min.Equals(minBack));

        Assert.IsNull(IInnerNodeMeta.FromBytes(IInnerNodeMeta.ToBytes(null)), "empty meta is stored as null");
    }

    [TestMethod]
    public void MinimizeIfPossible_KeepsTheAccessFieldsIntact() {
        // The store shrinks meta objects so they can be shared between nodes. A meta that differs only in
        // a field the minimized form drops would silently take on someone else's access level.
        var readAccess = Guid.NewGuid();
        var editAccess = Guid.NewGuid();
        var minimized = IInnerNodeMeta.MinimizeIfPossible(new InnerNodeMetaFull(
            revisionKey: 0, collectionId: Guid.Empty, readAccess: readAccess, editAccess: editAccess,
            editViewAccess: editAccess, publishAccess: editAccess, deleted: false,
            createdBy: Guid.Empty, changedBy: Guid.Empty, cultureId: Guid.Empty,
            releaseUtc: null, expireUtc: null))!;
        Assert.IsInstanceOfType(minimized, typeof(InnerNodeMetaMin));
        Assert.AreEqual(readAccess, minimized.ReadAccess);
        Assert.AreEqual(editAccess, minimized.EditAccess);

        var distinctPublishAccess = new InnerNodeMetaFull(
            revisionKey: 0, collectionId: Guid.Empty, readAccess: readAccess, editAccess: editAccess,
            editViewAccess: editAccess, publishAccess: Guid.NewGuid(), deleted: false,
            createdBy: Guid.Empty, changedBy: Guid.Empty, cultureId: Guid.Empty,
            releaseUtc: null, expireUtc: null);
        Assert.IsInstanceOfType(IInnerNodeMeta.MinimizeIfPossible(distinctPublishAccess), typeof(InnerNodeMetaFull),
            "a publish access of its own cannot be represented by the minimal form");
        Assert.IsNull(IInnerNodeMeta.MinimizeIfPossible(IInnerNodeMeta.Empty), "an all default meta is dropped entirely");
    }
}

/// <summary>
/// The parts of meta based access control that do not work yet. Each test states the behaviour the
/// feature needs and fails today; the cause is named in the test.
/// </summary>
[TestClass]
public class NodeMetaAccessKnownIssuesTests {

    static (NodeStore db, Guid plainUser, Guid editorUser, Guid adminUser, Guid editors, Guid staff) openWithUsers() {
        var db = NodeMetaAccessTests.Open();
        var editors = db.CreateAndInsert<ISystemUserGroup>(g => g.GroupName = "editors").Id;
        var staff = db.CreateAndInsert<ISystemUserGroup>(g => g.GroupName = "staff").Id;
        db.AddRelation<ISystemUserGroup>(staff, g => g.GroupMembers, editors); // editors is a member group of staff
        var plainUser = db.CreateAndInsert<ISystemUser>(u => u.UserType = SystemUserType.User).Id;
        var editorUser = db.CreateAndInsert<ISystemUser>(u => u.UserType = SystemUserType.User).Id;
        db.AddRelation<ISystemUser>(editorUser, u => u.Memberships, editors);
        var adminUser = db.CreateAndInsert<ISystemUser>(u => u.UserType = SystemUserType.Admin).Id;
        return (db, plainUser, editorUser, adminUser, editors, staff);
    }

    [TestMethod]
    public void UserType_IsReadFromTheUserNode() {
        // NativeModelStore.addUser looks the user type up by NodeConstants.NativeUserPropertyUserType
        // (61bfa8ff-...), but the property id generated for ISystemUser.UserType is 4f64452a-... , so the
        // lookup never hits and every user is registered as SystemUserType.Anonymous. (ISystemCulture.
        // CultureCode works only because its constant happens to be the generated id.) The value also
        // arrives boxed as an int, so the (SystemUserType) cast in addUser needs an int cast as well.
        var (db, plainUser, _, adminUser, _, _) = openWithUsers();
        using (db) {
            NodeMetaAccessTests.Page(db, "membersOnly", nameof(IInnerNodeMeta.ReadAccess), NodeConstants.UserGroupMember);
            NodeMetaAccessTests.Page(db, "adminsOnly", nameof(IInnerNodeMeta.ReadAccess), NodeConstants.UserGroupAdmins);

            Assert.AreEqual("membersOnly", NodeMetaAccessTests.Titles(db, QueryContext.Anonymous.User(plainUser)),
                "any signed in user is a member of the built in Member group");
            Assert.AreEqual("adminsOnly,membersOnly", NodeMetaAccessTests.Titles(db, QueryContext.Anonymous.User(adminUser)),
                "a user node of type Admin is a member of every group");
        }
    }

    [TestMethod]
    public void GroupMembership_GrantsAccessToTheGroupsNodes() {
        // QueryContextKey's constructor takes membershipIds but never assigns the MembershipIds field
        // (Relatude.DB.Model/Datamodels/QueryContext.cs), so it is always null and no user can match a
        // group specific read access. The same omission makes two contexts that differ only by user
        // compare equal, so the second user is served the first user's cached id set.
        var (db, plainUser, editorUser, _, editors, _) = openWithUsers();
        using (db) {
            NodeMetaAccessTests.Page(db, "editorsOnly", nameof(IInnerNodeMeta.ReadAccess), editors);
            Assert.AreEqual("editorsOnly", NodeMetaAccessTests.Titles(db, QueryContext.Anonymous.User(editorUser)),
                "a member of the editors group may read a node restricted to that group");
            Assert.AreEqual("", NodeMetaAccessTests.Titles(db, QueryContext.Anonymous.User(plainUser)),
                "a user outside the group may not");
        }
    }

    [TestMethod]
    public void GroupMembership_IsInheritedThroughGroupsOfGroups() {
        // NativeModelStore.GetEffectiveMembershipsOfUser already expands nested groups; it is the dropped
        // MembershipIds field (see above) that keeps the result from ever being used.
        var (db, _, editorUser, _, _, staff) = openWithUsers();
        using (db) {
            NodeMetaAccessTests.Page(db, "staffOnly", nameof(IInnerNodeMeta.ReadAccess), staff);
            Assert.AreEqual("staffOnly", NodeMetaAccessTests.Titles(db, QueryContext.Anonymous.User(editorUser)),
                "editors is a member group of staff, so an editor is effectively staff");
        }
    }

    [TestMethod]
    public void EditViewAccess_AppliesInEditViewMode() {
        // QueryContextKey's constructor also never assigns EditView, so ctx.EditViewMode() never reaches
        // the filter and the edit view access check in isMetaRelevantForContext is dead code.
        using var db = NodeMetaAccessTests.Open();
        NodeMetaAccessTests.Page(db, "notEditable", nameof(IInnerNodeMeta.EditViewAccess), NodeConstants.UserGroupAdmins);
        Assert.AreEqual("notEditable", NodeMetaAccessTests.Titles(db, QueryContext.Anonymous),
            "edit view access does not affect normal reading");
        Assert.AreEqual("", NodeMetaAccessTests.Titles(db, QueryContext.Anonymous.EditViewMode()),
            "but in edit view mode the node is only listed for those who may open it in the editor");
    }

    [TestMethod]
    public void PerQueryContext_IsUsedByTheQuery() {
        // QueryStringBuilder.Prepare() builds the QueryStringEvaluater without _ctx, and the evaluater
        // calls Datastore.Query(query, parameters) without the ctx argument the interface offers. So the
        // context handed to Query<T>(ctx) is silently ignored and the store default is used - which reads
        // as a successful permission check to any caller that passes a restricted context per query.
        using var db = NodeMetaAccessTests.Open();
        NodeMetaAccessTests.Page(db, "adminsOnly", nameof(IInnerNodeMeta.ReadAccess), NodeConstants.UserGroupAdmins);
        db.SetQueryContext(QueryContext.MasterAdmin); // store default sees everything
        Assert.AreEqual(0, db.Query<SecuredPage>(QueryContext.Anonymous).Count(),
            "the context passed to the query must override the store default");
    }

    [TestMethod]
    public void Include_DoesNotLeakRestrictedNodes() {
        // Traverse, WhereRelates and lazy relation properties are all filtered by the context, but the
        // nodes preloaded by Include are not: the restricted child is handed to an anonymous reader.
        using var db = NodeMetaAccessTests.Open();
        var openId = db.CreateAndInsert<SecuredPage>(p => p.Title = "open").Id;
        var secretId = NodeMetaAccessTests.Page(db, "secret", nameof(IInnerNodeMeta.ReadAccess), NodeConstants.UserGroupAdmins);
        db.AddRelation<SecuredPage>(openId, p => p.Children, secretId);

        db.SetQueryContext(QueryContext.Anonymous);
        var included = db.Query<SecuredPage>().Where(p => p.Id == openId).Include(p => p.Children).Execute()
            .SelectMany(p => p.Children.Select(c => c.Title)).ToList();
        CollectionAssert.AreEqual(new List<string>(), included,
            "Include must apply the same read access filter as Traverse does");
    }
}
