using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Nodes;

namespace Relatude.Transactions.GenericForwardModels {
    // an interface node type, as an application using Create<IUpsertNote>() has: the engine generates the class
    [Node]
    public interface IUpsertNote {
        Guid Id { get; set; }
        [StringProperty(DisplayName = true)]
        string Title { get; set; }
    }
}

namespace Relatude.Transactions {
    using Relatude.Transactions.GenericForwardModels;

    /// <summary>
    /// The NodeStore methods taking an IEnumerable{T}. For an unconstrained T the collection is not covariant to
    /// IEnumerable{object}, so Upsert{T} and ForceUpsert{T} bound to the single-node overload on Transaction and
    /// mapped the whole collection as one node: "IUpsertNote[] is not part of the datamodel".
    /// </summary>
    [TestClass]
    public class GenericCollectionForwardTests {

        static NodeStore open() {
            var dm = new Datamodel();
            dm.Add<IUpsertNote>();
            return new NodeStore(DataStoreLocal.Open(dm, null, new IOProviderMemory()));
        }
        static IUpsertNote note(NodeStore store, string title) {
            var n = store.Create<IUpsertNote>();
            n.Title = title;
            return n;
        }
        static void assertStored(NodeStore store, IUpsertNote[] notes) {
            Assert.AreEqual(notes.Length, store.Query<IUpsertNote>().Count());
            foreach (var n in notes) {
                Assert.AreNotEqual(Guid.Empty, n.Id);
                Assert.AreEqual(n.Title, store.Get<IUpsertNote>(n.Id).Title);
            }
        }

        [TestMethod]
        public void Upsert_EnumerableOfInterfaceNodes_InsertsThenUpdatesEach() {
            using var store = open();
            var notes = new[] { note(store, "A"), note(store, "B") };

            store.Upsert(notes);
            assertStored(store, notes);

            notes[0].Title = "A2";
            notes[1].Title = "B2";
            store.Upsert(notes);
            assertStored(store, notes);
        }

        [TestMethod]
        public void ForceUpsert_EnumerableOfInterfaceNodes_InsertsThenOverwritesEach() {
            using var store = open();
            var notes = new List<IUpsertNote> { note(store, "A"), note(store, "B") };

            store.ForceUpsert(notes);
            assertStored(store, notes.ToArray());

            notes[0].Title = "A2";
            notes[1].Title = "B2";
            store.ForceUpsert(notes);
            assertStored(store, notes.ToArray());
        }

        // these forward to Transaction's non-generic IEnumerable overloads, which bind correctly: kept here so a
        // future IEnumerable<object> overload on Transaction cannot quietly break them the same way
        [TestMethod]
        public async Task Update_EnumerableOfInterfaceNodes_WritesEach() {
            using var store = open();
            var notes = new[] { note(store, "A"), note(store, "B") };
            store.Insert(notes);
            assertStored(store, notes);

            var writes = new (string Name, Func<IUpsertNote[], Task> Write)[] {
                ("Update", ns => { store.Update(ns); return Task.CompletedTask; }),
                ("UpdateIfExists", ns => { store.UpdateIfExists(ns); return Task.CompletedTask; }),
                ("UpdateOrFail", ns => { store.UpdateOrFail(ns); return Task.CompletedTask; }),
                ("ForceUpdate", ns => { store.ForceUpdate(ns); return Task.CompletedTask; }),
                ("UpdateAsync", ns => store.UpdateAsync(ns)),
                ("UpdateIfExistsAsync", ns => store.UpdateIfExistsAsync(ns)),
                ("UpdateOrFailAsync", ns => store.UpdateOrFailAsync(ns)),
                ("ForceUpdateAsync", ns => store.ForceUpdateAsync(ns)),
            };
            foreach (var (name, write) in writes) {
                notes[0].Title = "A " + name;
                notes[1].Title = "B " + name;
                await write(notes);
                assertStored(store, notes);
            }
        }
    }
}
