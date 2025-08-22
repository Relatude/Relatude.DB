
using WAF.Common;
using WAF.Datamodels;
using WAF.Datamodels.Properties;
using WAF.Serialization;
using System.Text;

namespace WAF.Query.Data;

public class ObjectCollection : ICollectionData {
    public List<object> Objects;
    public ObjectCollection() {
        Objects = new();
    }
    ObjectCollection(List<object> objects) {
        Objects = objects;
    }
    public void Add(object obj) {
        Objects.Add(obj);
    }
    public object Evaluate(IVariables vars) {
        throw new NotImplementedException();
    }
    public IEnumerable<object> Values {
        get {
            foreach (var r in Objects) {
                yield return r;
            }
        }
    }
    public double DurationMs { get; set; }
    public int Count => Objects.Count;
    public int TotalCount => Objects.Count;
    public int PageIndex { get; set; }
    public int? PageSize { get; set; }
    public int PageIndexUsed => PageIndex; // Not checked
    public int? PageSizeUsed => null; // Not checked
    public ICollectionData Filter(bool[] keep) =>
        new ObjectCollection(Objects.Where((o, index) => keep[index]).ToList());

    public static object DeSerialize(Datamodel datamodel, Stream stream) {
        throw new NotImplementedException();
    }
    public ICollectionData Page(int pageIndex, int pageSize) {
        throw new NotImplementedException();
    }
    public PropertyType GetPropertyType(string name) {
        throw new NotImplementedException();
    }
    public ICollectionData ReOrder(IEnumerable<int> newPos) {
        var newObjects = new List<object>(Count);
        foreach (var i in newPos) {
            newObjects.Add(Objects[i]);
        }
        return new ObjectCollection(newObjects);
    }
    public ICollectionData Take(int take) {
        throw new NotImplementedException();
    }
    public ICollectionData Skip(int skip) {
        throw new NotImplementedException();
    }
}
