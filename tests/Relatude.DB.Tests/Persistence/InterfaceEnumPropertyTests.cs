using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Nodes;

namespace Relatude.Persistence;

public enum TicketStatus { None = 0, Open = 1, Resolved = 2, Closed = 3 }

[Node]
public interface IEnumTicket {
    Guid Id { get; set; }
    string Title { get; set; }
    TicketStatus Status { get; set; }
}

// Regression tests for enum properties on interface node types: enums are stored as boxed int
// (IntegerPropertyModel), so the generated proxy's GetValue<TEnum> "is T" check was false for
// values loaded from the store and always fell back to the property default. The proxy write
// path had the opposite asymmetry, storing the boxed enum itself instead of an int.
[TestClass]
public class InterfaceEnumPropertyTests {

    static Datamodel getDatamodel() {
        var dm = new Datamodel();
        dm.Add<IEnumTicket>();
        return dm;
    }

    [TestMethod]
    public void EnumProperty_RoundTripsThroughStore() {
        using var store = new NodeStore(DataStoreLocal.Open(getDatamodel(), null, new IOProviderMemory()));

        var ticket = store.Create<IEnumTicket>();
        ticket.Title = "T1";
        ticket.Status = TicketStatus.Resolved;
        Assert.AreEqual(TicketStatus.Resolved, ticket.Status, "Freshly set value should read back on the same proxy. ");
        store.Insert(ticket);

        var loaded = store.Get<IEnumTicket>(ticket.Id);
        Assert.AreEqual(TicketStatus.Resolved, loaded.Status, "Value loaded from the store should not fall back to the default. ");

        loaded.Status = TicketStatus.Closed;
        store.Update(loaded);
        var reloaded = store.Get<IEnumTicket>(ticket.Id);
        Assert.AreEqual(TicketStatus.Closed, reloaded.Status);
    }

    [TestMethod]
    public void EnumProperty_SurvivesLogReplay() {
        var io = new IOProviderMemory();
        var datamodel = getDatamodel();
        var store = new NodeStore(DataStoreLocal.Open(datamodel, null, io));

        var ticket = store.Create<IEnumTicket>();
        ticket.Title = "T1";
        ticket.Status = TicketStatus.Open;
        store.Insert(ticket);
        var id = ticket.Id;
        store.Dispose();

        // reopen from the transaction log, forcing the value through serialization both ways:
        store = new NodeStore(DataStoreLocal.Open(datamodel, null, io));
        var loaded = store.Get<IEnumTicket>(id);
        Assert.AreEqual(TicketStatus.Open, loaded.Status);
        store.Dispose();
    }

    [TestMethod]
    public void EnumProperty_UnsetReadsBackDefault() {
        using var store = new NodeStore(DataStoreLocal.Open(getDatamodel(), null, new IOProviderMemory()));

        var ticket = store.Create<IEnumTicket>();
        ticket.Title = "T1";
        store.Insert(ticket);

        var loaded = store.Get<IEnumTicket>(ticket.Id);
        Assert.AreEqual(TicketStatus.None, loaded.Status);
    }
}
