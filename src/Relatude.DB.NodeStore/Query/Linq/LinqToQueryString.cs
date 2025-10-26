using Relatude.DB.Query;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace Relatude.DB.Query.Linq;
internal class LinqToQueryString {
    public static string Get(Expression expression) {
        var query = ExpressionStringBuilder.ToCSharp(expression);
        return query;
    }

}

public sealed class ExpressionStringBuilder : ExpressionVisitor {
    private readonly StringBuilder _sb = new();
    private readonly Stack<int> _precStack = new();

    public static string ToCSharp(Expression expr) {
        var b = new ExpressionStringBuilder();
        b.VisitCore(expr, Prec.Top); // root: no outer parentheses
        return b._sb.ToString();
    }

    // ---------- Precedence ----------
    private static class Prec {
        public const int Top = 10000;            // never parenthesize against Top
        public const int Lambda = 10;            // =>
        public const int Conditional = 20;       // ?:
        public const int Coalesce = 30;          // ??
        public const int OrElse = 40;            // ||
        public const int AndAlso = 50;           // &&
        public const int Or = 60;                // |
        public const int Xor = 70;               // ^
        public const int And = 80;               // &
        public const int Eq = 90;                // == !=
        public const int Rel = 100;              // < > <= >=
        public const int Shift = 110;            // << >>
        public const int Add = 120;              // + -
        public const int Mul = 130;              // * / %
        public const int Unary = 140;            // ! ~ + -
        public const int CallAccess = 150;       // . () [] new
        public const int Primary = 160;          // literals, names
    }

    private bool NeedParens(int nodePrec) => ParentPrec != Prec.Top && nodePrec < ParentPrec;

    private void VisitCore(Expression node, int currentNodePrec) {
        _precStack.Push(currentNodePrec);
        Visit(node);
        _precStack.Pop();
    }

    private int ParentPrec => _precStack.Count == 0 ? Prec.Top : _precStack.Peek();

    private void EmitWithParensIfNeeded(Expression child, int childPrec) {
        bool parens = NeedParens(childPrec);
        if (parens) _sb.Append('(');
        VisitCore(child, childPrec);
        if (parens) _sb.Append(')');
    }

    // ---------- Core overrides ----------
    protected override Expression VisitLambda<T>(Expression<T> node) {
        bool parens = NeedParens(Prec.Lambda);
        if (parens) _sb.Append('(');

        if (node.Parameters.Count == 1) {
            _sb.Append(node.Parameters[0].Name);
        } else {
            _sb.Append('(');
            for (int i = 0; i < node.Parameters.Count; i++) {
                if (i > 0) _sb.Append(", ");
                _sb.Append(node.Parameters[i].Name);
            }
            _sb.Append(')');
        }

        _sb.Append(" => ");
        VisitCore(node.Body, Prec.Lambda);

        if (parens) _sb.Append(')');
        return node;
    }

    protected override Expression VisitParameter(ParameterExpression node) {
        _sb.Append(node.Name ?? "p");
        return node;
    }

    protected override Expression VisitConstant(ConstantExpression node) {
        _sb.Append(ConstantToCode(node.Value, node.Type));
        return node;
    }

    protected override Expression VisitConditional(ConditionalExpression node) {
        bool parens = NeedParens(Prec.Conditional);
        if (parens) _sb.Append('(');

        EmitWithParensIfNeeded(node.Test, Prec.Conditional);
        _sb.Append(" ? ");
        EmitWithParensIfNeeded(node.IfTrue, Prec.Conditional);
        _sb.Append(" : ");
        EmitWithParensIfNeeded(node.IfFalse, Prec.Conditional);

        if (parens) _sb.Append(')');
        return node;
    }

    protected override Expression VisitUnary(UnaryExpression node) {
        // Omit Convert / ConvertChecked
        if (node.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked) {
            VisitCore(node.Operand, ParentPrec);
            return node;
        }

        var (op, isPost) = node.NodeType switch {
            ExpressionType.Negate => ("-", false),
            ExpressionType.UnaryPlus => ("+", false),
            ExpressionType.Not => ("!", false),
            ExpressionType.OnesComplement => ("~", false),
            ExpressionType.PreIncrementAssign => ("++", false),
            ExpressionType.PreDecrementAssign => ("--", false),
            ExpressionType.PostIncrementAssign => ("++", true),
            ExpressionType.PostDecrementAssign => ("--", true),
            _ => (null, false)
        };

        if (op == null) {
            _sb.Append(node.NodeType.ToString()).Append('(');
            Visit(node.Operand);
            _sb.Append(')');
            return node;
        }

        bool parens = NeedParens(Prec.Unary);
        if (parens) _sb.Append('(');
        if (!isPost) _sb.Append(op);
        VisitCore(node.Operand, Prec.Unary);
        if (isPost) _sb.Append(op);
        if (parens) _sb.Append(')');
        return node;
    }

    protected override Expression VisitBinary(BinaryExpression node) {
        if (node.NodeType == ExpressionType.Coalesce) return EmitInfix(node, "??", Prec.Coalesce);
        if (node.NodeType == ExpressionType.OrElse) return EmitInfix(node, "||", Prec.OrElse);
        if (node.NodeType == ExpressionType.AndAlso) return EmitInfix(node, "&&", Prec.AndAlso);

        var map = new Dictionary<ExpressionType, (string op, int prec)>
        {
            { ExpressionType.Or, ("|", Prec.Or) },
            { ExpressionType.ExclusiveOr, ("^", Prec.Xor) },
            { ExpressionType.And, ("&", Prec.And) },
            { ExpressionType.Equal, ("==", Prec.Eq) },
            { ExpressionType.NotEqual, ("!=", Prec.Eq) },
            { ExpressionType.LessThan, ("<", Prec.Rel) },
            { ExpressionType.LessThanOrEqual, ("<=", Prec.Rel) },
            { ExpressionType.GreaterThan, (">", Prec.Rel) },
            { ExpressionType.GreaterThanOrEqual, (">=", Prec.Rel) },
            { ExpressionType.LeftShift, ("<<", Prec.Shift) },
            { ExpressionType.RightShift, (">>", Prec.Shift) },
            { ExpressionType.Add, ("+", Prec.Add) },
            { ExpressionType.Subtract, ("-", Prec.Add) },
            { ExpressionType.Multiply, ("*", Prec.Mul) },
            { ExpressionType.Divide, ("/", Prec.Mul) },
            { ExpressionType.Modulo, ("%", Prec.Mul) },
        };

        if (map.TryGetValue(node.NodeType, out var info))
            return EmitInfix(node, info.op, info.prec);

        if (node.NodeType == ExpressionType.ArrayIndex) {
            bool parens = NeedParens(Prec.CallAccess);
            if (parens) _sb.Append('(');
            EmitWithParensIfNeeded(node.Left, Prec.CallAccess);
            _sb.Append('[');
            Visit(node.Right);
            _sb.Append(']');
            if (parens) _sb.Append(')');
            return node;
        }

        // Fallback for uncommon binary nodes in LINQ (assignments etc.)
        _sb.Append('(').Append(node.NodeType).Append(' ');
        Visit(node.Left);
        _sb.Append(", ");
        Visit(node.Right);
        _sb.Append(')');
        return node;
    }

    private Expression EmitInfix(BinaryExpression node, string op, int prec) {
        bool parens = NeedParens(prec);
        if (parens) _sb.Append('(');
        EmitWithParensIfNeeded(node.Left, prec);
        _sb.Append(' ').Append(op).Append(' ');
        EmitWithParensIfNeeded(node.Right, prec);
        if (parens) _sb.Append(')');
        return node;
    }

    protected override Expression VisitMember(MemberExpression node) {
        if (node.Expression == null) {
            bool parensS = NeedParens(Prec.CallAccess);
            if (parensS) _sb.Append('(');
            _sb.Append(GetTypeDisplayName(node.Member.DeclaringType)).Append('.').Append(node.Member.Name);
            if (parensS) _sb.Append(')');
            return node;
        }

        bool parens = NeedParens(Prec.CallAccess);
        if (parens) _sb.Append('(');
        EmitWithParensIfNeeded(node.Expression, Prec.CallAccess);
        _sb.Append('.').Append(node.Member.Name);
        if (parens) _sb.Append(')');
        return node;
    }

    private static bool IsExtension(MethodInfo m)
        => m.IsStatic && m.IsDefined(typeof(ExtensionAttribute), inherit: false);

    protected override Expression VisitMethodCall(MethodCallExpression node) {
        bool parens = NeedParens(Prec.CallAccess);
        if (parens) _sb.Append('(');

        // Extension method? Render as receiver.Method(args...)
        if (node.Method.IsStatic && IsExtension(node.Method) && node.Arguments.Count > 0) {
            var receiver = node.Arguments[0];
            EmitWithParensIfNeeded(receiver, Prec.CallAccess);
            _sb.Append('.').Append(node.Method.Name).Append('(');
            for (int i = 1; i < node.Arguments.Count; i++) {
                if (i > 1) _sb.Append(", ");
                Visit(node.Arguments[i]);
            }
            _sb.Append(')');
            if (parens) _sb.Append(')');
            return node;
        }

        // Instance method
        if (node.Object != null) {
            EmitWithParensIfNeeded(node.Object, Prec.CallAccess);
            _sb.Append('.').Append(node.Method.Name);
        } else {
            // Static non-extension
            _sb.Append(GetTypeDisplayName(node.Method.DeclaringType))
               .Append('.').Append(node.Method.Name);
        }

        _sb.Append('(');
        for (int i = 0; i < node.Arguments.Count; i++) {
            if (i > 0) _sb.Append(", ");
            Visit(node.Arguments[i]);
        }
        _sb.Append(')');

        if (parens) _sb.Append(')');
        return node;
    }

    protected override Expression VisitNew(NewExpression node) {
        // Anonymous type?
        if (IsAnonymousType(node.Type) && node.Members is { Count: > 0 }) {
            bool parens = NeedParens(Prec.CallAccess);
            if (parens) _sb.Append('(');

            _sb.Append("new { ");
            for (int i = 0; i < node.Arguments.Count; i++) {
                if (i > 0) _sb.Append(", ");
                var memberName = node.Members![i].Name;
                var arg = node.Arguments[i];

                var guessed = TryGuessName(arg);
                if (string.Equals(memberName, guessed, StringComparison.Ordinal)) {
                    Visit(arg); // shorthand: new { name }
                } else {
                    _sb.Append(memberName).Append(" = ");
                    Visit(arg);
                }
            }
            _sb.Append(" }");

            if (parens) _sb.Append(')');
            return node;
        }

        // Regular object construction
        bool par = NeedParens(Prec.CallAccess);
        if (par) _sb.Append('(');

        _sb.Append("new ").Append(GetTypeDisplayName(node.Type)).Append('(');
        for (int i = 0; i < node.Arguments.Count; i++) {
            if (i > 0) _sb.Append(", ");
            Visit(node.Arguments[i]);
        }
        _sb.Append(')');

        if (par) _sb.Append(')');
        return node;
    }

    protected override Expression VisitNewArray(NewArrayExpression node) {
        bool parens = NeedParens(Prec.CallAccess);
        if (parens) _sb.Append('(');

        var elemType = node.Type.GetElementType() ?? node.Type.GetElementTypeSafe();

        if (node.NodeType == ExpressionType.NewArrayInit) {
            _sb.Append("new ")
              .Append(GetTypeDisplayName(elemType))
              .Append("[] { ");
            for (int i = 0; i < node.Expressions.Count; i++) {
                if (i > 0) _sb.Append(", ");
                Visit(node.Expressions[i]);
            }
            _sb.Append(" }");
        } else {
            _sb.Append("new ")
              .Append(GetTypeDisplayName(elemType))
              .Append('[');
            for (int i = 0; i < node.Expressions.Count; i++) {
                if (i > 0) _sb.Append(", ");
                Visit(node.Expressions[i]);
            }
            _sb.Append(']');
        }

        if (parens) _sb.Append(')');
        return node;
    }

    protected override Expression VisitListInit(ListInitExpression node) {
        Visit(node.NewExpression);
        _sb.Append(" { ");
        for (int i = 0; i < node.Initializers.Count; i++) {
            if (i > 0) _sb.Append(", ");
            var init = node.Initializers[i];
            if (init.Arguments.Count == 1) {
                Visit(init.Arguments[0]);
            } else {
                _sb.Append('{');
                for (int j = 0; j < init.Arguments.Count; j++) {
                    if (j > 0) _sb.Append(", ");
                    Visit(init.Arguments[j]);
                }
                _sb.Append('}');
            }
        }
        _sb.Append(" }");
        return node;
    }

    protected override Expression VisitMemberInit(MemberInitExpression node) {
        // Anonymous types are immutable; MemberInit won't occur for them.
        Visit(node.NewExpression);
        _sb.Append(" { ");
        for (int i = 0; i < node.Bindings.Count; i++) {
            if (i > 0) _sb.Append(", ");
            if (node.Bindings[i] is MemberAssignment ma) {
                _sb.Append(ma.Member.Name).Append(" = ");
                Visit(ma.Expression);
            }
        }
        _sb.Append(" }");
        return node;
    }

    protected override Expression VisitInvocation(InvocationExpression node) {
        bool parens = NeedParens(Prec.CallAccess);
        if (parens) _sb.Append(')');
        EmitWithParensIfNeeded(node.Expression, Prec.CallAccess);
        _sb.Append('(');
        for (int i = 0; i < node.Arguments.Count; i++) {
            if (i > 0) _sb.Append(", ");
            Visit(node.Arguments[i]);
        }
        _sb.Append(')');
        if (parens) _sb.Append(')');
        return node;
    }

    protected override Expression VisitIndex(IndexExpression node) {
        bool parens = NeedParens(Prec.CallAccess);
        if (parens) _sb.Append('(');
        EmitWithParensIfNeeded(node.Object!, Prec.CallAccess);
        _sb.Append('[');
        for (int i = 0; i < node.Arguments.Count; i++) {
            if (i > 0) _sb.Append(", ");
            Visit(node.Arguments[i]);
        }
        _sb.Append(']');
        if (parens) _sb.Append(')');
        return node;
    }

    protected override Expression VisitExtension(Expression node) {
        // Reduce custom provider nodes into standard nodes when possible.
        if (node.CanReduce) {
            var reduced = node.Reduce();
            if (!ReferenceEquals(reduced, node)) {
                Visit(reduced);
                return node;
            }
        }

        // Last resort: keep output valid and readable.
        _sb.Append("/* ").Append(node.ToString()).Append(" */");
        return node;
    }

    // ---------- Utilities ----------
    private static string GetTypeDisplayName(Type? t) {
        if (t == null) return "object";
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>))
            return GetTypeDisplayName(t.GetGenericArguments()[0]) + "?";

        if (!t.IsGenericType) {
            return t switch {
                var _ when t == typeof(void) => "void",
                var _ when t == typeof(bool) => "bool",
                var _ when t == typeof(byte) => "byte",
                var _ when t == typeof(sbyte) => "sbyte",
                var _ when t == typeof(short) => "short",
                var _ when t == typeof(ushort) => "ushort",
                var _ when t == typeof(int) => "int",
                var _ when t == typeof(uint) => "uint",
                var _ when t == typeof(long) => "long",
                var _ when t == typeof(ulong) => "ulong",
                var _ when t == typeof(float) => "float",
                var _ when t == typeof(double) => "double",
                var _ when t == typeof(decimal) => "decimal",
                var _ when t == typeof(string) => "string",
                var _ when t == typeof(object) => "object",
                var _ when t == typeof(char) => "char",
                _ => t.Name
            };
        }

        var def = t.GetGenericTypeDefinition();
        var name = def.Name;
        var tick = name.IndexOf('`');
        if (tick >= 0) name = name[..tick];
        var args = t.GetGenericArguments().Select(GetTypeDisplayName);
        return $"{name}<{string.Join(", ", args)}>";
    }

    private static bool IsAnonymousType(Type t) {
        // Heuristic: compiler-generated generic class named like <>f__AnonymousType*
        return Attribute.IsDefined(t, typeof(CompilerGeneratedAttribute), false)
               && t.IsGenericType
               && t.Name.Contains("AnonymousType", StringComparison.Ordinal)
               && (t.Attributes & TypeAttributes.NotPublic) == TypeAttributes.NotPublic;
    }

    private static Expression UnwrapConvert(Expression e)
        => e is UnaryExpression u && u.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked
            ? UnwrapConvert(u.Operand)
            : e;

    private static string? TryGuessName(Expression e) {
        e = UnwrapConvert(e);
        return e switch {
            ParameterExpression p => p.Name,
            MemberExpression m when m.Expression is ParameterExpression => m.Member.Name,
            _ => null
        };
    }

    private static string EscapeString(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";

    private static string EscapeChar(char c) =>
        c switch {
            '\\' => "'\\\\'",
            '\'' => "'\\''",
            '\n' => "'\\n'",
            '\r' => "'\\r'",
            '\t' => "'\\t'",
            _ => $"'{c}'"
        };

    private static string ConstantToCode(object? value, Type declaredType) {
        if (value is null) return "null";

        if (declaredType.IsEnum) {
            var name = Enum.GetName(declaredType, value) ?? value.ToString();
            return declaredType.Name + "." + name;
        }

        switch (value) {
            case string s: return EscapeString(s);
            case char ch: return EscapeChar(ch);
            case bool b: return b ? "true" : "false";
            case float f:
                if (float.IsNaN(f)) return "float.NaN";
                if (float.IsPositiveInfinity(f)) return "float.PositiveInfinity";
                if (float.IsNegativeInfinity(f)) return "float.NegativeInfinity";
                return f.ToString("R", CultureInfo.InvariantCulture) + "f";
            case double d:
                if (double.IsNaN(d)) return "double.NaN";
                if (double.IsPositiveInfinity(d)) return "double.PositiveInfinity";
                if (double.IsNegativeInfinity(d)) return "double.NegativeInfinity";
                return d.ToString("R", CultureInfo.InvariantCulture);
            case decimal m: return m.ToString(CultureInfo.InvariantCulture) + "m";
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                return Convert.ToString(value, CultureInfo.InvariantCulture)!;
            case DateTime dt: return $"new DateTime({dt.Ticks}, DateTimeKind.{dt.Kind})";
            case Guid g: return $"new Guid(\"{g}\")";
        }

        if (value is IEnumerable enumerable && value is not string) {
            var items = new List<string>();
            foreach (var it in enumerable)
                items.Add(ConstantToCode(it, it?.GetType() ?? typeof(object)));
            return $"new [] {{ {string.Join(", ", items)} }}";
        }

        return $"/* const {GetTypeDisplayName(value.GetType())} */";
    }
}

// small helper for .NETs where GetElementType may be null
internal static class TypeExt {
    public static Type? GetElementTypeSafe(this Type t)
        => t.HasElementType ? t.GetElementType() : null;
}
