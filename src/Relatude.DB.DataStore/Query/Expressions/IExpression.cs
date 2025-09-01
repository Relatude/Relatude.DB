namespace Relatude.DB.Query.Expressions;
public interface IExpression {
    object Evaluate(IVariables vars);
    //void BuildReferenceList(QueryDependencies propsRelsAndTypes);
}
