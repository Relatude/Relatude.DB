using Relatude.DB.DataStores.Relations;
using Relatude.DB.DataStores.Sets;

namespace Tests {
    [TestClass]
    public class RelationIndexes {
        public static SetRegister setRegister = new SetRegister(10000);

        [TestMethod]
        public void TestManyMany() {
            var r = new ManyManyIndex();
            r.Add(1, 2, DateTime.UtcNow);
            r.Add(1, 3, DateTime.UtcNow);
            r.Add(1, 1, DateTime.UtcNow);
            Assert.ThrowsException<ItemAlreadyInRelationException>(() => r.Add(1, 2, DateTime.UtcNow));
            Assert.ThrowsException<ItemAlreadyInRelationException>(() => r.Add(2, 1, DateTime.UtcNow));
            Assert.IsTrue(r.Contains(1, 2));
            Assert.IsTrue(r.Contains(2, 1));
            Assert.IsTrue(r.Contains(1, 1));
            Assert.IsTrue(r.Contains(1, 3));
            Assert.IsTrue(r.Contains(3, 1));
            Assert.IsFalse(r.Contains(2, 3));
            Assert.IsFalse(r.Contains(3, 2));
            r.Remove(1, 2);
            r.Add(1, 2, DateTime.UtcNow);
            r.Remove(2, 1);
            Assert.IsFalse(r.Contains(1, 2));
            Assert.IsFalse(r.Contains(2, 1));
            Assert.ThrowsException<ItemNotInRelationException>(() => r.Remove(2, 1));
            r.Remove(1, 1);
            Assert.IsFalse(r.Contains(1, 1));
        }
        [TestMethod]
        public void TestManyToMany() {
            var r = new ManyToManyIndex();
            r.Add(1, 2, DateTime.UtcNow);
            r.Add(1, 3, DateTime.UtcNow);
            r.Add(1, 1, DateTime.UtcNow);
            Assert.ThrowsException<ItemAlreadyInRelationException>(() => r.Add(1, 2, DateTime.UtcNow));
            Assert.IsTrue(r.Contains(1, 2));
            Assert.IsFalse(r.Contains(2, 1));
            Assert.IsTrue(r.Contains(1, 1));
            Assert.IsTrue(r.Contains(1, 3));
            Assert.IsFalse(r.Contains(3, 1));
            Assert.IsFalse(r.Contains(2, 3));
            Assert.IsFalse(r.Contains(3, 2));
            r.Remove(1, 2);
            Assert.IsFalse(r.Contains(1, 2));
            r.Add(1, 2, DateTime.UtcNow);
            Assert.IsTrue(r.Contains(1, 2));
            Assert.IsFalse(r.Contains(2, 1));
            Assert.ThrowsException<ItemNotInRelationException>(() => r.Remove(2, 1));
            Assert.IsFalse(r.Contains(2, 1));            
            r.Remove(1, 1);
            Assert.IsFalse(r.Contains(1, 1));
        }
        [TestMethod]
        public void TestOneToMany() {
            var r = new OneToManyIndex(setRegister);
            r.Add(1, 1, DateTime.UtcNow);
            r.Remove(1, 1);
            r.Add(2, 1, DateTime.UtcNow);
            Assert.IsTrue(r.Contains(2, 1));
            r.Remove(2, 1);
            r.Add(3, 1, DateTime.UtcNow);
            Assert.IsFalse(r.Contains(2, 1)); // due to one relation, 2 now has a new parent
            r.Remove(3, 1);
            r.Add(1, 2, DateTime.UtcNow);            
            Assert.IsTrue(r.Contains(1, 2));
            Assert.ThrowsException<ItemAlreadyInRelationException>(() => r.Add(1, 2, DateTime.UtcNow));            
        }
        [TestMethod]
        public void TestOneToOne() {
            var r = new OneToOneIndex(setRegister);
            r.Add(1, 1, DateTime.UtcNow);
            r.Remove(1, 1);
            r.Add(2, 1, DateTime.UtcNow);
            Assert.IsTrue(r.Contains(2, 1));
            Assert.IsFalse(r.Contains(1, 2));
            r.Remove(2, 1);
            r.Add(3, 1, DateTime.UtcNow);
            Assert.IsFalse(r.Contains(2, 1)); // due to one relation, 2 now has a new parent
            r.Add(1, 2, DateTime.UtcNow);
            Assert.IsTrue(r.Contains(1, 2));
            Assert.ThrowsException<ItemAlreadyInRelationException>(() => r.Add(1, 2, DateTime.UtcNow));
        }
        [TestMethod]
        public void TestOneOne() {
            var r = new OneOneIndex(setRegister);
            r.Add(1, 1, DateTime.UtcNow);
            r.Remove(1, 1);
            r.Add(2, 1, DateTime.UtcNow);
            Assert.IsTrue(r.Contains(2, 1));
            Assert.IsTrue(r.Contains(1, 2));
            r.Remove(2, 1);
            r.Add(3, 1, DateTime.UtcNow);
            r.Remove(3, 1);
            Assert.IsFalse(r.Contains(2, 1)); // due to one relation, 2 now has a new parent
            r.Add(1, 2, DateTime.UtcNow);
            Assert.IsTrue(r.Contains(1, 2));
            Assert.ThrowsException<ItemAlreadyInRelationException>(() => r.Add(1, 2, DateTime.UtcNow));
        }
    }
}