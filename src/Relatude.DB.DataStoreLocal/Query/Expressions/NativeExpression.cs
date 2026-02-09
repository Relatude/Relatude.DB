using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Definitions.PropertyTypes;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using System.Text;

namespace Relatude.DB.Query.Expressions {
    internal interface IBooleanNativeExpression : IExpression {
        public IdSet Filter(IdSet set, QueryContext ctx);
        /// <summary>
        /// Value is used for optimizing the evaluation order of expressions.
        /// The calculation of this value must be fast and is only an estimation.
        /// </summary>
        /// <returns>Worst case maximum number of values returned</returns>
        public int MaxCount();
    }
    internal interface IAndOrNativeExpression : IBooleanNativeExpression {
        public List<IBooleanNativeExpression> Expressions { get; }
    }
    internal class ConstantBooleanNativeExpression(bool value) : IBooleanNativeExpression {
        public bool Value { get; } = value;
        public object? Evaluate(IVariables vars) => Value;
        public IdSet Filter(IdSet set, QueryContext ctx) => Value ? set : IdSet.Empty;
        public int MaxCount() => Value ? int.MaxValue : 0;
    }
    internal class AndNativeExpression : IAndOrNativeExpression {
        public AndNativeExpression() {
            Expressions = new List<IBooleanNativeExpression>();
        }
        public List<IBooleanNativeExpression> Expressions { get; }
        public IdSet Filter(IdSet set, QueryContext ctx) {
            if (Expressions.Count < 2) throw new Exception("Too few expressions in AND statement. ");
            var optimizedOrder = Expressions.OrderBy(e => e.MaxCount());
            foreach (var exp in optimizedOrder) {
                set = exp.Filter(set, ctx);
                var after = set.Count;
                if (set.Count == 0) break;
            }
            return set;
        }
        public int MaxCount() {
            return Expressions.Min(e => e.MaxCount()); // in a and statement, the maximum count is the smallest count of the expressions
        }
        public override string ToString() {
            StringBuilder sb = new StringBuilder();
            sb.Append("(");
            sb.Append(Expressions[0].ToString());
            for (int i = 1; i < Expressions.Count; i++) {
                sb.Append(" AND ");
                sb.Append(Expressions[i].ToString());
            }
            sb.Append(")");
            return sb.ToString();
        }
        public object Evaluate(IVariables vars) => throw new NotImplementedException();
    }
    internal class OrNativeExpression : IAndOrNativeExpression {
        SetRegister _sets;
        public OrNativeExpression(SetRegister sets) {
            Expressions = new List<IBooleanNativeExpression>();
            _sets = sets;
        }
        public List<IBooleanNativeExpression> Expressions { get; }
        public IdSet Filter(IdSet set, QueryContext ctx) {
            if (Expressions.Count < 2) throw new Exception("Too few expressions in OR statement. ");
            var countAll = set.Count;
            var optimizedOrder = Expressions.OrderBy(e => e.MaxCount());
            var firstExpression = optimizedOrder.First();
            var workingSet = firstExpression.Filter(set, ctx);
            foreach (var exp in optimizedOrder.Skip(1)) {
                if (workingSet.Count == countAll) break;
                workingSet = _sets.Union(workingSet, exp.Filter(set, ctx));
            }
            return workingSet;
        }
        public int MaxCount() {
            return Expressions.Max(e => e.MaxCount()); // in a or statement, the maximum count is the largest count of the expressions
        }
        public override string ToString() {
            StringBuilder sb = new StringBuilder();
            sb.Append("(");
            sb.Append(Expressions[0].ToString());
            for (int i = 1; i < Expressions.Count; i++) {
                sb.Append(" OR ");
                sb.Append(Expressions[i].ToString());
            }
            sb.Append(")");
            return sb.ToString();
        }
        public object Evaluate(IVariables vars) => throw new NotImplementedException();
    }
    internal class OperatorExpressionNativeIdProperty : IBooleanNativeExpression {
        IndexOperator _operator;
        public int Id { get; }
        SetRegister _sets;
        public OperatorExpressionNativeIdProperty(int id, IndexOperator op, SetRegister sets) {
            _operator = op;
            _sets = sets;
            Id = id;
        }
        public object Evaluate(IVariables vars) {
            throw new NotImplementedException();
        }
        public IdSet Filter(IdSet set, QueryContext ctx) {
            if (_operator == IndexOperator.Equal) {
                return _sets.WhereEqualId(set, Id);
            } else if (_operator == IndexOperator.NotEqual) {
                return _sets.WhereNotEqualId(set, Id);
            } else {
                throw new NotSupportedException("Id property can only be used with equal or not equal operator");
            }
        }
        public int MaxCount() {
            if (_operator == IndexOperator.Equal) {
                return 1;
            } else if (_operator == IndexOperator.NotEqual) {
                return int.MaxValue;
            } else {
                throw new NotSupportedException("Id property can only be used with equal or not equal operator");
            }
        }
    }
    internal class OperatorExpressionNativeBooleanProperty : IBooleanNativeExpression {
        readonly bool _value;
        readonly BooleanProperty _property;
        IndexOperator _operator;
        public OperatorExpressionNativeBooleanProperty(BooleanProperty property, bool value, IndexOperator op) {
            _property = property;
            _operator = op;
            _value = value;
        }
        public IdSet Filter(IdSet set, QueryContext ctx) {
            if (_property.Indexed && _property.Index != null) {
                return _property.Index.Filter(set, _operator, _value);
            } else {
                throw new NotImplementedException();
            }
        }
        public int MaxCount() {
            if (_property.Indexed && _property.Index != null) {
                return _property.Index.MaxCount(_operator, _value);
            } else {
                throw new NotImplementedException();
            }
        }
        public override string ToString() {
            return IndexOperatorUtil.ToString(_property.CodeName, _operator, _value.ToString());
        }
        public object Evaluate(IVariables vars) => throw new NotImplementedException();
    }
    internal class OperatorExpressionNativeIntegerProperty : IBooleanNativeExpression {
        int _value;
        IntegerProperty _property;
        IndexOperator _operator;
        public OperatorExpressionNativeIntegerProperty(IntegerProperty property, int value, IndexOperator op) {
            _property = property;
            _operator = op;
            _value = value;
        }
        public IdSet Filter(IdSet set, QueryContext ctx) {
            if (_property.Indexed) {
                if (_property.Index == null) throw new NullReferenceException(nameof(_property.Index));
                return _property.Index.Filter(set, _operator, _value);
            } else {
                throw new NotImplementedException();
            }
        }
        public int MaxCount() {
            if (_property.Indexed && _property.Index != null) {
                return _property.Index.MaxCount(_operator, _value);
            } else {
                throw new NotImplementedException();
            }
        }
        public override string ToString() {
            return IndexOperatorUtil.ToString(_property.CodeName, _operator, _value.ToString());
        }
        public object Evaluate(IVariables vars) => throw new NotImplementedException();
    }
    internal class OperatorExpressionNativeFloatProperty : IBooleanNativeExpression {
        float _value;
        FloatProperty _property;
        IndexOperator _operator;
        public OperatorExpressionNativeFloatProperty(FloatProperty property, float value, IndexOperator op) {
            _property = property;
            _operator = op;
            _value = value;
        }
        public IdSet Filter(IdSet set, QueryContext ctx) {
            if (_property.Indexed) {
                if (_property.Index == null) throw new NullReferenceException(nameof(_property.Index));
                return _property.Index.Filter(set, _operator, _value);
            } else {
                throw new NotImplementedException();
            }
        }
        public int MaxCount() {
            if (_property.Indexed && _property.Index != null) {
                return _property.Index.MaxCount(_operator, _value);
            } else {
                throw new NotImplementedException();
            }
        }
        public override string ToString() {
            return IndexOperatorUtil.ToString(_property.CodeName, _operator, _value.ToString());
        }
        public object Evaluate(IVariables vars) => throw new NotImplementedException();
    }
    internal class OperatorExpressionNativeDateTimeProperty : IBooleanNativeExpression {
        DateTime _value;
        DateTimeProperty _property;
        IndexOperator _operator;
        public OperatorExpressionNativeDateTimeProperty(DateTimeProperty property, DateTime value, IndexOperator op) {
            _property = property;
            _operator = op;
            _value = value;
        }
        public IdSet Filter(IdSet set, QueryContext ctx) {
            if (_property.Indexed) {
                if (_property.Index == null) throw new NullReferenceException(nameof(_property.Index));
                return _property.Index.Filter(set, _operator, _value);
            } else {
                throw new NotImplementedException();
            }
        }
        public int MaxCount() {
            if (_property.Indexed && _property.Index != null) {
                return _property.Index.MaxCount(_operator, _value);
            } else {
                throw new NotImplementedException();
            }
        }
        public override string ToString() {
            return IndexOperatorUtil.ToString(_property.CodeName, _operator, _value.ToString());
        }
        public object Evaluate(IVariables vars) => throw new NotImplementedException();
    }
    internal class OperatorExpressionNativeDecimalProperty : IBooleanNativeExpression {
        decimal _value;
        DecimalProperty _property;
        IndexOperator _operator;
        public OperatorExpressionNativeDecimalProperty(DecimalProperty property, decimal value, IndexOperator op) {
            _property = property;
            _operator = op;
            _value = value;
        }
        public IdSet Filter(IdSet set, QueryContext ctx) {
            if (_property.Indexed) {
                if (_property.Index == null) throw new NullReferenceException(nameof(_property.Index));
                return _property.Index.Filter(set, _operator, _value);
            } else {
                throw new NotImplementedException();
            }
        }
        public int MaxCount() {
            if (_property.Indexed && _property.Index != null) {
                return _property.Index.MaxCount(_operator, _value);
            } else {
                throw new NotImplementedException();
            }
        }
        public override string ToString() {
            return IndexOperatorUtil.ToString(_property.CodeName, _operator, _value.ToString());
        }
        public object Evaluate(IVariables vars) => throw new NotImplementedException();
    }
    internal class OperatorExpressionNativeLongProperty : IBooleanNativeExpression {
        long _value;
        LongProperty _property;
        IndexOperator _operator;
        public OperatorExpressionNativeLongProperty(LongProperty property, long value, IndexOperator op) {
            _property = property;
            _operator = op;
            _value = value;
        }
        public IdSet Filter(IdSet set, QueryContext ctx) {
            if (_property.Indexed) {
                if (_property.Index == null) throw new NullReferenceException(nameof(_property.Index));
                return _property.Index.Filter(set, _operator, _value);
            } else {
                throw new NotImplementedException();
            }
        }
        public int MaxCount() {
            if (_property.Indexed && _property.Index != null) {
                return _property.Index.MaxCount(_operator, _value);
            } else {
                throw new NotImplementedException();
            }
        }
        public override string ToString() {
            return IndexOperatorUtil.ToString(_property.CodeName, _operator, _value.ToString());
        }
        public object Evaluate(IVariables vars) => throw new NotImplementedException();
    }
    internal class OperatorExpressionNativeStringProperty : IBooleanNativeExpression {
        readonly string _value;
        readonly StringProperty _property;
        readonly IndexOperator _operator;
        public OperatorExpressionNativeStringProperty(StringProperty property, string value, IndexOperator op) {
            _property = property;
            _operator = op;
            _value = value;
        }
        public IdSet Filter(IdSet set, QueryContext ctx) {
            if (_property.TryGetIndex(ctx, out var index)) {
                return index.Filter(set, _operator, _value);
            } else {
                throw new NotImplementedException("String property is not indexed by value.");
            }
        }
        public int MaxCount() {
            if (_property.Indexed) {
                if (_property.Index == null) throw new NullReferenceException(nameof(_property.Index));
                return _property.Index.MaxCount(_operator, _value);
            } else {
                throw new NotImplementedException("String property is not indexed by value.");
            }
        }
        public override string ToString() {
            return IndexOperatorUtil.ToString(_property.CodeName, _operator, _value.ToStringLiteral());
        }
        public object Evaluate(IVariables vars) => throw new NotImplementedException();
    }
    internal class MethodExpressionNativeSearchProperty : IBooleanNativeExpression {
        readonly string _value;
        readonly int _maxHits;
        readonly StringProperty _property;
        public MethodExpressionNativeSearchProperty(SetRegister sets, StringProperty property, string value, DataStoreLocal db) {
            _property = property;
            _value = value;
            var ps = _value.Split('|');
            if (ps.Length > 1) {
                int.TryParse(ps[1], out _maxHits);
                _value = ps[0];
            } else {
                _maxHits = int.MaxValue;
            }
        }
        public IdSet Filter(IdSet set, QueryContext ctx) {
            throw new NotImplementedException("Search method with max hits not implemented in native expression.");
            //var ids = _property.SearchForIdSet(_value, ratioSemantic, orSearch, _db);
            //return _sets.Intersection(set, ids);
        }
        public int MaxCount() {
            throw new NotImplementedException("Search method with max hits not implemented in native expression.");
            //if (_property.WordIndex == null) throw new NullReferenceException(nameof(_property.WordIndex));
            //return _property.SearchForIdSet(_value, ratioSemantic, false, _db).Count;
        }
        public override string ToString() {
            return _property.CodeName + ".Contains(" + _value.ToStringLiteral() + ")";
        }
        public object Evaluate(IVariables vars) => throw new NotImplementedException();
    }
    internal class MethodExpressionNativeRelation(SetRegister sets, bool[] directions, Relation[] relations, int to, RelQuestion method) : IBooleanNativeExpression {
        public IdSet Filter(IdSet set, QueryContext ctx) {
            return sets.WhereHasRelation(set, directions, relations, to, method);
        }
        public int MaxCount() => int.MaxValue;
        public object Evaluate(IVariables vars) => throw new NotImplementedException();
    }
    internal class MethodExpressionNativeRange(Property prop, string from, string to) : IBooleanNativeExpression {
        public IdSet Filter(IdSet set, QueryContext ctx) {
            var fromO = prop.ForceValueType(from, out _);
            var toO = prop.ForceValueType(to, out _);
            return prop.FilterRanges(set, fromO, toO, ctx);
        }
        public int MaxCount() => 1000;
        public object Evaluate(IVariables vars) => throw new NotImplementedException();
    }
    internal class OperatorExpressionNativeNotPrefix : IBooleanNativeExpression {
        IBooleanNativeExpression _expression;
        SetRegister _sets;
        public OperatorExpressionNativeNotPrefix(SetRegister sets, IBooleanNativeExpression expression) {
            _expression = expression;
            _sets = sets;
        }
        public IdSet Filter(IdSet set, QueryContext ctx) {
            return _sets.Difference(set, _expression.Filter(set, ctx));
        }
        public int MaxCount() {
            return int.MaxValue;
        }
        public override string ToString() {
            return "!" + _expression.ToString();
        }
        public object Evaluate(IVariables vars) => throw new NotImplementedException();
    }
}

