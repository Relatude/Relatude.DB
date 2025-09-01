using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Query.Data;

namespace Relatude.DB.Query.Expressions;
// not currently used
public class SearchPropertyExpression : IExpression {
    public SearchPropertyExpression(PropertyReferenceExpression propertyReference, string searchText) {
        PropertyReference = propertyReference;
        SearchText = searchText;
    }
    public PropertyReferenceExpression PropertyReference { get; }
    public string SearchText { get; }
    public object Evaluate(IVariables vars) {
        //var inputExpression = (IStoreNodeDataCollection)_input.Evaluate(vars);
        throw new NotImplementedException();
        //var r = new SearchQueryResultData(SearchText, _input.TotalCount, (IStoreNodeDataCollection)inputExpression, _input.Datamodel);
        //return r;
    }
}
