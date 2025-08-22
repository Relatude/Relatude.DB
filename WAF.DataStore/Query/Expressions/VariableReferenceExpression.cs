using WAF.Datamodels.Properties;
using WAF.Query.Data;

namespace WAF.Query.Expressions {
    public class VariableReferenceExpression : IExpression {
        public string Name { get; }
        public VariableReferenceExpression(string name) {
            Name = name;
        }
        public object Evaluate(IVariables vars) => vars.Get(Name);
        public override string ToString() => Name;
    }
}
