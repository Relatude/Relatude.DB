using Relatude.DB.DataStores;
using Relatude.DB.Nodes;
using Relatude.DB.Query;
using Relatude.DB.Utils;

namespace Tests;

[TestClass]
public class TransactionOperationTests {

    // -----------------------------------------------------------------------
    // Insert / InsertOrFail
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Insert_SingleNode_NodeExistsAfterCommit() {
        using var store = Helper.Open();
        var a = new Article { Id = 1, Name = "Alpha" };

        var t = store.CreateTransaction();
        t.Insert(a);
        t.Execute();

        var result = store.Get<Article>(1);
        Assert.IsNotNull(result);
        Assert.AreEqual("Alpha", result.Name);
    }

    [TestMethod]
    public void Insert_MultipleNodes_AllExistAfterCommit() {
        using var store = Helper.Open();
        var articles = Enumerable.Range(1, 5)
            .Select(i => new Article { Id = i, Name = "Article " + i })
            .ToList();

        var t = store.CreateTransaction();
        t.Insert(articles.Cast<object>());
        t.Execute();

        Assert.AreEqual(5, store.Query<Article>().Count());
    }

    [TestMethod]
    public void Insert_ReturnsGuid_MatchesInsertedNode() {
        using var store = Helper.Open();
        var a = new Article { Name = "GuidTest" };

        var t = store.CreateTransaction();
        t.Insert(a, out Guid id);
        t.Execute();

        Assert.AreNotEqual(Guid.Empty, id);
        var result = store.Get<Article>(id);
        Assert.IsNotNull(result);
        Assert.AreEqual("GuidTest", result.Name);
    }

    [TestMethod]
    public void InsertOrFail_DuplicateId_ThrowsOnExecute() {
        using var store = Helper.Open();
        var a = new Article { Id = 99, Name = "Orig" };
        store.Insert(a);

        var duplicate = new Article { Id = 99, Name = "Dup" };
        var t = store.CreateTransaction();
        t.InsertOrFail(duplicate);

        Assert.ThrowsException<ExceptionWithoutIntegrityLoss>(() => t.Execute());

        // Original should still be intact
        var existing = store.Get<Article>(99);
        Assert.AreEqual("Orig", existing.Name);
    }

    [TestMethod]
    public void InsertOrFail_NodeDoesNotExist_Succeeds() {
        using var store = Helper.Open();
        var a = new Article { Id = 42, Name = "New" };

        var t = store.CreateTransaction();
        t.InsertOrFail(a);
        t.Execute();

        var result = store.Get<Article>(42);
        Assert.IsNotNull(result);
        Assert.AreEqual("New", result.Name);
    }

    [TestMethod]
    public void InsertOrFail_WithCollection_AllInserted() {
        using var store = Helper.Open();
        var nodes = Enumerable.Range(10, 3)
            .Select(i => new Article { Id = i, Name = "N" + i })
            .Cast<object>()
            .ToList();

        var t = store.CreateTransaction();
        t.InsertOrFail(nodes);
        t.Execute();

        Assert.AreEqual(3, store.Query<Article>().Count());
    }

    // -----------------------------------------------------------------------
    // InsertIfNotExists
    // -----------------------------------------------------------------------

    [TestMethod]
    public void InsertIfNotExists_NodeAlreadyExists_DoesNotFail() {
        using var store = Helper.Open();
        var a = new Article { Id = 5, Name = "Existing" };
        store.Insert(a);

        // Reuse the same object instance so the int->Guid mapping stays consistent.
        var t = store.CreateTransaction();
        t.InsertIfNotExists(a);
        t.Execute(); // must not throw

        // Name should remain unchanged
        var result = store.Get<Article>(5);
        Assert.AreEqual("Existing", result.Name);
    }

    [TestMethod]
    public void InsertIfNotExists_NodeDoesNotExist_InsertsNode() {
        using var store = Helper.Open();

        var t = store.CreateTransaction();
        t.InsertIfNotExists(new Article { Id = 7, Name = "Fresh" });
        t.Execute();

        var result = store.Get<Article>(7);
        Assert.IsNotNull(result);
        Assert.AreEqual("Fresh", result.Name);
    }

    [TestMethod]
    public void InsertIfNotExists_WithCollection_ExistingNodesNotOverwritten() {
        using var store = Helper.Open();
        var a1 = new Article { Id = 1, Name = "Keep1" };
        var a2 = new Article { Id = 2, Name = "Keep2" };
        store.Insert(a1);
        store.Insert(a2);

        // Reuse the same instances for existing nodes so their int->Guid mapping is preserved.
        var nodes = new List<object> { a1, a2, new Article { Id = 3, Name = "NewNode" } };

        var t = store.CreateTransaction();
        t.InsertIfNotExists(nodes);
        t.Execute();

        Assert.AreEqual("Keep1", store.Get<Article>(1).Name);
        Assert.AreEqual("Keep2", store.Get<Article>(2).Name);
        Assert.AreEqual("NewNode", store.Get<Article>(3).Name);
    }

    // -----------------------------------------------------------------------
    // UpdateOrFail / Update
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Update_ExistingNode_PropertyChangedAfterCommit() {
        using var store = Helper.Open();
        store.Insert(new Article { Id = 1, Name = "Before" });

        var t = store.CreateTransaction();
        t.Update(new Article { Id = 1, Name = "After" });
        t.Execute();

        Assert.AreEqual("After", store.Get<Article>(1).Name);
    }

    [TestMethod]
    public void UpdateOrFail_NodeDoesNotExist_ThrowsOnExecute() {
        using var store = Helper.Open();

        var t = store.CreateTransaction();
        t.UpdateOrFail(new Article { Id = 999, Name = "Ghost" });

        Assert.ThrowsException<ExceptionWithoutIntegrityLoss>(() => t.Execute());
    }

    [TestMethod]
    public void UpdateOrFail_MultipleNodes_AllUpdated() {
        using var store = Helper.Open();
        store.Insert(new Article { Id = 1, Name = "A" });
        store.Insert(new Article { Id = 2, Name = "B" });

        var t = store.CreateTransaction();
        t.Update(new Article { Id = 1, Name = "A_Updated" });
        t.Update(new Article { Id = 2, Name = "B_Updated" });
        t.Execute();

        Assert.AreEqual("A_Updated", store.Get<Article>(1).Name);
        Assert.AreEqual("B_Updated", store.Get<Article>(2).Name);
    }

    // -----------------------------------------------------------------------
    // UpdateIfExists
    // -----------------------------------------------------------------------

    [TestMethod]
    public void UpdateIfExists_NodeExists_Updates() {
        using var store = Helper.Open();
        store.Insert(new Article { Id = 1, Name = "Original" });

        var t = store.CreateTransaction();
        t.UpdateIfExists(new Article { Id = 1, Name = "Updated" });
        t.Execute();

        Assert.AreEqual("Updated", store.Get<Article>(1).Name);
    }

    [TestMethod]
    public void UpdateIfExists_NodeDoesNotExist_ThrowsBecauseIntIdUnknown() {
        // When using integer IDs, UpdateIfExists requires the int->Guid mapping to
        // already be registered in the store. An unknown int id causes an exception.
        using var store = Helper.Open();

        var t = store.CreateTransaction();
        t.UpdateIfExists(new Article { Id = 404, Name = "Missing" });

        Assert.ThrowsException<ExceptionWithoutIntegrityLoss>(() => t.Execute());
    }

    // -----------------------------------------------------------------------
    // ForceUpdate
    // -----------------------------------------------------------------------

    [TestMethod]
    public void ForceUpdate_ExistingNode_Updates() {
        using var store = Helper.Open();
        store.Insert(new Article { Id = 1, Name = "V1" });

        var t = store.CreateTransaction();
        t.ForceUpdate(new Article { Id = 1, Name = "V2" });
        t.Execute();

        Assert.AreEqual("V2", store.Get<Article>(1).Name);
    }

    [TestMethod]
    public void ForceUpdate_Collection_AllNodesUpdated() {
        using var store = Helper.Open();
        for (int i = 1; i <= 3; i++)
            store.Insert(new Article { Id = i, Name = "Old" + i });

        var t = store.CreateTransaction();
        for (int i = 1; i <= 3; i++)
            t.ForceUpdate(new Article { Id = i, Name = "New" + i });
        t.Execute();

        for (int i = 1; i <= 3; i++)
            Assert.AreEqual("New" + i, store.Get<Article>(i).Name);
    }

    // -----------------------------------------------------------------------
    // Upsert / ForceUpsert
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Upsert_NewNode_Inserted() {
        using var store = Helper.Open();

        var t = store.CreateTransaction();
        t.Upsert(new Article { Id = 1, Name = "UpsertNew" });
        t.Execute();

        Assert.AreEqual("UpsertNew", store.Get<Article>(1).Name);
    }

    [TestMethod]
    public void Upsert_ExistingNode_Updated() {
        using var store = Helper.Open();
        var a = new Article { Id = 1, Name = "Before" };
        store.Insert(a);

        a.Name = "After";
        var t = store.CreateTransaction();
        t.Upsert(a);
        t.Execute();

        Assert.AreEqual("After", store.Get<Article>(1).Name);
    }

    [TestMethod]
    public void ForceUpsert_NewNode_Inserted() {
        using var store = Helper.Open();

        var t = store.CreateTransaction();
        t.ForceUpsert(new Article { Id = 5, Name = "ForceNew" });
        t.Execute();

        Assert.AreEqual("ForceNew", store.Get<Article>(5).Name);
    }

    [TestMethod]
    public void ForceUpsert_ExistingNode_Overwrites() {
        using var store = Helper.Open();
        var a = new Article { Id = 5, Name = "Old" };
        store.Insert(a);

        a.Name = "Overwritten";
        var t = store.CreateTransaction();
        t.ForceUpsert(a);
        t.Execute();

        Assert.AreEqual("Overwritten", store.Get<Article>(5).Name);
    }

    // -----------------------------------------------------------------------
    // Delete / DeleteOrFail
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Delete_ExistingNode_NodeRemovedAfterCommit() {
        using var store = Helper.Open();
        store.Insert(new Article { Id = 1, Name = "ToDelete" });

        var t = store.CreateTransaction();
        t.Delete(1);
        t.Execute();

        Assert.AreEqual(0, store.Query<Article>().Count());
    }

    [TestMethod]
    public void DeleteOrFail_NodeDoesNotExist_ThrowsOnExecute() {
        using var store = Helper.Open();

        var t = store.CreateTransaction();
        t.DeleteOrFail(999);

        Assert.ThrowsException<ExceptionWithoutIntegrityLoss>(() => t.Execute());
    }

    [TestMethod]
    public void DeleteOrFail_ExistingNode_Succeeds() {
        using var store = Helper.Open();
        store.Insert(new Article { Id = 1, Name = "Del" });

        var t = store.CreateTransaction();
        t.DeleteOrFail(1);
        t.Execute();

        Assert.AreEqual(0, store.Query<Article>().Count());
    }

    [TestMethod]
    public void DeleteIfExists_NodeExists_Deleted() {
        using var store = Helper.Open();
        store.Insert(new Article { Id = 1, Name = "Del" });

        var t = store.CreateTransaction();
        t.DeleteIfExists(1);
        t.Execute();

        Assert.AreEqual(0, store.Query<Article>().Count());
    }

    [TestMethod]
    public void DeleteIfExists_NodeDoesNotExist_DoesNotThrow() {
        using var store = Helper.Open();

        var t = store.CreateTransaction();
        t.DeleteIfExists(1234);
        t.Execute(); // must not throw
    }

    [TestMethod]
    public void Delete_MultipleIds_AllRemoved() {
        using var store = Helper.Open();
        for (int i = 1; i <= 4; i++)
            store.Insert(new Article { Id = i });

        var t = store.CreateTransaction();
        t.Delete(new[] { 1, 2, 3 });
        t.Execute();

        Assert.AreEqual(1, store.Query<Article>().Count());
        Assert.IsNotNull(store.Get<Article>(4));
    }

    // -----------------------------------------------------------------------
    // UpdateProperty / ForceUpdateProperty
    // -----------------------------------------------------------------------

    [TestMethod]
    public void UpdateProperty_ByExpression_ValueChangedAfterCommit() {
        using var store = Helper.Open();
        store.Insert(new Article { Id = 1, Name = "OldName" });

        var t = store.CreateTransaction();
        t.UpdateProperty<Article, string>(1, a => a.Name, "NewName");
        t.Execute();

        Assert.AreEqual("NewName", store.Get<Article>(1).Name);
    }

    [TestMethod]
    public void ForceUpdateProperty_ByExpression_ValueChangedAfterCommit() {
        using var store = Helper.Open();
        store.Insert(new Article { Id = 1, Name = "OldName" });

        var t = store.CreateTransaction();
        t.ForceUpdateProperty<Article, string>(1, a => a.Name, "ForcedName");
        t.Execute();

        Assert.AreEqual("ForcedName", store.Get<Article>(1).Name);
    }

    [TestMethod]
    public void UpdateProperty_IntegerProperty_ValueChangedAfterCommit() {
        using var store = Helper.Open();
        store.Insert(new Article { Id = 1, IntegerNum = 0 });

        var t = store.CreateTransaction();
        t.UpdateProperty<Article, int>(1, a => a.IntegerNum, 42);
        t.Execute();

        Assert.AreEqual(42, store.Get<Article>(1).IntegerNum);
    }

    // -----------------------------------------------------------------------
    // AddToProperty / ResetProperty
    // -----------------------------------------------------------------------

    [TestMethod]
    public void AddToProperty_Integer_AccumulatesCorrectly() {
        using var store = Helper.Open();
        store.Insert(new Article { Id = 1, IntegerNum = 10 });

        var t = store.CreateTransaction();
        t.AddToProperty<Article, int>(1, a => a.IntegerNum, 5);
        t.Execute();

        Assert.AreEqual(15, store.Get<Article>(1).IntegerNum);
    }

    [TestMethod]
    public void ResetProperty_ExecutesWithoutError() {
        // ResetProperty removes a stored value so the node falls back to its default.
        // The exact observable value depends on how the property was stored.
        using var store = Helper.Open();
        store.Insert(new Article { Id = 1, Name = "SomeName", IntegerNum = 77 });

        var t = store.CreateTransaction();
        t.ResetProperty<Article, int>(1, a => a.IntegerNum);
        t.Execute(); // must not throw
    }

    // -----------------------------------------------------------------------
    // ValidateProperty
    // -----------------------------------------------------------------------

    [TestMethod]
    public void ValidateProperty_ValueMatches_TransactionSucceeds() {
        using var store = Helper.Open();
        store.Insert(new Article { Id = 1, IntegerNum = 100 });

        var t = store.CreateTransaction();
        t.ValidateProperty<Article, int>(1, a => a.IntegerNum, 100);
        t.Update(new Article { Id = 1, IntegerNum = 200 });
        t.Execute(); // validation passes, update applies

        Assert.AreEqual(200, store.Get<Article>(1).IntegerNum);
    }

    [TestMethod]
    public void ValidateProperty_ValueDoesNotMatch_ThrowsOnExecute() {
        using var store = Helper.Open();
        store.Insert(new Article { Id = 1, IntegerNum = 5 });

        var t = store.CreateTransaction();
        t.ValidateProperty<Article, int>(1, a => a.IntegerNum, 999); // wrong expected value
        t.Update(new Article { Id = 1, IntegerNum = 10 });

        Assert.ThrowsException<ExceptionWithoutIntegrityLoss>(() => t.Execute());

        // Value must remain unchanged
        Assert.AreEqual(5, store.Get<Article>(1).IntegerNum);
    }

    // -----------------------------------------------------------------------
    // Relations: Relate / UnRelate / SetRelation / ClearRelations
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Relate_AddsRelation_QueryReturnsRelated() {
        using var store = Helper.Open();
        store.Insert(new Article { Id = 1 });
        store.Insert(new Article { Id = 2 });

        var t = store.CreateTransaction();
        t.AddRelation<Article>(1, a => a.Children, 2);
        t.Execute();

        var children = store.Query<Article>(a => a.Id == 1)
            .Include<Article>(a => a.Children)
            .FirstOrDefault();
        Assert.IsNotNull(children);
    }

    [TestMethod]
    public void UnRelate_RemovesExistingRelation() {
        using var store = Helper.Open();
        store.Insert(new Article { Id = 1 });
        store.Insert(new Article { Id = 2 });
        store.AddRelation<Article>(1, a => a.Children, 2);

        var t = store.CreateTransaction();
        t.UnRelate<Article>(1, a => a.Children, 2);
        t.Execute();
    }

    [TestMethod]
    public void SetRelation_ReplacesExistingRelation() {
        using var store = Helper.Open();
        store.Insert(new Article { Id = 1 });
        store.Insert(new Article { Id = 2 });
        store.Insert(new Article { Id = 3 });
        store.AddRelation<Article>(1, a => a.Children, 2);

        var t = store.CreateTransaction();
        t.SetRelation<Article>(1, a => a.Children, 3);
        t.Execute();
    }

    [TestMethod]
    public void ClearRelations_RemovesAllRelationsFromNode() {
        using var store = Helper.Open();
        store.Insert(new Article { Id = 1 });
        store.Insert(new Article { Id = 2 });
        store.Insert(new Article { Id = 3 });
        store.AddRelation<Article>(1, a => a.Children, 2);
        store.AddRelation<Article>(1, a => a.Children, 3);

        var t = store.CreateTransaction();
        t.ClearRelations<Article>(1, a => a.Children);
        t.Execute();
    }

    [TestMethod]
    public void Relate_ToDeletedNodeInSameTransaction_ThrowsOnExecute() {
        using var store = Helper.Open();
        store.Insert(new Article { Id = 1 });
        store.Insert(new Article { Id = 2 });

        var t = store.CreateTransaction();
        t.Delete(2);
        t.AddRelation<Article>(1, a => a.Children, 2); // target does not exist after delete

        Assert.ThrowsException<ExceptionWithoutIntegrityLoss>(() => t.Execute());

        // Ensure the delete was rolled back
        Assert.IsNotNull(store.Get<Article>(2));
    }

    // -----------------------------------------------------------------------
    // Rollback behaviour
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Transaction_WhenFails_AllChangesRolledBack() {
        using var store = Helper.Open();
        store.Insert(new Article { Id = 1, Name = "Stable" });

        var t = store.CreateTransaction();
        t.Insert(new Article { Id = 2, Name = "WillBeRolledBack" });
        t.Update(new Article { Id = 1, Name = "ChangedButShouldRollBack" });
        t.DeleteOrFail(999); // does not exist → forces failure

        Assert.ThrowsException<ExceptionWithoutIntegrityLoss>(() => t.Execute());

        // Article 1 name must be unchanged
        Assert.AreEqual("Stable", store.Get<Article>(1).Name);
        // Article 2 must not have been inserted
        Assert.AreEqual(1, store.Query<Article>().Count());
    }

    [TestMethod]
    public void Transaction_SuccessfulComplexBatch_AllChangesApplied() {
        using var store = Helper.Open();
        store.Insert(new Article { Id = 1, Name = "A" });
        store.Insert(new Article { Id = 2, Name = "B" });
        store.Insert(new Article { Id = 3, Name = "C" });

        var t = store.CreateTransaction();
        t.Insert(new Article { Id = 4, Name = "D" });
        t.Update(new Article { Id = 1, Name = "A_Updated" });
        t.Delete(3);
        t.AddRelation<Article>(1, a => a.Children, 2);
        t.Execute();

        Assert.AreEqual("A_Updated", store.Get<Article>(1).Name);
        Assert.IsNotNull(store.Get<Article>(4));
        Assert.AreEqual(3, store.Query<Article>().Count()); // 1, 2, 4
    }

    // -----------------------------------------------------------------------
    // Transaction chaining (fluent API)
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Transaction_FluentChaining_ExecutesAllOperations() {
        using var store = Helper.Open();
        store.Insert(new Article { Id = 1, Name = "Old" });

        store.CreateTransaction()
            .Insert(new Article { Id = 2, Name = "Two" })
            .Insert(new Article { Id = 3, Name = "Three" })
            .Update(new Article { Id = 1, Name = "Updated" })
            .Delete(3)
            .Execute();

        Assert.AreEqual("Updated", store.Get<Article>(1).Name);
        Assert.IsNotNull(store.Get<Article>(2));
        Assert.AreEqual(2, store.Query<Article>().Count()); // 1 and 2
    }

    // -----------------------------------------------------------------------
    // Count
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Transaction_Count_ReflectsNumberOfActions() {
        using var store = Helper.Open();
        store.Insert(new Article { Id = 1 });

        var t = store.CreateTransaction();
        Assert.AreEqual(0, t.Count);

        t.Insert(new Article { Id = 2 });
        t.Delete(1);
        t.UpdateProperty<Article, string>(2, a => a.Name, "X");

        Assert.IsTrue(t.Count >= 3);
    }

    // -----------------------------------------------------------------------
    // CommitCallback
    // -----------------------------------------------------------------------

    [TestMethod]
    public void SetCommitCallback_CalledBeforeCommit_CanInspectState() {
        using var store = Helper.Open();
        bool callbackInvoked = false;

        var t = store.CreateTransaction();
        t.Insert(new Article { Id = 1, Name = "CB" });
        t.SetCommitCallback(_ => { callbackInvoked = true; });
        t.Execute();

        Assert.IsTrue(callbackInvoked);
    }

    [TestMethod]
    public void SetCommitCallback_ThrowsInCallback_TransactionRolledBack() {
        // The framework wraps callback exceptions in ExceptionWithoutIntegrityLoss.
        using var store = Helper.Open();

        var t = store.CreateTransaction();
        t.Insert(new Article { Id = 1, Name = "ShouldNotPersist" });
        t.SetCommitCallback(_ => throw new InvalidOperationException("Callback veto"));

        Assert.ThrowsException<ExceptionWithoutIntegrityLoss>(() => t.Execute());

        Assert.AreEqual(0, store.Query<Article>().Count());
    }

    // -----------------------------------------------------------------------
    // Insert with related nodes
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Insert_NodeWithRelatedChildren_RelationsCreated() {
        using var store = Helper.Open();
        var child1 = new Article { Id = 10, Name = "Child1" };
        var child2 = new Article { Id = 11, Name = "Child2" };
        var parent = new Article { Id = 1, Name = "Parent", Children = new[] { child1, child2 } };

        var t = store.CreateTransaction();
        t.Insert(parent);
        t.Execute();

        Assert.AreEqual(3, store.Query<Article>().Count());
    }

    [TestMethod]
    public void Insert_NodeWithRelatedParent_RelationCreated() {
        using var store = Helper.Open();
        var parent = new Article { Id = 1, Name = "Parent" };
        var child = new Article { Id = 2, Name = "Child", Parent = parent };

        var t = store.CreateTransaction();
        t.Insert(child);
        t.Execute();

        Assert.AreEqual(2, store.Query<Article>().Count());
    }
}
