using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;
using System.Xml.Schema;

namespace Relatude.DB.Query {
    public interface IVariables {
        Variables CreateScope();
        object Get(string name);
    }
    public class Variable {
        public object? Value;
        public Func<Metrics, object>? Evaluate;
    }
    public class Metrics {
        public int NodeCount;
        public int UniqueNodeCount;
        public int DiskReads;
        public int NodesReadFromDisk;
    }
    public class Variables : IVariables {
        readonly Variables? _parentScope;
        Metrics _metrics = new();
        public Metrics Metrics => _metrics;
        readonly Dictionary<string, Variable> _vars = new();
        public Variables() { }
        private Variables(Variables parentScope) {
            _parentScope = parentScope;
        }
        public Variables CreateRootScope(IEnumerable<Parameter> parameters) {
            var v = new Variables(this);
            foreach (var p in parameters) v.DeclarerAndSet(p.Name, p.Value);
            v._metrics = new();
            return v;
        }
        public Variables CreateScope() {
            var v = new Variables(this) {
                _metrics = this._metrics // inherit metrics
            };
            return v;
        }
        public object Get(string name) {
            if (_vars.TryGetValue(name, out var vari)) {
                if (vari.Value != null) return vari.Value;
                if (vari.Evaluate != null) return vari.Evaluate(_metrics);
                throw new Exception("Variable \"" + name + "\" is not set. ");
            }
            if (_parentScope != null) return _parentScope.Get(name);
            if (name == "null") throw new Exception("Variable \"" + name + "\" is null. ");
            throw new Exception("Idenitier \"" + name + "\" is unknown. ");
        }
        // Declares and new variable name and sets its value
        public void DeclarerAndSet(string name, object exp) {
            _vars.Add(name, new() { Value = exp });
        }
        // Declares and new variable name and sets its value
        public void DeclarerAndSet(string name, Func<Metrics, object> callback) {
            _vars.Add(name, new() { Evaluate = callback });
        }
        // Sets the value of an existing variable
        public void Set(string name, object exp) {
            _vars[name].Value = exp;
        }
        public void Declare(string name) {
            _vars.Add(name, new());
        }
        public override string ToString() {
            var sb = new StringBuilder();
            if (_parentScope != null) sb.AppendLine(_parentScope.ToString());
            foreach (var kv in _vars) sb.AppendLine(kv.Key);
            return sb.ToString();
        }
    }
}
