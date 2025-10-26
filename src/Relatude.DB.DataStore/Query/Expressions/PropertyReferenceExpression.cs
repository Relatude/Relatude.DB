using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Query.Data;
namespace Relatude.DB.Query.Expressions {
    public class PropertyReferenceExpression : IExpression {
        public readonly VariableReferenceExpression SourceObject;
        public readonly string PropertyName;
        public PropertyReferenceExpression(VariableReferenceExpression sourceObject, string propertyName) {
            SourceObject = sourceObject;
            PropertyName = propertyName;
        }
        public object? Evaluate(IVariables vars) {
            var input = SourceObject.Evaluate(vars);
            if (input is ObjectData inputObj) {
                return inputObj.GetValue(PropertyName);
            } else if (input is IStoreNodeDataCollection storeObject) {
                return storeObject;
            } else if (input is IStoreNodeData nodeObject) {
                var value = nodeObject.GetValue(PropertyName);
                //if (value == null) throw new NullReferenceException($"Property {PropertyName} is null");
                return value!;
            } else {
                throw new NotImplementedException();
            }
        }
        public override string ToString() => SourceObject + "." + PropertyName;
    }
}
