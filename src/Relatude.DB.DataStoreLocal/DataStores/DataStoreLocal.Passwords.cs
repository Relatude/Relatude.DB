using System.Runtime.CompilerServices;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Transactions;
namespace Relatude.DB.DataStores;

public sealed partial class DataStoreLocal {
    PasswordProtector? _passwordProtector;
    internal PasswordProtector PasswordProtector => _passwordProtector ??= new(_settings.Passwords);
    public IPasswordProtector? Passwords => PasswordProtector;

    // the stored forms this store made for the password changes of a transaction under way, by the change, which
    // PasswordProperty takes in their place
    readonly ConditionalWeakTable<PasswordValue, PasswordValue> _madePasswords = new();
    internal bool TryGetMadePassword(PasswordValue change, out PasswordValue stored) => _madePasswords.TryGetValue(change, out stored!);

    /// <summary>
    /// Makes the stored form of every password the transaction sets, before it takes its lock: an Argon2 hash takes
    /// tens of milliseconds, which no other writer should wait for. A password the property's rules refuse fails
    /// the transaction here, before anything is written. The changes made, or null when it sets none.
    /// </summary>
    List<PasswordValue>? makePasswords(TransactionData transaction) {
        if (!Datamodel.Properties.Values.Any(p => p is PasswordPropertyModel)) return null;
        List<PasswordValue>? made = null;
        try {
            foreach (var action in transaction.Actions) {
                switch (action) {
                    case NodeAction { Node: NodeDataRevisions revisions }:
                        foreach (var revision in revisions.Revisions) makeAll(revision, ref made);
                        break;
                    case NodeAction { Node: NodeDataAbstract node }: // a node or one revision of it; the others carry an id alone
                        makeAll(node, ref made);
                        break;
                    case NodePropertyAction { Values: not null } propertyAction:
                        for (var i = 0; i < propertyAction.PropertyIds.Length; i++) make(propertyAction.PropertyIds[i], propertyAction.Values[i], ref made);
                        break;
                }
            }
        } catch {
            if (made != null) foreach (var change in made) _madePasswords.Remove(change);
            throw;
        }
        return made;
    }
    void makeAll(INodeData node, ref List<PasswordValue>? made) {
        foreach (var kv in node.Values) make(kv.PropertyId, kv.Value, ref made);
    }
    void make(Guid propertyId, object? value, ref List<PasswordValue>? made) {
        if (value is not PasswordValue change || !PasswordValue.TryGetNewPassword(change, out _, out _)) return;
        if (_madePasswords.TryGetValue(change, out _)) return; // one change given to two nodes, or a retry
        if (!Datamodel.Properties.TryGetValue(propertyId, out var property) || property is not PasswordPropertyModel model) return; // refused later, as any value of the wrong type is
        _madePasswords.AddOrUpdate(change, MakePassword(model, change));
        (made ??= []).Add(change);
    }
    /// <summary>The stored form a password change gives, checked against the property's rules.</summary>
    internal PasswordValue MakePassword(PasswordPropertyModel model, PasswordValue change) {
        if (!PasswordValue.TryGetNewPassword(change, out var password, out var storage)) throw new ArgumentException("The value sets no password. ");
        var problem = model.CheckPassword(password, storage);
        if (problem != null) throw new ValueConstraintException(problem, model.Id);
        if (storage == PasswordStorage.Encrypted && !PasswordProtector.CanEncrypt) {
            throw new ValueConstraintException(model.CodeName + " cannot be kept encrypted: the database has no encryption key (Passwords.EncryptionKey in its settings). ", model.Id);
        }
        return PasswordValue.FromStored(PasswordProtector.Protect(password, storage));
    }
}
