namespace Relatude.DB.Query.Parsing.Tokens;
public enum TokenTypes {
    Empty,
    Variable,
    ValueConstant,
    ExpressionBracket,
    MethodCall,
    LambdaDeclaration,
    AnonymousObject,
    ObjectConstruction,
    OperatorExpression,
    PreFixOperatorExpression,
}
