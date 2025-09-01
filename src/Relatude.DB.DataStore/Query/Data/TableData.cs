
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Serialization;
using System.Text;

namespace Relatude.DB.Query.Data;
// Table data is an optimized version of a collection of objects
// It assumes that all objects (rows) have the same properties
// It stores the properties in a separate array and then stores the values in a 2D array
public class TableData : ICollectionData {
    public KeyValuePair<string, PropertyType>[] Properties;
    public List<object[]> Rows;
    public int Count => Rows.Count;
    public TableData() {
        Properties = Array.Empty<KeyValuePair<string, PropertyType>>();
        Rows = new();
    }
    public TableData(KeyValuePair<string, PropertyType>[] properties, List<object[]> rows) {
        Properties = properties;
        Rows = rows;
    }
    public IEnumerable<object> Values {
        get {
            for (int i = 0; i < Rows.Count; i++) {
                yield return new ObjectData(Properties, Rows[i]);
            }
        }
    }
    public IEnumerable<ObjectData> Objects {
        get {
            for (int i = 0; i < Rows.Count; i++) {
                yield return new ObjectData(Properties, Rows[i]);
            }
        }
    }
    public int AddColumn(string name, PropertyType dataType) {
        if (Properties == null) throw new Exception();
        var kv = new KeyValuePair<string, PropertyType>(name, dataType);
        Properties = Properties.Append(kv).ToArray();
        return Properties.Length - 1;
    }
    public void SetColumns(KeyValuePair<string, PropertyType>[] cols) {
        Properties = cols;
    }
    public int AddColumn(KeyValuePair<string, PropertyType> kv) {
        Properties = Properties.Append(kv).ToArray();
        return Properties.Length - 1;
    }
    public void AddRow(params object[] row) {
        Rows.Add(row);
    }

    public int TotalCount => Rows.Count;
    public int PageIndex { get; set; }
    public int? PageSize { get; set; }
    public double DurationMs { get; set; }

    public int PageIndexUsed => throw new NotImplementedException();

    public int? PageSizeUsed => throw new NotImplementedException();

    public int IndexOfCol(string name) {
        for (int i = 0; i < Properties.Length; i++) {
            if (Properties[i].Key == name) return i;
        }
        return -1;
    }
    public object Evaluate(IVariables vars) {
        return this;
    }
    public ICollectionData Filter(bool[] keep) => new TableData(Properties, Rows.Where((r, i) => keep[i]).ToList());
    public void Serialize(Datamodel datamodel, Stream stream) {
        throw new NotImplementedException();
    }
    public static object DeSerialize(Datamodel datamodel, Stream stream) {
        throw new NotImplementedException();
    }
    public PropertyType GetPropertyType(string name) {
        for (int i = 0; i < Properties.Length; i++) {
            if (Properties[i].Key == name) return Properties[i].Value;
        }
        throw new Exception("Property not found " + name + ". ");
    }
    public ICollectionData ReOrder(IEnumerable<int> newPos) {
        throw new NotImplementedException();
    }
    public ICollectionData Take(int take) {
        throw new NotImplementedException();
    }
    public ICollectionData Skip(int skip) {
        throw new NotImplementedException();
    }
    public ICollectionData Page(int pageIndex, int pageSize) {
        throw new NotImplementedException();
    }
}