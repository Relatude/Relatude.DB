using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Definitions.PropertyTypes;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.Query.Expressions;
namespace Relatude.DB.Query.Data;

internal partial class NodeCollectionData : IStoreNodeDataCollection, IFacetSource {
    public IStoreNodeDataCollection FilterAsMuchAsPossibleUsingIndexes(Variables vars, IExpression orgFilter, out IExpression? remainingFilter) {
        if (canBeNative(vars, orgFilter, _nodeType)) {
            remainingFilter = null;
            var nativeFilter = getIndexExpression(vars, orgFilter, _def, _db);
            if (nativeFilter is not IBooleanNativeExpression exp) throw new Exception("Filter clause does not evaluate to a bool expression. ");
            var filteredIds = exp.Filter(_ids, vars.Context);
            return new NodeCollectionData(_db, _ctx, _metrics, filteredIds, _nodeType, _includeBranches);
        } else {
            remainingFilter = orgFilter;
            // Console.WriteLine("Filter could not be converted to native expression: " + orgFilter.ToString());
            return this;
        }
    }
    static bool canBeNative(Variables vars, IExpression exp, NodeType nodeType) {
        if (exp is OperatorExpression opExp && opExp.IsBooleanExpression) {
            if (opExp.Operators.Count == 0) { // parenthesized single expression
                return opExp.Expressions.Count == 1 && canBeNative(vars, opExp.Expressions[0], nodeType);
            }
            var operand = opExp.Operators[0];
            if (operand == Operator.Or || operand == Operator.And) {
                foreach (var e in opExp.Expressions) {
                    if (!canBeNative(vars, e, nodeType))
                        return false;
                }
                return true;
            }
            // comparison: getIndexExpression only supports exactly one property compared to one constant,
            // anything else (property vs property, chained comparisons, etc.) must fall back to row evaluation
            if (opExp.Expressions.Count != 2) return false;
            var c1 = opExp.Expressions[0];
            var c2 = opExp.Expressions[1];
            var propSide = (c1 as PropertyReferenceExpression) ?? (c2 as PropertyReferenceExpression);
            if (propSide == null || c1 is PropertyReferenceExpression && c2 is PropertyReferenceExpression) return false;
            if (c1 is not ConstantExpression && c2 is not ConstantExpression) return false;
            return canBeNative(vars, propSide, nodeType);
        } else if (exp is ConstantExpression) {
            return true;
        } else if (exp is PropertyReferenceExpression propEx) { // simplification: other expression like freetext search could be supported....
            if (nodeType.AllPropertiesByName.TryGetValue(propEx.PropertyName, out var prop)) {
                if (prop.Indexed) {
                    // Console.WriteLine("Property " + propEx.PropertyName + "  IS indexed");
                    return true;
                } else {
                    // Console.WriteLine("Property " + propEx.PropertyName + " is NOT indexed");
                    return false;
                }
            }
            return true;
        } else if (exp is MatchesSearchExpression matchesEx) {
            // a search has no row evaluation, so only claim it when an index can actually answer it.
            // note the word and semantic indexes are independent of the value index, hence no Indexed check
            if (nodeType.AllPropertiesByName.TryGetValue(matchesEx.PropertyName, out var prop)) {
                if (prop is StringProperty sp) return sp.IndexedByWords || sp.IndexedBySemantic;
            }
            return false;
        } else if (exp is RelationExpression) {
            return true;
        } else if (exp is RangeExpression rangeEx) {
            if (nodeType.AllPropertiesByName.TryGetValue(rangeEx.PropertyName, out var prop)) {
                if (prop is DateTimeProperty or DateTimeOffsetProperty) return prop.Indexed;
            }
            return false;
        } else if (exp is GeoWithinExpression geoEx) {
            if (nodeType.AllPropertiesByName.TryGetValue(geoEx.PropertyName, out var prop)) {
                if (prop is GeoCoordinateProperty) return prop.Indexed;
            }
            return false;
        } else if (exp is ContainsExpression containsEx) {
            // arrays need a per element index, strings need the value index to scan unique values;
            // everything else (non indexed properties, float and byte arrays) falls back to row evaluation
            if (nodeType.AllPropertiesByName.TryGetValue(containsEx.PropertyName, out var prop)) {
                if (prop is IArrayProperty or StringProperty) return prop.Indexed;
            }
            return false;
        } else if (exp is StartsWithExpression startsWithEx) {
            if (nodeType.AllPropertiesByName.TryGetValue(startsWithEx.PropertyName, out var prop)) {
                if (prop is StringProperty) return prop.Indexed;
            }
            return false;
        } else if (exp is NotPrefixExpression notPrefix) {
            return canBeNative(vars, notPrefix.Subject, nodeType);
        } else {
            return false;
        }
    }
    // the property a "x.Name.Method(..)" expression is called on, resolved against the collection
    // the lambda parameter is bound to (normally this collection... but could be other)
    static Property propertyOf(Variables vars, VariableReferenceExpression source, string propertyName) {
        if (source.Evaluate(vars) is not NodeCollectionData nc) throw new NotSupportedException();
        if (!nc._nodeType.AllPropertiesByName.TryGetValue(propertyName, out var prop))
            throw new NotSupportedException(propertyName + " is not a property of " + nc._nodeType.ToString());
        return prop;
    }
    static IExpression getIndexExpression(Variables vars, IExpression orgFilter, Definition def, DataStores.DataStoreLocal db) {
        if (orgFilter is OperatorExpression opExp && opExp.IsBooleanExpression) {
            if (opExp.Operators.Count == 0 && opExp.Expressions.Count == 1) { // parenthesized single expression
                return getIndexExpression(vars, opExp.Expressions[0], def, db);
            }
            var operand = opExp.Operators[0];
            IAndOrNativeExpression e;
            if (operand == Operator.Or || operand == Operator.And) {
                e = operand == Operator.Or ? new OrNativeExpression(def.Sets) : new AndNativeExpression();
                foreach (var exp in opExp.Expressions) {
                    e.Expressions.Add((IBooleanNativeExpression)getIndexExpression(vars, exp, def, db));
                }
                return e;
            } else {
                var op = operand switch {
                    Operator.Equal => IndexOperator.Equal,
                    Operator.NotEqual => IndexOperator.NotEqual,
                    Operator.Greater => IndexOperator.Greater,
                    Operator.Smaller => IndexOperator.Smaller,
                    Operator.SmallerOrEqual => IndexOperator.SmallerOrEqual,
                    Operator.GreaterOrEqual => IndexOperator.GreaterOrEqual,
                    _ => throw new NotSupportedException(),
                };
                var e1 = opExp.Expressions[0];
                var e2 = opExp.Expressions[1];
                PropertyReferenceExpression propEx;
                ConstantExpression constEx;
                if (e1 is PropertyReferenceExpression p1 && e2 is ConstantExpression c2) {
                    propEx = p1;
                    constEx = c2;
                } else if (e2 is PropertyReferenceExpression p2 && e1 is ConstantExpression c1) {
                    propEx = p2;
                    constEx = c1;
                    op = op switch { // constant was on the left, so flip the comparison to read property-vs-constant
                        IndexOperator.Greater => IndexOperator.Smaller,
                        IndexOperator.Smaller => IndexOperator.Greater,
                        IndexOperator.GreaterOrEqual => IndexOperator.SmallerOrEqual,
                        IndexOperator.SmallerOrEqual => IndexOperator.GreaterOrEqual,
                        _ => op,
                    };
                } else {
                    return orgFilter;
                }
                var collection = propEx.Evaluate(vars); // normally this collection... but could be other
                if (collection is not NodeCollectionData nc) throw new NotSupportedException();

                if (!nc._nodeType.AllPropertiesByName.TryGetValue(propEx.PropertyName, out var prop)) {
                    if (db.Datamodel.NodeTypes.TryGetValue(nc._nodeType.Id, out var nodeType)) {
                        if (nodeType.NameOfPublicIdProperty == propEx.PropertyName) {
                            if (!Guid.TryParse(constEx.Value?.ToString(), out var id)) throw new NotSupportedException("Id property can only be used with Guid constant");
                            int uid;
                            if (!db._guids.TryGetId(id, out uid)) uid = 0; // unknown id, so continue with 0, should result in no match
                            return new OperatorExpressionNativeIdProperty(uid, op, def.Sets);
                        }
                        if (nodeType.NameOfInternalIdProperty == propEx.PropertyName) {
                            if (!int.TryParse(constEx.Value?.ToString(), out var uid)) throw new Exception("InternalId property can only be used with int constant");
                            return new OperatorExpressionNativeIdProperty(uid, op, def.Sets);
                        }
                    }
                    throw new NotSupportedException(propEx.PropertyName + " is not a property of " + nc._nodeType.ToString());
                }
                switch (prop.PropertyType) {
                    case PropertyType.Boolean:
                        return new OperatorExpressionNativeBooleanProperty((BooleanProperty)prop, (bool)constEx.Value!, op);
                    case PropertyType.Integer:
                        var integerValue = IntegerPropertyModel.ForceValueType(constEx.Value!, out _);
                        return new OperatorExpressionNativeIntegerProperty((IntegerProperty)prop, integerValue, op);
                    case PropertyType.Float:
                        var floatValue = FloatPropertyModel.ForceValueType(constEx.Value!, out _);
                        return new OperatorExpressionNativeFloatProperty((FloatProperty)prop, floatValue, op);
                    case PropertyType.Double:
                        var doubleValue = DoublePropertyModel.ForceValueType(constEx.Value!, out _);
                        return new OperatorExpressionNativeDoubleProperty((DoubleProperty)prop, doubleValue, op);
                    case PropertyType.String:
                        return new OperatorExpressionNativeStringProperty((StringProperty)prop, (string)constEx.Value!, op);
                    case PropertyType.DateTime:
                        var dateTimeValue = DateTimePropertyModel.ForceValueType(constEx.Value!, out _);
                        return new OperatorExpressionNativeDateTimeProperty((DateTimeProperty)prop, dateTimeValue, op);
                    case PropertyType.TimeSpan:
                        var timeSpanValue = TimeSpanPropertyModel.ForceValueType(constEx.Value!, out _);
                        return new OperatorExpressionNativeTimeSpanProperty((TimeSpanProperty)prop, timeSpanValue, op);
                    case PropertyType.DateTimeOffset:
                        var dateTimeOffsetValue = DateTimeOffsetPropertyModel.ForceValueType(constEx.Value!, out _);
                        return new OperatorExpressionNativeDateTimeOffsetProperty((DateTimeOffsetProperty)prop, dateTimeOffsetValue, op);
                    case PropertyType.Long:
                        var longValue = LongPropertyModel.ForceValueType(constEx.Value!, out _);
                        return new OperatorExpressionNativeLongProperty((LongProperty)prop, longValue, op);
                    case PropertyType.Decimal:
                        var decimalValue = DecimalPropertyModel.ForceValueType(constEx.Value!, out _);
                        return new OperatorExpressionNativeDecimalProperty((DecimalProperty)prop, decimalValue, op);
                    case PropertyType.Guid:
                        var guidValue = GuidPropertyModel.ForceValueType(constEx.Value!, out _);
                        return new OperatorExpressionNativeGuidProperty((GuidProperty)prop, guidValue, op);
                    case PropertyType.GeoCoordinate:
                        var geoValue = GeoCoordinatePropertyModel.ForceValueType(constEx.Value!, out _);
                        return new OperatorExpressionNativeGeoCoordinateProperty((GeoCoordinateProperty)prop, def.Sets, geoValue, op);
                    case PropertyType.Any:
                    case PropertyType.Relation:
                    default: throw new NotSupportedException();
                }
            }
        } else if (orgFilter is ConstantExpression consExp) {
            if (consExp.Value is bool boolValue) return new ConstantBooleanNativeExpression(boolValue); // filter fully folded to a constant, eg. Where(c => 2 + 2 == 4)
            return consExp;
        } else if (orgFilter is VariableReferenceExpression varExp) {
            return varExp;
        } else if (orgFilter is RelationExpression relExp) {
            var collection = relExp.SourceObject.Evaluate(vars); // normally this collection... but could be other
            if (collection is not NodeCollectionData nc) throw new NotSupportedException();
            var (directions, relations) = relExp.GetRelationInfo(nc._nodeType.Id, db.Datamodel);
            if (db._guids.TryGetId(relExp.GetTo(db), out var id)) {
                var rel = new Relation[relations.Length];
                for (var i = 0; i < relations.Length; i++) rel[i] = db._definition.Relations[relations[i]];
                return new MethodExpressionNativeRelation(def.Sets, directions, rel, id, relExp.Method);
            } else { // unknown id
                return relExp.Method switch {
                    RelQuestion.Relates => new ConstantBooleanNativeExpression(false),
                    _ => throw new NotSupportedException(),
                };
            }
        } else if (orgFilter is MatchesSearchExpression matchesEx) {
            var prop = propertyOf(vars, matchesEx.SourceObject, matchesEx.PropertyName);
            if (prop is not StringProperty strProp)
                throw new NotSupportedException(matchesEx.PropertyName + " is not a string property, so MatchesSearch is not supported");
            return new MethodExpressionNativeMatchesSearch(strProp, def.Sets, db, matchesEx);
        } else if (orgFilter is NotPrefixExpression notPrefix) {
            var exp = (IBooleanNativeExpression)getIndexExpression(vars, notPrefix.Subject, def, db);
            return new OperatorExpressionNativeNotPrefix(def.Sets, exp);
        } else if (orgFilter is PropertyReferenceExpression propEx) {
            var collection = propEx.Evaluate(vars); // normally this collection... but could be other
            if (collection is not NodeCollectionData nc) throw new NotSupportedException();
            if (!nc._nodeType.AllPropertiesByName.TryGetValue(propEx.PropertyName, out var prop)) {
                throw new NotSupportedException(propEx.PropertyName + " is not a property of " + nc._nodeType.ToString());
            }
            if (prop is not BooleanProperty boolProp) {
                throw new NotSupportedException(propEx.PropertyName + " is not a boolean value type");
            }
            return new OperatorExpressionNativeBooleanProperty(boolProp, true, IndexOperator.Equal);
        } else if (orgFilter is RangeExpression rangeEx) {
            var collection = rangeEx.SourceObject.Evaluate(vars); // normally this collection... but could be other
            if (collection is not NodeCollectionData nc) throw new NotSupportedException();
            if (!nc._nodeType.AllPropertiesByName.TryGetValue(rangeEx.PropertyName, out var prop)) {
                throw new NotSupportedException(rangeEx.PropertyName + " is not a property of " + nc._nodeType.ToString());
            }
            if (prop is not IValueProperty vProp)
                throw new NotSupportedException(rangeEx.PropertyName + " does not support range queries");
            return new MethodExpressionNativeRange(vProp, rangeEx.From, rangeEx.To);
        } else if (orgFilter is GeoWithinExpression geoEx) {
            var collection = geoEx.SourceObject.Evaluate(vars); // normally this collection... but could be other
            if (collection is not NodeCollectionData nc) throw new NotSupportedException();
            if (!nc._nodeType.AllPropertiesByName.TryGetValue(geoEx.PropertyName, out var prop)) {
                throw new NotSupportedException(geoEx.PropertyName + " is not a property of " + nc._nodeType.ToString());
            }
            if (prop is not GeoCoordinateProperty geoProp)
                throw new NotSupportedException(geoEx.PropertyName + " is not a GeoCoordinate property");
            return new MethodExpressionNativeGeoWithin(geoProp, def.Sets, geoEx.Center, geoEx.Meters);
        } else if (orgFilter is ContainsExpression containsEx) {
            var prop = propertyOf(vars, containsEx.SourceObject, containsEx.PropertyName);
            if (prop is StringProperty strProp) // substring, as in C#
                return new MethodExpressionNativeStringContains(strProp, def.Sets, containsEx.SubstringValue);
            if (prop is not IArrayProperty arrayProp)
                throw new NotSupportedException(containsEx.PropertyName + " is neither a string nor an array property with a per element index, so Contains cannot be answered from the index");
            return new MethodExpressionNativeContains(arrayProp, prop.CodeName, containsEx.Value);
        } else if (orgFilter is StartsWithExpression startsWithEx) {
            var prop = propertyOf(vars, startsWithEx.SourceObject, startsWithEx.PropertyName);
            if (prop is not StringProperty strProp)
                throw new NotSupportedException(startsWithEx.PropertyName + " is not a string property, so StartsWith is not supported");
            return new MethodExpressionNativeStringStartsWith(strProp, def.Sets, startsWithEx.Prefix);
        } else {
            throw new NotImplementedException();
        }
    }
    public IStoreNodeDataCollection FilterByTypes(Guid[] types, bool includeDescendants) {
        var newIds = _def.Sets.WhereTypes(_ids, types.Select(t => _def.GetAllIdsForTypeNoAccessControl(t, includeDescendants)).ToArray());
        // access control not needed, as we are filtering down ( _ids is already filtered by access control )
        return new NodeCollectionData(_db, _ctx, _metrics, newIds, _nodeType, _includeBranches);
    }
}