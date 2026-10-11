using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.Nodes;

namespace Relatude.Store;

#region test model: two unrelated interface types, and a third with a type inheriting from it
[Node]
public interface ITlUser {
    [PublicIdProperty]
    Guid Id { get; set; }
    string Name { get; set; }
}
[Node]
public interface ITlCompany {
    [PublicIdProperty]
    Guid Id { get; set; }
    string Name { get; set; }
}
[Node]
public interface ITlLicense {
    [PublicIdProperty]
    Guid Id { get; set; }
    string Name { get; set; }
}
[Node]
public interface ITlLicenseTemplate : ITlLicense {
}
#endregion

// A typed lookup by id often takes the id from outside, from a url say, so the id can belong to a node of
// another type. TryGet<T> only checked that the id existed and then cast, so such an id threw an
// InvalidCastException ("Unable to cast object of type '__ITlUser' to type 'ITlCompany'") where it should have
// answered false, and Get<T> threw that same message, which names neither the node nor its type. Exists<T>
// already asked the datamodel, though it threw for object, and the lookups now all give the same answer.
[TestClass]
public class TypedLookupTests {

    static NodeStore open(out ITlUser user, out ITlCompany company, out ITlLicense license, out ITlLicenseTemplate template) {
        var dm = new Datamodel();
        dm.Add<ITlUser>();
        dm.Add<ITlCompany>();
        dm.Add<ITlLicense>();
        dm.Add<ITlLicenseTemplate>();
        var store = new NodeStore(DataStoreLocal.Open(dm));
        user = store.CreateAndInsert<ITlUser>(u => u.Name = "Ann");
        company = store.CreateAndInsert<ITlCompany>(c => c.Name = "Acme");
        license = store.CreateAndInsert<ITlLicense>(l => l.Name = "Acme's license");
        template = store.CreateAndInsert<ITlLicenseTemplate>(t => t.Name = "Standard");
        return store;
    }

    // Exists and both TryGets answer whether the node with this id is a T, and Get either reads it or says
    // what the node is instead: all four must agree, whichever node and type are asked about
    static void assertIsA<T>(NodeStore store, Guid id, bool isA) where T : class {
        var lookup = "Is node " + id + " a " + typeof(T).Name + "? ";
        Assert.AreEqual(isA, store.Exists<T>(id), lookup + "Exists<T> ");
        Assert.AreEqual(isA, store.TryGet<T>(id, out var byGuid), lookup + "TryGet<T> by public id ");
        Assert.AreEqual(isA, store.TryGet<T>(store.Datastore.GetId(id), out var byInt), lookup + "TryGet<T> by internal id ");
        if (isA) {
            Assert.AreEqual(id, store.Mapper.GetIdGuid(byGuid!));
            Assert.AreEqual(id, store.Mapper.GetIdGuid(byInt!));
            Assert.AreEqual(id, store.Mapper.GetIdGuid(store.Get<T>(id)));
        } else {
            Assert.IsNull(byGuid);
            Assert.IsNull(byInt);
            var error = Assert.ThrowsExactly<InvalidCastException>(() => store.Get<T>(id), lookup + "Get<T> ");
            StringAssert.Contains(error.Message, id.ToString());
            StringAssert.Contains(error.Message, typeof(T).FullName);
        }
    }
    static void assertLookups(NodeStore store, Guid id, bool isUser = false, bool isCompany = false, bool isLicense = false, bool isTemplate = false) {
        assertIsA<object>(store, id, true); // every node is an object
        assertIsA<ITlUser>(store, id, isUser);
        assertIsA<ITlCompany>(store, id, isCompany);
        assertIsA<ITlLicense>(store, id, isLicense);
        assertIsA<ITlLicenseTemplate>(store, id, isTemplate);
    }

    [TestMethod]
    public void EveryTypedLookup_AnswersWhetherTheNodeIsOfTheType() {
        using var store = open(out var user, out var company, out var license, out var template);
        assertLookups(store, user.Id, isUser: true);
        assertLookups(store, company.Id, isCompany: true);
        assertLookups(store, license.Id, isLicense: true); // a license is not a template...
        assertLookups(store, template.Id, isLicense: true, isTemplate: true); // ...but a template is a license
    }

    [TestMethod]
    public void TryGet_IdOfAnotherType_ReturnsFalse() {
        using var store = open(out var user, out _, out _, out _);
        // a user's id where a company's was expected, which threw an InvalidCastException:
        Assert.IsFalse(store.TryGet<ITlCompany>(user.Id, out var company));
        Assert.IsNull(company);
        Assert.IsFalse(store.TryGet<ITlCompany>(store.Datastore.GetId(user.Id), out company));
        Assert.IsNull(company);
    }

    [TestMethod]
    public void TryGet_ReadsANodeOfAnInheritingTypeAsWhatItIs() {
        using var store = open(out _, out _, out _, out var template);
        Assert.IsTrue(store.TryGet<ITlLicense>(template.Id, out var license));
        Assert.IsInstanceOfType<ITlLicenseTemplate>(license);
        Assert.AreEqual("Standard", license.Name);
    }

    [TestMethod]
    public void MissingId_IsNotFoundByAnyLookup() {
        using var store = open(out _, out _, out _, out _);
        var missing = Guid.NewGuid();
        Assert.IsFalse(store.Exists(missing));
        Assert.IsFalse(store.Exists<object>(missing));
        Assert.IsFalse(store.Exists<ITlUser>(missing));
        Assert.IsFalse(store.TryGet(missing, out _));
        Assert.IsFalse(store.TryGet<ITlUser>(missing, out _));
        Assert.IsFalse(store.TryGet<ITlUser>(int.MaxValue, out _));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Get<ITlUser>(missing));
    }

    [TestMethod]
    public async Task Get_IdOfAnotherType_SaysWhatTheNodeIs() {
        using var store = open(out var user, out _, out _, out _);
        var expected = "Node " + user.Id + " is of type " + typeof(ITlUser).FullName + ", not " + typeof(ITlCompany).FullName + ". ";
        Assert.AreEqual(expected, Assert.ThrowsExactly<InvalidCastException>(() => store.Get<ITlCompany>(user.Id)).Message);
        Assert.AreEqual(expected, Assert.ThrowsExactly<InvalidCastException>(() => store.Get<ITlCompany>(store.Datastore.GetId(user.Id))).Message);
        Assert.AreEqual(expected, Assert.ThrowsExactly<InvalidCastException>(() => store.Get<ITlCompany>(new[] { user.Id }).ToList()).Message);
        Assert.AreEqual(expected, (await Assert.ThrowsExactlyAsync<InvalidCastException>(() => store.GetAsync<ITlCompany>(user.Id))).Message);
    }

    [TestMethod]
    public void TryGetFromAddress_NodeOfAnotherType_ReturnsFalse() {
        using var store = open(out var user, out _, out _, out _);
        store.UpdateAddress(user.Id, "ann");
        Assert.IsTrue(store.TryGetFromAddress<ITlUser>("ann", out var found));
        Assert.AreEqual(user.Id, found.Id);
        Assert.IsTrue(store.TryGetFromAddress<object>("ann", out _));
        Assert.IsFalse(store.TryGetFromAddress<ITlCompany>("ann", out var company));
        Assert.IsNull(company);
        Assert.IsFalse(store.TryGetFromAddress<ITlUser>("nobody", out _));
    }

    [TestMethod]
    public void TypeOutsideTheDatamodel_IsAnErrorRatherThanAFalse() {
        // no node can be a string: asking is a mistake in the calling code, reported like Exists<T> always did
        using var store = open(out var user, out _, out _, out _);
        StringAssert.Contains(Assert.ThrowsExactly<Exception>(() => store.Exists<string>(user.Id)).Message, "not part of the datamodel");
        StringAssert.Contains(Assert.ThrowsExactly<Exception>(() => store.TryGet<string>(user.Id, out _)).Message, "not part of the datamodel");
    }
}
