using WAF.Datamodels.Properties;
using WAF.Query.Data;
using WAF.Query.Expressions;

namespace WAF.Query.Methods {
    public class PageMethod : IExpression {
        readonly IExpression _input;
        public PageMethod(IExpression input, int pageIndex, int pageSize) {
            _input = input;
            _pageIndex = pageIndex;
            _pageSize = pageSize;
        }
        readonly int _pageIndex;
        readonly int _pageSize;
        public object Evaluate(IVariables vars) {
            if (_input is FacetMethod facetMethod) {
                facetMethod.SetPaging(_pageIndex, _pageSize);
                return _input.Evaluate(vars);
            }
            if (_input is SearchMethod searchMethod) {
                searchMethod.SetPaging(_pageIndex, _pageSize);
                return _input.Evaluate(vars);
            }
            var result = _input.Evaluate(vars);
            if (result is ICollectionData collection) return collection.Page(_pageIndex, _pageSize);
            throw new Exception("Cannot page result of type " + result.GetType().Name + ". Expected ICollectionData or FacetMethod.");
        }
        public override string ToString() => _input + ".Page(" + _pageIndex + ", " + _pageSize + ")";
    }
}
