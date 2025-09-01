
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Serialization;
using System.Text;

namespace Relatude.DB.Query.Data;
public class ValueCollectionData : ICollectionData {
    List<object> _values = new();
    public ValueCollectionData() { }
    public ValueCollectionData(int initalSize) {
        _values = new List<object>(initalSize);
    }
    ValueCollectionData(List<object> values) {
        _values = values;
    }
    public int Count => _values.Count;
    public void Add(object v) => _values.Add(v);
    public object Evaluate(IVariables vars) {
        throw new NotImplementedException();
    }
    public IEnumerable<object> Values {
        get {
            foreach (var r in _values) yield return r;
        }
    }

    public int TotalCount => _values.Count;

    public int PageIndex { get; set; }
    public int? PageSize { get; set; }
    public double DurationMs { get; set; }

    public int PageIndexUsed => PageIndex; // Not checked
    public int? PageSizeUsed => null; // Not checked

    public ICollectionData Filter(bool[] keep) => new ValueCollectionData(_values.Where((o, index) => keep[index]).ToList());

    public void Serialize(Datamodel datamodel, Stream stream) {
        throw new NotImplementedException();
    }
    public static object DeSerialize(Datamodel datamodel, Stream stream) {
        throw new NotImplementedException();
    }
    public void BuildTypeScriptTypeInfo(StringBuilder sb) {
        throw new NotImplementedException();
    }
    public PropertyType GetPropertyType(string name) {
        throw new NotImplementedException();
    }
    public ICollectionData ReOrder(IEnumerable<int> newPos) {
        throw new NotImplementedException();
    }
    public ICollectionData Page(int pageIndex, int pageSize) {
        throw new NotImplementedException();
    }
    public ICollectionData Take(int take) {
        throw new NotImplementedException();
    }
    public ICollectionData Skip(int skip) {
        throw new NotImplementedException();
    }
}
