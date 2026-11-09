using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Definitions.PropertyTypes;

internal class EmbeddedProperty : Property {
    public EmbeddedProperty(PropertyModel pm, Definition def) : base(pm, def) {
    }
    public override PropertyType PropertyType => PropertyType.Embedded;
    public override object ForceValueType(object value, out bool changed) {
        if (value is IInnerNodeDataMap ndm) {
            changed = false;
            return ndm;
        }
        changed = true;
        return null!;
    }
    public override void ValidateValue(object value, INodeData node) {
    }
    bool _anyAbstract;
    internal override void Initalize(DataStoreLocal store, Definition def, SettingsLocal config, IIOProvider io, AIEngine? ai) {
        _anyAbstract = def.NodeTypes.Values.Any(t => t.Model.IsAbstract);
    }
    /// <summary>
    /// The inner nodes written from outside, as a transaction brings them: none of them, at any depth, may be made of an
    /// abstract type (<see cref="NodeTypeModel.IsAbstract"/>). One the node holds already, by its id, is kept as it is, so a
    /// type marked abstract after its inner nodes were made does not stop the node they are in from being saved.
    /// </summary>
    public override object TransformFromOuterToInnerValue(object value, INodeData? oldNodeData) {
        if (!_anyAbstract || value is not IInnerNodeDataMap map) return value;
        HashSet<Guid>? held = null;
        if (oldNodeData != null && oldNodeData.TryGetValue(Id, out var old) && old is IInnerNodeDataMap oldMap) collectIds(oldMap, held = []);
        refuseAbstract(map, held);
        return value;
    }
    static void collectIds(IInnerNodeDataMap map, HashSet<Guid> ids) {
        foreach (var inner in map) {
            if (inner == null) continue;
            if (inner.Id != Guid.Empty) ids.Add(inner.Id);
            foreach (var v in inner.Values) if (v.Value is IInnerNodeDataMap nested) collectIds(nested, ids);
        }
    }
    void refuseAbstract(IInnerNodeDataMap map, HashSet<Guid>? held) {
        foreach (var inner in map) {
            if (inner == null) continue;
            var isNew = inner.Id == Guid.Empty || held == null || !held.Contains(inner.Id);
            if (isNew && Definition.NodeTypes.TryGetValue(inner.NodeType, out var type) && type.Model.IsAbstract) {
                throw new ValueConstraintException(type.Model.FullName + " is abstract, so no node is of the type itself: " + CodeName
                    + " can hold inner nodes of the types inheriting from it, not of " + type.Model.CodeName + ". ", Id);
            }
            foreach (var v in inner.Values) if (v.Value is IInnerNodeDataMap nested) refuseAbstract(nested, held);
        }
    }
    public override bool AreValuesEqual(object v1, object v2) {
        IInnerNodeDataMap? ndm1 = v1 as IInnerNodeDataMap;
        IInnerNodeDataMap? ndm2 = v2 as IInnerNodeDataMap;
        if (ndm1 == null && ndm2 == null) return true;
        if (ndm1 == null || ndm2 == null) return false;
        if (ndm1.Count != ndm2.Count) return false;
        var values1 = ndm1.ToList();
        var values2 = ndm2.ToList();
        for (var i = 0; i < values1.Count; i++) {
            var node1 = values1[i];
            var node2 = values2[i];
            var props1 = node1.Values.ToArray();
            var props2 = node2.Values.ToArray();
            if (props1.Length != props2.Length) return false;
            for (var j = 0; j < props1.Length; j++) {
                var p1 = props1[j];
                var p2 = props2[j];
                if (p1.PropertyId != p2.PropertyId) return false;
                var propDef = Definition.Properties[p1.PropertyId];
                if (!propDef.AreValuesEqual(p1.Value, p2.Value)) return false;
            }
        }
        return true;
    }
}
