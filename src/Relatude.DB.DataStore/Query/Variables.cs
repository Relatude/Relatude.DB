using Relatude.DB.Datamodels;
using System.Text;

namespace Relatude.DB.Query {
    public interface IVariables {
        Variables CreateScope();
        object Get(string name, QueryContext? ctx = null);
    }
    public class Variable {
        public object? Value;
        public Func<Metrics, QueryContext, object>? Evaluate;
    }
    public class Metrics {
        public int NodeCount;
        public int UniqueNodeCount;
        public int DiskReads;
        public int NodesReadFromDisk;
    }
    public class Variables : IVariables {
        readonly Variables? _parentScope;
        QueryContext? _ctx;
        Metrics _metrics = new();
        public Metrics Metrics => _metrics;
        readonly Dictionary<string, Variable> _vars = new();
        public Variables() { }
        private Variables(Variables parentScope, QueryContext ctx) {
            _parentScope = parentScope;
            _ctx = ctx;
        }
        public Variables CreateRootScope(IEnumerable<Parameter> parameters, QueryContext ctx) {
            var v = new Variables(this, ctx);
            foreach (var p in parameters) {
                if (p.Value is Func<Metrics, QueryContext, object> func) {
                    v.DeclarerAndSetCallback(p.Name, func);
                } else {
                    v.DeclarerAndSetConstant(p.Name, p.Value);
                }
            }
            v._metrics = new();
            return v;
        }
        public Variables CreateScope() {
            if (_ctx == null) throw new Exception("Cannot create child scope without context. ");
            var v = new Variables(this, _ctx) {
                _metrics = this._metrics // inherit metrics
            };
            return v;
        }
        public object Get(string name, QueryContext? ctx = null) {
            ctx ??= _ctx;
            if (ctx == null) throw new Exception("Cannot get variable without context. ");
            if (_vars.TryGetValue(name, out var vari)) {
                if (vari.Value != null) return vari.Value;
                if (vari.Evaluate != null) return vari.Evaluate(_metrics, ctx);
                throw new Exception("Variable \"" + name + "\" is not set. ");
            }
            if (_parentScope != null) return _parentScope.Get(name, ctx);
            if (name == "null") throw new Exception("Variable \"" + name + "\" is null. ");
            throw new Exception("Idenitier \"" + name + "\" is unknown. ");
        }
        // Declares a new variable name and sets its value
        public void DeclarerAndSetConstant(string name, object? exp) {
            _vars.Add(name, new() { Value = exp });
        }
        // Declares a new variable name and sets its value
        public void DeclarerAndSetCallback(string name, Func<Metrics, QueryContext, object> callback) {
            _vars.Add(name, new() { Evaluate = callback });
        }
        // Sets the value of an existing variable
        public void Set(string name, object? exp) {
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
