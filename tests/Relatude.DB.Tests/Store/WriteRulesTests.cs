using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.WriteRuleModels;

namespace Relatude.WriteRuleModels {
    public enum WrColor { Red = 0, Green = 1 }

    [Node]
    public class WrPost {
        public Guid Id { get; set; }
        [StringProperty(RegularExpression = @"^[a-z0-9-]+$")]
        public string Slug { get; set; } = "";
        [StringProperty(LegalValues = new[] { "draft", "published" })]
        public string Status { get; set; } = "";
        [IntegerProperty(LegalValues = new[] { 1, 2, 3 }, DefaultValue = 1)]
        public int Stars { get; set; } = 1;
        public WrColor Color { get; set; }
        public string Free { get; set; } = "";
    }

    [Node(MaxNoInstances = 2)]
    public class WrCapped {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }

    [Node(MinNoInstances = 2)]
    public class WrKept {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }

    // a limit on a base type counts the nodes of every type inheriting from it
    [Node(MaxNoInstances = 2)]
    public interface IWrLimited {
        string Name { get; set; }
    }
    [Node]
    public class WrLimitedA : IWrLimited {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }
    [Node]
    public class WrLimitedB : IWrLimited {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }

    [Node]
    public class WrPlain {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }
}

namespace Relatude.WriteRuleModels.Invalid {
    [Node]
    public class WrBadPattern {
        public Guid Id { get; set; }
        [StringProperty(RegularExpression = "([a-z")]
        public string Code { get; set; } = "";
    }
    [Node(MinNoInstances = 3, MaxNoInstances = 2)]
    public class WrBadLimits {
        public Guid Id { get; set; }
    }
}

namespace Relatude.Store {

    /// <summary>
    /// The rules of a property (RegularExpression, LegalValues) and of a node type (MinNoInstances,
    /// MaxNoInstances), declared with attributes: that they reach the model, and that a write breaking
    /// them fails and leaves the store as it was.
    /// </summary>
    [TestClass]
    public class WriteRulesTests {

        static NodeStore open(params Type[] types) {
            var dm = new Datamodel();
            foreach (var t in types) dm.Add(t);
            return new NodeStore(DataStoreLocal.Open(dm));
        }
        static void assertRefused(Action write, string because) {
            try {
                write();
            } catch (Exception error) {
                var messages = string.Join(" | ", chain(error).Select(e => e.Message));
                Assert.IsTrue(messages.Contains(because, StringComparison.Ordinal), "Refused for another reason: " + messages);
                return;
            }
            Assert.Fail("The write was not refused (expected: " + because + ")");
        }
        static IEnumerable<Exception> chain(Exception? e) {
            for (; e != null; e = e.InnerException) yield return e;
        }

        // ---- the model ----

        [TestMethod]
        public void Attributes_ReachTheModel() {
            var dm = new Datamodel();
            dm.Add<WrPost>();
            dm.Add<WrCapped>();
            dm.Add<WrKept>();
            dm.Add<WrPlain>();
            dm.EnsureInitalization();
            var post = dm.NodeTypes.Values.Single(t => t.CodeName == nameof(WrPost));
            var slug = (StringPropertyModel)post.Properties.Values.Single(p => p.CodeName == nameof(WrPost.Slug));
            var status = (StringPropertyModel)post.Properties.Values.Single(p => p.CodeName == nameof(WrPost.Status));
            var free = (StringPropertyModel)post.Properties.Values.Single(p => p.CodeName == nameof(WrPost.Free));
            Assert.AreEqual(@"^[a-z0-9-]+$", slug.RegularExpression);
            CollectionAssert.AreEqual(new[] { "draft", "published" }, status.LegalValues);
            Assert.IsNull(free.RegularExpression);
            Assert.IsNull(free.LegalValues);

            var capped = dm.NodeTypes.Values.Single(t => t.CodeName == nameof(WrCapped));
            Assert.AreEqual(2, capped.MaxNoInstances);
            Assert.AreEqual(int.MinValue, capped.MinNoInstances, "an unset minimum stays the model's 'no limit'");
            var kept = dm.NodeTypes.Values.Single(t => t.CodeName == nameof(WrKept));
            Assert.AreEqual(2, kept.MinNoInstances);
            Assert.AreEqual(int.MaxValue, kept.MaxNoInstances);
            var plain = dm.NodeTypes.Values.Single(t => t.CodeName == nameof(WrPlain));
            Assert.AreEqual(int.MinValue, plain.MinNoInstances);
            Assert.AreEqual(int.MaxValue, plain.MaxNoInstances);
        }

        [TestMethod]
        public void UnusedRules_LeaveTheModelJsonAsItWas() {
            // a changed model checksum rebuilds the state and every index, so a model that sets none of
            // the new members must serialize as before: no LegalValues on a string, no limits on a type
            var dm = new Datamodel();
            dm.Add<WrPlain>();
            dm.EnsureInitalization();
            var json = DatamodelJson.Serialize(dm);
            Assert.IsFalse(json.Contains("\"LegalValues\"", StringComparison.Ordinal), "LegalValues must be left out while null");
            Assert.IsTrue(json.Contains("\"MinNoInstances\": " + int.MinValue, StringComparison.Ordinal));

            var withRules = new Datamodel();
            withRules.Add<WrPost>();
            withRules.EnsureInitalization();
            var back = DatamodelJson.Deserialize(DatamodelJson.Serialize(withRules));
            back.EnsureInitalization();
            var status = (StringPropertyModel)back.Properties.Values.Single(p => p.CodeName == nameof(WrPost.Status));
            CollectionAssert.AreEqual(new[] { "draft", "published" }, status.LegalValues, "legal values survive the JSON round trip");
        }

        [TestMethod]
        public void InvalidRules_FailWhenTheModelIsBuilt() {
            var e1 = Assert.Throws<Exception>(() => new Datamodel().Add<Relatude.WriteRuleModels.Invalid.WrBadPattern>());
            StringAssert.Contains(string.Join(" ", chain(e1).Select(e => e.Message)), "not a valid regular expression");
            var e2 = Assert.Throws<Exception>(() => new Datamodel().Add<Relatude.WriteRuleModels.Invalid.WrBadLimits>());
            StringAssert.Contains(string.Join(" ", chain(e2).Select(e => e.Message)), "MaxNoInstances");
        }

        // ---- property rules on write ----

        [TestMethod]
        public void RegularExpression_FromAttribute_IsEnforced() {
            using var store = open(typeof(WrPost));
            var post = new WrPost { Id = Guid.NewGuid(), Slug = "hello-world" };
            store.Insert(post);
            assertRefused(() => store.Insert(new WrPost { Slug = "Hello World" }), "does not match the pattern");
            assertRefused(() => store.Insert(new WrPost()), "does not match the pattern"); // the empty default is matched too
            Assert.AreEqual(1, store.Query<WrPost>().Count());

            post.Slug = "not a slug";
            assertRefused(() => store.Update(post), "does not match the pattern");
            Assert.AreEqual("hello-world", store.Get<WrPost>(post.Id).Slug, "a refused update leaves the stored value");
        }

        [TestMethod]
        public void StringLegalValues_AreEnforced_EmptyIsAllowed() {
            using var store = open(typeof(WrPost));
            store.Insert(new WrPost { Slug = "a", Status = "draft" });
            store.Insert(new WrPost { Slug = "b", Status = "" }); // empty: no value yet
            assertRefused(() => store.Insert(new WrPost { Slug = "c", Status = "Draft" }), "is not one of its legal values"); // ordinal
            assertRefused(() => store.Insert(new WrPost { Slug = "d", Status = "archived" }), "is not one of its legal values");
            Assert.AreEqual(2, store.Query<WrPost>().Count());
        }

        [TestMethod]
        public void IntegerLegalValues_AreEnforcedForPlainIntegers_NotForEnums() {
            using var store = open(typeof(WrPost));
            store.Insert(new WrPost { Slug = "a", Stars = 3 });
            assertRefused(() => store.Insert(new WrPost { Slug = "b", Stars = 4 }), "is not one of its legal values");
            assertRefused(() => store.Insert(new WrPost { Slug = "c", Stars = 0 }), "is not one of its legal values");
            // an enum's legal values are its members, for display: any int is still a value of it
            store.Insert(new WrPost { Slug = "d", Color = (WrColor)99 });
            Assert.AreEqual((WrColor)99, store.Query<WrPost>().Where(p => p.Slug == "d").Execute().Single().Color);
            Assert.AreEqual(2, store.Query<WrPost>().Count());
        }

        // ---- instance limits ----

        [TestMethod]
        public void MaxNoInstances_RefusesTheInsertAboveIt() {
            using var store = open(typeof(WrCapped));
            var a = new WrCapped { Id = Guid.NewGuid(), Name = "a" };
            store.Insert(a);
            store.Insert(new WrCapped { Name = "b" });
            assertRefused(() => store.Insert(new WrCapped { Name = "c" }), "MaxNoInstances");
            Assert.AreEqual(2, store.Query<WrCapped>().Count(), "the refused transaction is rolled back");

            // a transaction that inserts three at once is refused as a whole
            store.Delete(a.Id);
            var t = store.CreateTransaction();
            t.Insert(new WrCapped { Name = "d" });
            t.Insert(new WrCapped { Name = "e" });
            assertRefused(() => t.Execute(), "MaxNoInstances");
            Assert.AreEqual(1, store.Query<WrCapped>().Count());

            // updates do not move the count; at the limit, a replacement in one transaction is fine
            store.Insert(new WrCapped { Name = "f" });
            var b = store.Query<WrCapped>().Where(x => x.Name == "b").Execute().Single();
            b.Name = "b2";
            store.Update(b);
            var swap = store.CreateTransaction();
            swap.Delete(b.Id);
            swap.Insert(new WrCapped { Name = "g" });
            swap.Execute();
            Assert.AreEqual(2, store.Query<WrCapped>().Count());
        }

        [TestMethod]
        public void MinNoInstances_RefusesTheDeleteBelowIt_ButTheCountMayStartBelow() {
            using var store = open(typeof(WrKept));
            var a = new WrKept { Id = Guid.NewGuid(), Name = "a" };
            store.Insert(a); // 1 of at least 2: allowed, the count goes up
            assertRefused(() => store.Delete(a.Id), "MinNoInstances");
            var b = new WrKept { Id = Guid.NewGuid(), Name = "b" };
            store.Insert(b);
            assertRefused(() => store.Delete(b.Id), "MinNoInstances");
            Assert.AreEqual(2, store.Query<WrKept>().Count());
            store.Insert(new WrKept { Name = "c" });
            store.Delete(b.Id); // 3 -> 2
            Assert.AreEqual(2, store.Query<WrKept>().Count());
        }

        [TestMethod]
        public void InstanceLimits_CountTheTypesInheritingFromTheLimitedType() {
            using var store = open(typeof(IWrLimited), typeof(WrLimitedA), typeof(WrLimitedB));
            store.Insert(new WrLimitedA { Name = "a" });
            store.Insert(new WrLimitedB { Name = "b" });
            assertRefused(() => store.Insert(new WrLimitedA { Name = "c" }), "MaxNoInstances");
            assertRefused(() => store.Insert(new WrLimitedB { Name = "d" }), "MaxNoInstances");
            Assert.AreEqual(2, store.Query<IWrLimited>().Count());
        }

        [TestMethod]
        public void MaxNoInstances_SetAfterTheNodes_BlocksOnlyInserts() {
            var folder = Path.Combine(Path.GetTempPath(), "relatude-write-rules-" + Guid.NewGuid().ToString("N"));
            try {
                var unlimited = new Datamodel();
                unlimited.Add<WrCapped>();
                unlimited.NodeTypes.Values.Single(t => t.CodeName == nameof(WrCapped)).MaxNoInstances = int.MaxValue;
                using (var store = new NodeStore(DataStoreLocal.Open(unlimited, new SettingsLocal(), new IOProviderDisk(folder)))) {
                    for (var i = 0; i < 3; i++) store.Insert(new WrCapped { Name = "n" + i }, flushToDisk: true);
                }
                var limited = new Datamodel();
                limited.Add<WrCapped>(); // MaxNoInstances = 2, with three nodes stored
                using (var store = new NodeStore(DataStoreLocal.Open(limited, new SettingsLocal(), new IOProviderDisk(folder)))) {
                    Assert.AreEqual(3, store.Query<WrCapped>().Count(), "the nodes already there stay");
                    var n0 = store.Query<WrCapped>().Where(x => x.Name == "n0").Execute().Single();
                    n0.Name = "renamed";
                    store.Update(n0); // the count does not move
                    assertRefused(() => store.Insert(new WrCapped { Name = "n3" }), "MaxNoInstances");
                    store.Delete(n0.Id); // moving towards the limit is fine
                    Assert.AreEqual(2, store.Query<WrCapped>().Count());
                    assertRefused(() => store.Insert(new WrCapped { Name = "n4" }), "MaxNoInstances");
                }
            } finally {
                try { Directory.Delete(folder, true); } catch { }
            }
        }
    }
}
