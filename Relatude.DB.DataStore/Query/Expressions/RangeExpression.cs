using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores;
using Relatude.DB.Query.Data;
namespace Relatude.DB.Query.Expressions {
    public class RangeExpression : IExpression {
        public readonly VariableReferenceExpression SourceObject;
        public readonly string From;
        public readonly string To;
        public readonly string PropertyName;
        public RangeExpression(string property, string from, string to) {
            var parts = property.Split('.');
            SourceObject = new VariableReferenceExpression(parts[0]);
            PropertyName = parts.Skip(1).Single();
            From = from;
            To = to;
        }
        public object Evaluate(IVariables vars) {
            throw new NotImplementedException();
        }
        public override string ToString() => SourceObject + "." + PropertyName + ".InRange(" + From + ", " + To + ")";
    }
}
