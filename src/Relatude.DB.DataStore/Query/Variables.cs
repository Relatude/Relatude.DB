using Relatude.DB.Datamodels;
using System.Text;

namespace Relatude.DB.Query {
    public interface IVariables {
        Variables CreateScope();
        public QueryContext Context { get; }
        object Get(string name);
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
        public Variables? ParentScope { get; set; }
        private QueryContext? _context;
        public QueryContext Context { get => _context ?? throw new Exception("No QueryContext available in this scope. "); set => _context = value; }
        private Metrics? _metrics;
        public Metrics Metrics { get => _metrics ?? throw new Exception("No Metrics available in this scope. "); set => _metrics = value; }
        readonly Dictionary<string, Variable> _vars = new();
        private Variables() { }
        private Variables(Variables parentScope, QueryContext ctx, Metrics metrics) {
            ParentScope = parentScope;
            Context = ctx;
            Metrics = metrics;
        }
        public static Variables CreateRootScope() {
            // Context and Metrics is null at root scope, will be set at subsequent scopes
            return new Variables();
        }
        public Variables CreateQueryBaseScope(IEnumerable<Parameter> parameters, QueryContext ctx) {
            var v = new Variables(this, ctx, new Metrics());
            foreach (var p in parameters) {
                if (p.Value is Func<Metrics, QueryContext, object> func) {
                    v.DeclarerAndSetCallback(p.Name, func);
                } else {
                    v.DeclarerAndSetConstant(p.Name, p.Value);
                }
            }
            return v;
        }
        public Variables CreateScope() => new(this, Context, Metrics);
        public object Get(string name) {
            return get(name, Context, Metrics);
        }
        private object get(string name, QueryContext ctx, Metrics metrics) {
            if (_vars.TryGetValue(name, out var vari)) {
                if (vari.Value != null) return vari.Value;
                if (vari.Evaluate != null) return vari.Evaluate(metrics, ctx);
                throw new Exception("Variable \"" + name + "\" is not set. ");
            }
            if (ParentScope != null) return ParentScope.get(name, ctx, metrics);
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
            if (ParentScope != null) sb.AppendLine(ParentScope.ToString());
            foreach (var kv in _vars) sb.AppendLine(kv.Key);
            return sb.ToString();
        }
    }
}
