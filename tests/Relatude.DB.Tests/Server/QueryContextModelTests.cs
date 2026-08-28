using Relatude.DB.Datamodels;
using Relatude.DB.NodeServer.Models;

namespace Relatude.Server;

/// <summary>
/// The reading context the HTTP query endpoint accepts in the request body. It is the only place where
/// a <see cref="QueryContext"/> is built from untyped input, so the mapping is worth pinning: a member
/// silently not carried over would let a query read with more than the caller asked for.
/// </summary>
[TestClass]
public class QueryContextModelTests {

    [TestMethod]
    public void NoContext_MeansTheStoreDefault() {
        Assert.IsNull(QueryContextModel.Convert(null),
            "null tells the store to read with its own context, which is what a request without a context must do");
    }

    [TestMethod]
    public void EmptyContext_ReadsAsAnonymousWithNothingIncluded() {
        var ctx = QueryContextModel.Convert(new QueryContextModel())!;
        Assert.AreEqual(Guid.Empty, ctx.UserId);
        Assert.IsFalse(ctx.IncludeDeleted);
        Assert.IsFalse(ctx.IncludeUnpublished);
        Assert.IsFalse(ctx.IncludeHidden);
        Assert.IsFalse(ctx.EditView);
        Assert.IsFalse(ctx.IncludeCultureFallback);
        Assert.IsFalse(ctx.OnlyWithCulture);
        Assert.IsFalse(ctx.ExcludeDecendants);
        Assert.IsNull(ctx.CollectionIds);
        Assert.IsNull(ctx.NowUtc);
        Assert.IsNull(ctx.CultureCode);
        Assert.IsNull(ctx.CultureId);
    }

    [TestMethod]
    public void EveryMember_IsCarriedOver() {
        var userId = Guid.NewGuid();
        var collection = Guid.NewGuid();
        var now = new DateTime(2030, 3, 2, 1, 0, 0, DateTimeKind.Utc);
        var ctx = QueryContextModel.Convert(new QueryContextModel {
            UserId = userId,
            CultureCode = "nb-NO",
            IncludeDeleted = true,
            IncludeUnpublished = true,
            IncludeCultureFallback = true,
            OnlyWithCulture = true,
            EditView = true,
            IncludeHidden = true,
            ExcludeDescendants = true,
            CollectionIds = [collection],
            NowUtc = now,
        })!;
        Assert.AreEqual(userId, ctx.UserId);
        Assert.AreEqual("nb-NO", ctx.CultureCode);
        Assert.IsTrue(ctx.IncludeDeleted);
        Assert.IsTrue(ctx.IncludeUnpublished);
        Assert.IsTrue(ctx.IncludeCultureFallback);
        Assert.IsTrue(ctx.OnlyWithCulture);
        Assert.IsTrue(ctx.EditView);
        Assert.IsTrue(ctx.IncludeHidden);
        Assert.IsTrue(ctx.ExcludeDecendants);
        CollectionAssert.AreEqual(new[] { collection }, ctx.CollectionIds);
        Assert.AreEqual(now, ctx.NowUtc);
    }

    [TestMethod]
    public void CultureById_IsCarriedOver() {
        var cultureId = Guid.NewGuid();
        var ctx = QueryContextModel.Convert(new QueryContextModel { CultureId = cultureId })!;
        Assert.AreEqual(cultureId, ctx.CultureId);
        Assert.IsNull(ctx.CultureCode);
    }

    [TestMethod]
    public void CultureCodeAndCultureId_TogetherAreRejected() {
        // the store throws on this combination when it resolves the context, but by then the request is
        // half executed and the message says nothing about the request body:
        Assert.ThrowsException<InvalidOperationException>(() => QueryContextModel.Convert(new QueryContextModel {
            CultureCode = "nb-NO",
            CultureId = Guid.NewGuid(),
        }));
    }

    [TestMethod]
    public void Converting_DoesNotTouchTheSharedDefaultContext() {
        // QueryContext is built by copy on write from a shared static, so a mapping that assigned a
        // property instead of using the fluent methods would change the default for the whole process:
        QueryContextModel.Convert(new QueryContextModel {
            UserId = Guid.NewGuid(), IncludeDeleted = true, IncludeHidden = true, EditView = true,
            CollectionIds = [Guid.NewGuid()], NowUtc = DateTime.UtcNow,
        });
        Assert.AreEqual(Guid.Empty, QueryContext.Default.UserId);
        Assert.IsFalse(QueryContext.Default.IncludeDeleted);
        Assert.IsFalse(QueryContext.Default.IncludeHidden);
        Assert.IsFalse(QueryContext.Default.EditView);
        Assert.IsNull(QueryContext.Default.CollectionIds);
        Assert.IsNull(QueryContext.Default.NowUtc);
    }
}
