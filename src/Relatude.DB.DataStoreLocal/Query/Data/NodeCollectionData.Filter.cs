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
            var filteredIds = exp.Filter(_ids);
            return new NodeCollectionData(_db, _metrics, filteredIds, _nodeType, _includeBranches);
        } else {
            remainingFilter = orgFilter;
            // Console.WriteLine("Filter could not be converted to native expression: " + orgFilter.ToString());
            return this;
        }
    }
    static bool canBeNative(Variables vars, IExpression exp, NodeType nodeType) {
        if (exp is OperatorExpression opExp && opExp.IsBooleanExpression) {
            foreach (var e in opExp.Expressions) {
                if (!canBeNative(vars, e, nodeType))
                    return false;
            }
            return true;
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
        } else if (exp is SearchPropertyExpression) {
            return true;
        } else if (exp is RelationExpression) {
            return true;
        } else if (exp is RangeExpression rangeEx) {
            if (nodeType.AllPropertiesByName.TryGetValue(rangeEx.PropertyName, out var prop)) {
                if (prop is DateTimeProperty dt) return dt.Indexed;
            }
            return false;
        } else if (exp is NotPrefixExpression notPrefix) {
            return canBeNative(vars, notPrefix.Subject, nodeType);
        } else {
            return false;
        }
    }
    static IExpression getIndexExpression(Variables vars, IExpression orgFilter, Definition def, DataStores.DataStoreLocal db) {
        if (orgFilter is OperatorExpression opExp && opExp.IsBooleanExpression) {
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
                    case PropertyType.String:
                        return new OperatorExpressionNativeStringProperty((StringProperty)prop, (string)constEx.Value!, op);
                    case PropertyType.DateTime:
                        var dateTimeValue = DateTimePropertyModel.ForceValueType(constEx.Value!, out _);
                        return new OperatorExpressionNativeDateTimeProperty((DateTimeProperty)prop, dateTimeValue, op);
                    case PropertyType.Long:
                        var longValue = LongPropertyModel.ForceValueType(constEx.Value!, out _);
                        return new OperatorExpressionNativeLongProperty((LongProperty)prop, longValue, op);
                    case PropertyType.Decimal:
                        var decimalValue = DecimalPropertyModel.ForceValueType(constEx.Value!, out _);
                        return new OperatorExpressionNativeDecimalProperty((DecimalProperty)prop, decimalValue, op);

                    case PropertyType.Any:
                    case PropertyType.Relation:
                    //case PropertyType.Collection:
                    //case PropertyType.DataObject:
                    default: throw new NotSupportedException();
                }
            }
        } else if (orgFilter is ConstantExpression consExp) {
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
        } else if (orgFilter is SearchPropertyExpression searchExp) {
            var collection = searchExp.PropertyReference.Evaluate(vars); // normally this collection... but could be other
            if (collection is not NodeCollectionData nc) throw new NotSupportedException();
            var propName = searchExp.PropertyReference.PropertyName;
            var prop = nc._nodeType.AllPropertiesByName[propName];
            return new MethodExpressionNativeSearchProperty(def.Sets, (StringProperty)prop, searchExp.SearchText, db);
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
            if (prop is not DateTimeProperty dtProp)
                throw new NotSupportedException(rangeEx.PropertyName + " is not a DateTime value type");
            return new MethodExpressionNativeRange(dtProp, rangeEx.From, rangeEx.To);
        } else {
            throw new NotImplementedException();
        }
    }
    public IStoreNodeDataCollection FilterByTypes(Guid[] types) {
        var newIds = _def.Sets.WhereTypes(_ids, types.Select(t => _def.GetAllIdsForType(t)).ToArray());
        return new NodeCollectionData(_db, _metrics, newIds, _nodeType, _includeBranches);
        //foreach(var id in _ids.Enumerate()) {
        //    var typeId = _def.GetTypeOfNode(id);
        //    _def.GetAllIdsForType(typeId).Has(id);
        //}



        //// should be optimized:
        //var lookUp = new HashSet<Guid>(types);
        //foreach (var type in types) {
        //    if (_db.Datamodel.NodeTypes.TryGetValue(type, out var nodeType)) {
        //        foreach (var subType in nodeType.ThisAndDescendingTypes) {
        //            lookUp.Add(subType.Key);
        //        }
        //    }
        //}
        //List<int> ids = new();
        //foreach (var id in _ids.Enumerate()) {
        //    var typeId = _def.GetTypeOfNode(id);
        //    if (lookUp.Contains(typeId)) ids.Add(id);
        //}
        //var newIdSet = new IdSet(ids);
        //return new NodeCollectionData(_db, newIdSet, _nodeType, _includeBranches);
    }
}