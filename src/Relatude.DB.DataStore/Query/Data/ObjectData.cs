using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using System.Text;

namespace Relatude.DB.Query.Data;

public class ObjectData {
    public double DurationMs { get; set; }
    public object?[] Values;
    public readonly KeyValuePair<string, PropertyType>[] Properties;
    public ObjectData(KeyValuePair<string, PropertyType>[] properties, object?[] values) {
        Properties = properties;
        Values = values;
    }
    public object? GetValue(string name) {
        for (var n = 0; n < Properties.Length; n++) if (Properties[n].Key == name) return Values[n];
        throw new Exception(name + " is unknown. ");
    }
    public PropertyType GetType(string name) {
        for (var n = 0; n < Properties.Length; n++) if (Properties[n].Key == name) return Properties[n].Value;
        throw new Exception(name + " is unknown. ");
    }
    public object Evaluate(IVariables vars) {
        return this;
    }
    object? convertIfNeeded(object? v, Func<INodeData, object?> convertNodeData) {
        if (v is IStoreNodeData nd) return convertNodeData(nd.NodeData);
        if (v is IStoreNodeData[] arr) {
            var v2 = new object?[arr.Length];
            for (var n = 0; n < arr.Length; n++) v2[n] = convertIfNeeded(arr[n], convertNodeData);
            return v2;
        }
        return v;
    }
    public Tuple<string, object?>[] GetValues(Func<INodeData, object?> convertNodeData) {
        var values = new Tuple<string, object?>[Properties.Length];
        for (var n = 0; n < Properties.Length; n++) {
            values[n] = new(Properties[n].Key, convertIfNeeded(Values[n], convertNodeData));
        }
        return values;
    }
    public void Serialize(Datamodel datamodel, Stream stream) {
        throw new NotImplementedException();
    }

    public static object DeSerialize(Datamodel datamodel, Stream stream) {
        throw new NotImplementedException();
    }

    public void BuildTypeScriptTypeInfo(StringBuilder sb) {
        throw new NotImplementedException();
    }

}
