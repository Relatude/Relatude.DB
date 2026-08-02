using Relatude.DB.Datamodels;
using Relatude.DB.Query.Data;
using Relatude.DB.Query.Expressions;
namespace Relatude.DB.Query.Methods;
public class TraverseMethod : IExpression {
    readonly IExpression _input;
    readonly Guid _propertyGuid;
    readonly int _minLevel;
    readonly int _maxLevel;
    readonly GraphDirection _direction;
    readonly int? _maxVisited;
    public TraverseMethod(IExpression input, Datamodel dm, string propertyIdString, int minLevel, int maxLevel, int direction, int? maxVisited) {
        _input = input;
        _propertyGuid = dm.GetPropertyGuid(propertyIdString); // name, guid or id
        if (minLevel < 0) throw new Exception("Traverse minLevel cannot be negative. ");
        if (maxLevel < minLevel) throw new Exception("Traverse maxLevel cannot be less than minLevel. ");
        if (direction is < 0 or > 2) throw new Exception("Traverse direction must be 0 (Default), 1 (Reverse) or 2 (Both). ");
        if (maxVisited is < 1) throw new Exception("Traverse maxVisited must be positive. ");
        _minLevel = minLevel;
        _maxLevel = maxLevel;
        _direction = (GraphDirection)direction;
        _maxVisited = maxVisited;
    }
    public object Evaluate(IVariables vars) {
        var result = _input.Evaluate(vars);
        if (result is IGraphCollection nodesColl) {
            return nodesColl.Traverse(_propertyGuid, _minLevel, _maxLevel, _direction, _maxVisited);
        } else {
            throw new Exception("Unable to link Traverse to previous expression.");
        }
    }
    public override string ToString() => throw new NotImplementedException();
}
public class ShortestPathMethod : IExpression {
    readonly IExpression _input;
    readonly Guid _propertyGuid;
    readonly Guid _fromNodeGuid;
    readonly Guid _toNodeGuid;
    readonly int _maxLevel;
    readonly GraphDirection _direction;
    readonly int? _maxVisited;
    public ShortestPathMethod(IExpression input, Datamodel dm, string propertyIdString, string fromNodeIdParam, string toNodeIdParam, int maxLevel, int direction, int? maxVisited) {
        _input = input;
        _propertyGuid = dm.GetPropertyGuid(propertyIdString); // name, guid or id
        _fromNodeGuid = Guid.Parse(fromNodeIdParam);
        _toNodeGuid = Guid.Parse(toNodeIdParam);
        if (maxLevel < 0) throw new Exception("ShortestPath maxLevel cannot be negative. ");
        if (direction is < 0 or > 2) throw new Exception("ShortestPath direction must be 0 (Default), 1 (Reverse) or 2 (Both). ");
        if (maxVisited is < 1) throw new Exception("ShortestPath maxVisited must be positive. ");
        _maxLevel = maxLevel;
        _direction = (GraphDirection)direction;
        _maxVisited = maxVisited;
    }
    public object Evaluate(IVariables vars) {
        var result = _input.Evaluate(vars);
        if (result is IGraphCollection nodesColl) {
            return nodesColl.ShortestPath(_propertyGuid, _fromNodeGuid, _toNodeGuid, _maxLevel, _direction, _maxVisited);
        } else {
            throw new Exception("Unable to link ShortestPath to previous expression.");
        }
    }
    public override string ToString() => throw new NotImplementedException();
}
