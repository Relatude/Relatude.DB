using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores;
using Relatude.DB.Nodes;

namespace Relatude.Querying;

#region plain reference test datamodel
// No relation attributes and no relation classes: with AutoDeduceRelations off (the default)
// these members must classify as Reference/References properties, not auto-created relations.
[Node]
public class PlainRefProduct {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty(Indexed = true)]
    public string Name { get; set; } = "";
    public PlainRefBrand? Brand { get; set; }                      // Reference, Object shape
    public PlainRefTag[]? TagsArray { get; set; }                  // References, Array shape
    public List<PlainRefTag>? TagsList { get; set; }               // References, List shape
    public IEnumerable<PlainRefTag>? TagsEnumerable { get; set; }  // References, Enumerable shape
    public ICollection<PlainRefTag>? TagsCollection { get; set; }  // References, Collection shape (generic interface)
}
[Node]
public class PlainRefBrand {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty]
    public string Name { get; set; } = "";
}
[Node]
public class PlainRefTag {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty]
    public string Name { get; set; } = "";
}
// native relation properties must stay relations even with AutoDeduceRelations off:
[Node]
public class PlainNativeNode {
    [PublicIdProperty]
    public Guid Id { get; set; }
    public PlainNativeTree.Parent Parent { get; set; } = new();
    public PlainNativeTree.Children Children { get; set; } = new();
}
public class PlainNativeTree : OneToMany<PlainNativeNode, PlainNativeNode> {
    public class Parent : One { }
    public class Children : Many { }
}
// minimal pair for the auto-deduce-on test (two same-typed collections would be ambiguous):
[Node]
public class AutoRelParent {
    [PublicIdProperty]
    public Guid Id { get; set; }
    public IEnumerable<AutoRelChild>? Children { get; set; }
}
[Node]
public class AutoRelChild {
    [PublicIdProperty]
    public Guid Id { get; set; }
    public AutoRelParent? Parent { get; set; }
}
// interface models exercise the generated proxy classes (lazy loading of plain references):
public interface IPlainRefArticle {
    Guid Id { get; set; }
    string Title { get; set; }
    IPlainRefAuthor? Author { get; set; }
    IPlainRefAuthor[]? CoAuthors { get; set; }
}
public interface IPlainRefAuthor {
    Guid Id { get; set; }
    string Name { get; set; }
}
#endregion

[TestClass]
public class PlainReferenceTests {

    [TestMethod]
    public void Classification_DefaultOff_PlainMembersBecomeReferences() {
        var dm = new Datamodel();
        dm.Add<PlainRefProduct>(); // default: autoDeduceRelations = false
        dm.Add<PlainRefBrand>();
        dm.Add<PlainRefTag>();
        dm.EnsureInitalization();

        Assert.AreEqual(0, dm.Relations.Count, "No relations should be auto created. ");
        var product = dm.NodeTypesByFullName[typeof(PlainRefProduct).FullName!];

        var brand = product.AllPropertiesByName["Brand"];
        Assert.AreEqual(PropertyType.Reference, brand.PropertyType);
        var brandRef = (ReferencePropertyModel)brand;
        Assert.AreEqual(ReferenceValueType.Object, brandRef.ReferenceValueType);
        Assert.IsTrue(brandRef.NodeTypes.Contains(dm.NodeTypesByFullName[typeof(PlainRefBrand).FullName!].Id));

        void assertReferences(string name, ReferenceValueType expectedShape) {
            var p = product.AllPropertiesByName[name];
            Assert.AreEqual(PropertyType.References, p.PropertyType, name);
            var refs = (ReferencesPropertyModel)p;
            Assert.AreEqual(expectedShape, refs.ReferenceValueType, name);
            Assert.IsTrue(refs.NodeTypes.Contains(dm.NodeTypesByFullName[typeof(PlainRefTag).FullName!].Id), name);
        }
        assertReferences("TagsArray", ReferenceValueType.Array);
        assertReferences("TagsList", ReferenceValueType.List);
        assertReferences("TagsEnumerable", ReferenceValueType.Enumerable);
        assertReferences("TagsCollection", ReferenceValueType.Collection);
    }

    [TestMethod]
    public void Classification_DefaultOff_NativeRelationPropertiesStayRelations() {
        var dm = new Datamodel();
        dm.Add<PlainNativeNode>(); // default: autoDeduceRelations = false
        dm.Add<PlainNativeTree>(); // relation classes are not pulled in by Add<T>, only by namespace scans
        dm.EnsureInitalization();

        Assert.AreEqual(1, dm.Relations.Count, "The explicitly declared relation class should still be part of the model. ");
        var node = dm.NodeTypesByFullName[typeof(PlainNativeNode).FullName!];
        Assert.AreEqual(PropertyType.Relation, node.AllPropertiesByName["Parent"].PropertyType);
        Assert.AreEqual(PropertyType.Relation, node.AllPropertiesByName["Children"].PropertyType);
    }

    [TestMethod]
    public void Classification_AutoDeduceOn_PlainMembersBecomeRelations() {
        var dm = new Datamodel();
        dm.Add<AutoRelParent>(autoDeduceRelations: true);
        dm.Add<AutoRelChild>(autoDeduceRelations: true);
        dm.EnsureInitalization();

        var parent = dm.NodeTypesByFullName[typeof(AutoRelParent).FullName!];
        var child = dm.NodeTypesByFullName[typeof(AutoRelChild).FullName!];
        Assert.AreEqual(PropertyType.Relation, parent.AllPropertiesByName["Children"].PropertyType);
        Assert.AreEqual(PropertyType.Relation, child.AllPropertiesByName["Parent"].PropertyType);
        Assert.AreEqual(1, dm.Relations.Count, "The two opposite members should pair into one auto created relation. ");
    }

    static NodeStore openStore() {
        var dm = new Datamodel();
        dm.Add<PlainRefProduct>();
        dm.Add<PlainRefBrand>();
        dm.Add<PlainRefTag>();
        return new NodeStore(DataStoreLocal.Open(dm));
    }

    [TestMethod]
    public void ClassModel_SaveAndIncludeLoad() {
        using var store = openStore();
        var brand = new PlainRefBrand { Id = Guid.NewGuid(), Name = "Acme" };
        var t1 = new PlainRefTag { Id = Guid.NewGuid(), Name = "T1" };
        var t2 = new PlainRefTag { Id = Guid.NewGuid(), Name = "T2" };
        var t3 = new PlainRefTag { Id = Guid.NewGuid(), Name = "T3" };
        store.Insert(brand);
        store.Insert(new[] { t1, t2, t3 });
        store.Insert(new PlainRefProduct {
            Name = "P1",
            Brand = brand,
            TagsArray = [t1, t2],
            TagsList = [t2, t3],
            TagsEnumerable = [t3],
            TagsCollection = [t3, t1],
        });

        // without include, plain reference members stay null:
        var plain = store.Query<PlainRefProduct>().Execute().Single();
        Assert.IsNull(plain.Brand);
        Assert.IsNull(plain.TagsArray);
        Assert.IsNull(plain.TagsList);
        Assert.IsNull(plain.TagsEnumerable);
        Assert.IsNull(plain.TagsCollection);

        // with include, they are populated in stored order:
        var loaded = store.Query<PlainRefProduct>()
            .Include(p => p.Brand)
            .Include(p => p.TagsArray)
            .Include(p => p.TagsList)
            .Include(p => p.TagsEnumerable!)
            .Include(p => p.TagsCollection!)
            .Execute().Single();
        Assert.IsNotNull(loaded.Brand);
        Assert.AreEqual(brand.Id, loaded.Brand!.Id);
        CollectionAssert.AreEqual(new[] { t1.Id, t2.Id }, loaded.TagsArray!.Select(t => t.Id).ToArray());
        CollectionAssert.AreEqual(new[] { t2.Id, t3.Id }, loaded.TagsList!.Select(t => t.Id).ToArray());
        CollectionAssert.AreEqual(new[] { t3.Id }, loaded.TagsEnumerable!.Select(t => t.Id).ToArray());
        CollectionAssert.AreEqual(new[] { t3.Id, t1.Id }, loaded.TagsCollection!.Select(t => t.Id).ToArray());
    }

    [TestMethod]
    public void ClassModel_UpdateWithoutTouchingPreservesReferences() {
        using var store = openStore();
        var brand = new PlainRefBrand { Id = Guid.NewGuid(), Name = "Acme" };
        var t1 = new PlainRefTag { Id = Guid.NewGuid(), Name = "T1" };
        store.Insert(brand);
        store.Insert(t1);
        store.Insert(new PlainRefProduct { Name = "P1", Brand = brand, TagsArray = [t1] });

        // load without include (reference members are null) and update another property:
        var plain = store.Query<PlainRefProduct>().Execute().Single();
        plain.Name = "P1 renamed";
        store.Update(plain);

        // null means "leave stored value unchanged", so the references must survive:
        var loaded = store.Query<PlainRefProduct>().Include(p => p.Brand).Include(p => p.TagsArray).Execute().Single();
        Assert.AreEqual("P1 renamed", loaded.Name);
        Assert.IsNotNull(loaded.Brand);
        Assert.AreEqual(brand.Id, loaded.Brand!.Id);
        CollectionAssert.AreEqual(new[] { t1.Id }, loaded.TagsArray!.Select(t => t.Id).ToArray());
    }

    [TestMethod]
    public void ClassModel_EmptyCollectionClearsReferences() {
        using var store = openStore();
        var t1 = new PlainRefTag { Id = Guid.NewGuid(), Name = "T1" };
        store.Insert(t1);
        store.Insert(new PlainRefProduct { Name = "P1", TagsArray = [t1] });

        var loaded = store.Query<PlainRefProduct>().Include(p => p.TagsArray).Execute().Single();
        CollectionAssert.AreEqual(new[] { t1.Id }, loaded.TagsArray!.Select(t => t.Id).ToArray());
        loaded.TagsArray = [];
        store.Update(loaded);

        var reloaded = store.Query<PlainRefProduct>().Include(p => p.TagsArray).Execute().Single();
        Assert.AreEqual(0, reloaded.TagsArray!.Length);
    }

    [TestMethod]
    public void ClassModel_ReferencedNodeMustExistBeforeInsert() {
        using var store = openStore();
        var brand = new PlainRefBrand { Name = "NewBrand" }; // no id yet, not inserted
        try {
            store.Insert(new PlainRefProduct { Name = "P1", Brand = brand });
            Assert.Fail("Referencing a node that is not in the store should throw (same rule as wrapper references). ");
        } catch (AssertFailedException) {
            throw;
        } catch {
            // expected: reference targets must exist at write time
        }
        Assert.AreNotEqual(Guid.Empty, brand.Id, "Saving the referencing node should stamp an id on the referenced object. ");

        store.Insert(brand); // persist the target under the stamped id, then retry
        store.Insert(new PlainRefProduct { Name = "P1", Brand = brand });
        var loaded = store.Query<PlainRefProduct>().Include(p => p.Brand).Execute().Single();
        Assert.IsNotNull(loaded.Brand);
        Assert.AreEqual(brand.Id, loaded.Brand!.Id);
    }

    static NodeStore openInterfaceStore() {
        var dm = new Datamodel();
        dm.Add<IPlainRefArticle>();
        dm.Add<IPlainRefAuthor>();
        return new NodeStore(DataStoreLocal.Open(dm));
    }

    [TestMethod]
    public void InterfaceModel_LazyLoadsPlainReferences() {
        using var store = openInterfaceStore();
        var author1 = store.Create<IPlainRefAuthor>();
        author1.Name = "A1";
        var author2 = store.Create<IPlainRefAuthor>();
        author2.Name = "A2";
        store.Insert(author1);
        store.Insert(author2);

        var article = store.Create<IPlainRefArticle>();
        article.Title = "Hello";
        article.Author = author1;
        article.CoAuthors = [author2, author1];
        store.Insert(article);

        // proxies lazy load plain references from the store, no include needed:
        var loaded = store.Query<IPlainRefArticle>().Execute().Single();
        Assert.IsNotNull(loaded.Author);
        Assert.AreEqual(author1.Id, loaded.Author!.Id);
        CollectionAssert.AreEqual(new[] { author2.Id, author1.Id }, loaded.CoAuthors!.Select(a => a.Id).ToArray());

        // updating an unrelated property must not lose the references:
        loaded.Title = "Hello again";
        store.Update(loaded);
        var reloaded = store.Query<IPlainRefArticle>().Execute().Single();
        Assert.AreEqual("Hello again", reloaded.Title);
        Assert.AreEqual(author1.Id, reloaded.Author!.Id);
        CollectionAssert.AreEqual(new[] { author2.Id, author1.Id }, reloaded.CoAuthors!.Select(a => a.Id).ToArray());
    }
}
