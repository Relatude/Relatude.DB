using Relatude.DB.DataStores;
using Relatude.DB.Query;
using System.Diagnostics;
using System.Linq.Expressions;
using Relatude.DB.Utils;
using Relatude.DB.Nodes;

namespace Tests;
[TestClass]
public class Transactions {

    [TestMethod]
    public void Reversal() {
        var datamodel = Helper.GetDatamodel();
        var storeData = DataStoreLocal.Open(datamodel);
        var store = new NodeStore(storeData);

        var a1 = new Article();
        a1.Id = 10;
        store.Insert(a1);

        var a2 = new Article();
        a2.Id = 20;
        store.Insert(a2);

        var a3 = new Article();
        a3.Id = 30;
        store.Insert(a3);

        var a4 = new Article();
        a4.Id = 40;
        store.Insert(a4);

        store.AddRelation<Article>(a1.Id, a => a.Children, a3.Id);
        var t = store.CreateTransaction();

        t.SetRelation<Article>(a3.Id, a => a.Parent!, a2.Id);
        t.Delete(a4.Id);
        t.AddRelation<Article>(a3.Id, a => a.Parent!, a4.Id); // will fail because a4 is deleted

        Assert.ThrowsException<ExceptionWithoutIntegrityLoss>(() => t.Execute());

        // testing that the transaction was rolled back and node was not deleted
        var id = a4.Id; // query language does not implement indirect references ( yet )
        Assert.IsTrue(store.Query<Article>(a => a.Id == id).Count() == 1);

        id = a3.Id;
        //Assert.IsTrue(store.Query<Article>(a => a.Id == id).Relates(a=>a.Parent, );


    }
    [TestMethod]
    public void Locking() {
        var datamodel = Helper.GetDatamodel();
        var storeData = DataStoreLocal.Open(datamodel);
        var store = new NodeStore(storeData);

        var a1 = new Article();
        a1.Id = 10;
        store.Insert(a1);

        var lockId = store.RequestLock(a1);
        {
            var t = store.CreateTransaction();
            t.NoRetriesIfLocked = true;
            a1.Body = Guid.NewGuid().ToString();
            t.ForceUpdate(a1);
            Assert.ThrowsException<NodeLockedException>(() => t.Execute());
        }
        store.ReleaseLock(lockId);
        {
            var t = store.CreateTransaction();
            t.NoRetriesIfLocked = true;
            a1.Body = Guid.NewGuid().ToString();
            t.ForceUpdate(a1);
            t.Execute();
        }

        var lockId2 = store.RequestLock(a1);
        var t2 = new Transaction(store, lockId2);
        t2.NoRetriesIfLocked = true;
        t2.ForceUpdate(a1);
        Assert.ThrowsException<NodeLockedException>(() => store.ForceUpdate(a1));
        t2.Execute();
        store.ReleaseLock(lockId2);

        // more should be testetd here....

    }
}