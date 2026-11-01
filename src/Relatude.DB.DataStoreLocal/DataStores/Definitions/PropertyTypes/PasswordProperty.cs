using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Definitions.PropertyTypes;

/// <summary>
/// A password (<see cref="PasswordValue"/>), with no index of any kind. What a value written from outside does:
/// <list type="bullet">
/// <item>a password set: the stored form the store made for it before the transaction took its lock
/// (<see cref="DataStoreLocal.TryGetMadePassword"/>), or made here when nothing made one;</item>
/// <item>a password cleared: no password;</item>
/// <item>any other value, a password as read: taken by a node being inserted, so copies keep the password,
/// and left out of an update, which keeps the stored password. An object read before the password changed and
/// saved after it, or a version of the node written back, cannot put an old password back.</item>
/// </list>
/// </summary>
internal class PasswordProperty : Property {
    DataStoreLocal? _store;
    readonly PasswordPropertyModel _model;
    public PasswordProperty(PasswordPropertyModel pm, Definition def) : base(pm, def) {
        _model = pm;
    }
    internal override void Initalize(DataStoreLocal store, Definition def, SettingsLocal config, IIOProvider io, AIEngine? ai) {
        _store = store;
    }
    public override PropertyType PropertyType => PropertyType.Password;
    public override object ForceValueType(object value, out bool changed) => PasswordPropertyModel.ForceValueType(value, out changed);
    public override object TransformFromOuterToInnerValue(object value, INodeData? oldNodeData) {
        if (value is not PasswordValue password) throw new Exception("Value is not a password value. ");
        if (PasswordValue.IsCleared(password)) return PasswordValue.FromStored(string.Empty);
        if (password.HasChange) {
            if (_store == null) throw new InvalidOperationException("The password property is not initialized. ");
            return _store.TryGetMadePassword(password, out var made) ? made : _store.MakePassword(_model, password);
        }
        if (oldNodeData == null) return PasswordValue.FromStored(PasswordValue.GetStored(password)); // an insert takes it, as a copy does
        if (oldNodeData.TryGetValue(Id, out var old) && old is PasswordValue oldPassword) return oldPassword;
        return PasswordValue.FromStored(string.Empty);
    }
    public override void ValidateValue(object value, INodeData node) {
        if (value is not PasswordValue password) throw new Exception("Value must be a password value. ");
        // a change reaches here only when values are not transformed: it must never be stored, let alone logged
        if (password.HasChange) throw new Exception("A password can only be set by a transaction written from outside the store, which makes its hash or encryption. ");
    }
    public override bool CanBeFacet() => false;
    public override bool AreValuesEqual(object v1, object v2) {
        if (v1 is PasswordValue p1 && v2 is PasswordValue p2) return PasswordValue.AreValuesEqual(p1, p2);
        return false;
    }
}
