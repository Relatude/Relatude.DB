using Relatude.DB.Datamodels;
using Relatude.DB.Query.Expressions;
using Relatude.DB.Query.Methods;
using Relatude.DB.Query.Parsing.Tokens;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Relatude.DB.Query.Parsing.Expressions;

// ── Parameter abstraction ────────────────────────────────────────────────────

enum MethodParamKind { Constant, Lambda, Any }

class MethodParamDef(MethodParamKind kind, bool isOptional = false, bool allowNull = false) {
    public MethodParamKind Kind { get; } = kind;
    public bool IsOptional { get; } = isOptional;
    public bool AllowNull { get; } = allowNull;

    public static MethodParamDef Required(MethodParamKind kind) => new(kind);
    public static MethodParamDef Optional(MethodParamKind kind) => new(kind, isOptional: true);
    public static MethodParamDef Nullable(MethodParamKind kind) => new(kind, allowNull: true);

    public void Validate(TokenBase token, int index, string methodName) {
        if (!AllowNull && token == null)
            throw new ArgumentNullException($"Parameter {index} of '{methodName}' must not be null.");
        if (token == null) return;
        if (Kind == MethodParamKind.Constant && token is not ValueConstantToken)
            throw new Exception($"Parameter {index} of '{methodName}' must be a constant value.");
        if (Kind == MethodParamKind.Lambda && token is not LambdaToken)
            throw new Exception($"Parameter {index} of '{methodName}' must be a lambda expression.");
    }
}

// ── Method abstraction ───────────────────────────────────────────────────────

abstract class MethodDef {
    public abstract string[] Names { get; }
    /// <summary>Minimum number of arguments required.</summary>
    public abstract int MinArgs { get; }
    /// <summary>Maximum number of arguments allowed (-1 = unlimited).</summary>
    public virtual int MaxArgs => MinArgs;
    /// <summary>Parameter definitions (by position). May be shorter than MaxArgs for variadic methods.</summary>
    public virtual MethodParamDef[] Params => [];

    protected void ValidateArgs(MethodCallToken e) {
        if (e.Arguments.Count < MinArgs)
            throw new Exception($"'{Names[0]}' requires at least {MinArgs} argument(s).");
        if (MaxArgs >= 0 && e.Arguments.Count > MaxArgs)
            throw new Exception($"'{Names[0]}' accepts at most {MaxArgs} argument(s).");
        for (int i = 0; i < Math.Min(Params.Length, e.Arguments.Count); i++)
            if (!Params[i].IsOptional || i < e.Arguments.Count)
                Params[i].Validate(e.Arguments[i], i, Names[0]);
    }

    protected static IExpression BuildSource(MethodCallToken e, Datamodel dm) {
        if (e.Subject == null) throw new NullReferenceException($"Subject of '{e.Name}' must not be null.");
        return ExpressionTreeBuilder.Build(e.Subject, dm);
    }

    protected static LambdaExpression BuildLambda(LambdaToken lambda, Datamodel dm, string methodName) {
        if (lambda.Paramaters == null || lambda.Paramaters.Count != 1)
            throw new Exception($"'{methodName}' only accepts a lambda with exactly one parameter.");
        return ExpressionTreeBuilder.BuildLambda(lambda, dm);
    }

    public IExpression Build(MethodCallToken e, Datamodel dm) {
        ValidateArgs(e);
        return Create(e, dm);
    }

    protected abstract IExpression Create(MethodCallToken e, Datamodel dm);
}

// ── Concrete method definitions ──────────────────────────────────────────────

sealed class SelectMethodDef : MethodDef {
    public override string[] Names => ["select"];
    public override int MinArgs => 1;
    public override MethodParamDef[] Params => [MethodParamDef.Required(MethodParamKind.Lambda)];
    protected override IExpression Create(MethodCallToken e, Datamodel dm)
        => new SelectMethod(BuildSource(e, dm), BuildLambda((LambdaToken)e.Arguments[0], dm, Names[0]));
}

sealed class SelectIdMethodDef : MethodDef {
    public override string[] Names => ["selectid"];
    public override int MinArgs => 0;
    protected override IExpression Create(MethodCallToken e, Datamodel dm) => new SelectIdMethod(BuildSource(e, dm));
}

sealed class WhereMethodDef : MethodDef {
    public override string[] Names => ["where"];
    public override int MinArgs => 1;
    public override int MaxArgs => 1;
    public override MethodParamDef[] Params => [MethodParamDef.Required(MethodParamKind.Lambda)];
    protected override IExpression Create(MethodCallToken e, Datamodel dm) {
        if (e.Arguments[0] is not LambdaToken lambda) throw new Exception("Where statement only accepts lambda expressions as argument.");
        return new WhereMethod(BuildSource(e, dm), BuildLambda(lambda, dm, Names[0]));
    }
}

sealed class WhereTypesMethodDef : MethodDef {
    public override string[] Names => ["wheretypes"];
    public override int MinArgs => 1;
    public override int MaxArgs => 2;
    public override MethodParamDef[] Params => [MethodParamDef.Required(MethodParamKind.Constant), MethodParamDef.Optional(MethodParamKind.Constant)];
    protected override IExpression Create(MethodCallToken e, Datamodel dm) {
        if (e.Arguments[0] is not ValueConstantToken arg1) throw new Exception("WhereTypes first argument must be a constant.");
        bool includeDescendants = e.Arguments.Count > 1 && e.Arguments[1] is ValueConstantToken arg2 ? arg2.GetBoolValue() : true;
        return new WhereTypesMethod(BuildSource(e, dm), arg1.GetNodeTypeGuids(dm), includeDescendants);
    }
}

sealed class OrderByMethodDef : MethodDef {
    public override string[] Names => ["orderby"];
    public override int MinArgs => 1;
    public override int MaxArgs => 2;
    public override MethodParamDef[] Params => [MethodParamDef.Required(MethodParamKind.Lambda), MethodParamDef.Optional(MethodParamKind.Any)];
    protected override IExpression Create(MethodCallToken e, Datamodel dm) {
        if (e.Arguments[0] is not LambdaToken lambda) throw new Exception("OrderBy statement only accepts lambda expressions as argument.");
        var descending = e.Arguments.Count > 1 && (e.Arguments[1] + "").ToLower() == "true";
        return new OrderByMethod(BuildSource(e, dm), BuildLambda(lambda, dm, Names[0]), descending);
    }
}

sealed class FacetsMethodDef : MethodDef {
    public override string[] Names => ["facets"];
    public override int MinArgs => 0;
    public override int MaxArgs => -1;
    protected override IExpression Create(MethodCallToken e, Datamodel dm) {
        foreach (var arg in e.Arguments) if (arg is not ValueConstantToken) throw new Exception("Only string arguments allowed in facets expression.");
        return new FacetMethod(BuildSource(e, dm), e.Arguments.Cast<ValueConstantToken>().Select(a => a.GetStringValue()), dm);
    }
}

sealed class AddFacetMethodDef : MethodDef {
    public override string[] Names => ["addfacet"];
    public override int MinArgs => 1;
    public override MethodParamDef[] Params => [MethodParamDef.Required(MethodParamKind.Constant)];
    protected override IExpression Create(MethodCallToken e, Datamodel dm) {
        if (BuildSource(e, dm) is not FacetMethod fc) throw new Exception("Expected facet expression.");
        if (e.Arguments[0] is not ValueConstantToken arg) throw new Exception("Only string arguments allowed in facet expression.");
        fc.AddFacet(arg.GetStringValue());
        return fc;
    }
}

sealed class AddValueFacetMethodDef : MethodDef {
    public override string[] Names => ["addvaluefacet"];
    public override int MinArgs => 1;
    public override int MaxArgs => 2;
    public override MethodParamDef[] Params => [MethodParamDef.Required(MethodParamKind.Constant), MethodParamDef.Optional(MethodParamKind.Constant)];
    protected override IExpression Create(MethodCallToken e, Datamodel dm) {
        if (BuildSource(e, dm) is not FacetMethod fc) throw new Exception("Expected facet expression.");
        if (e.Arguments[0] is not ValueConstantToken arg) throw new Exception("Only string arguments allowed in facet expression.");
        if (e.Arguments.Count == 1) { fc.AddValueFacet(arg.GetStringValue()); return fc; }
        if (e.Arguments[1] is not ValueConstantToken arg2) throw new Exception("Only string arguments allowed in facet expression.");
        fc.AddValueFacet(arg.GetStringValue(), arg2.GetStringValue());
        return fc;
    }
}

sealed class AddRangeFacetMethodDef : MethodDef {
    public override string[] Names => ["addrangefacet"];
    public override int MinArgs => 1;
    public override int MaxArgs => 3;
    public override MethodParamDef[] Params => [MethodParamDef.Required(MethodParamKind.Constant), MethodParamDef.Optional(MethodParamKind.Constant), MethodParamDef.Optional(MethodParamKind.Constant)];
    protected override IExpression Create(MethodCallToken e, Datamodel dm) {
        if (BuildSource(e, dm) is not FacetMethod fc) throw new Exception("Expected facet expression.");
        if (e.Arguments[0] is not ValueConstantToken arg) throw new Exception("Only string arguments allowed in facet expression.");
        if (e.Arguments.Count == 1) { fc.AddRangeFacet(arg.GetStringValue()); return fc; }
        if (e.Arguments[1] is not ValueConstantToken arg2) throw new Exception("Only string arguments allowed in facet expression.");
        if (e.Arguments[2] is not ValueConstantToken arg3) throw new Exception("Only string arguments allowed in facet expression.");
        fc.AddRangeFacet(arg.GetStringValue(), arg2.GetStringValue(), arg3.GetStringValue());
        return fc;
    }
}

sealed class SetFacetValueMethodDef : MethodDef {
    public override string[] Names => ["setfacetvalue"];
    public override int MinArgs => 2;
    public override MethodParamDef[] Params => [MethodParamDef.Required(MethodParamKind.Constant), MethodParamDef.Required(MethodParamKind.Constant)];
    protected override IExpression Create(MethodCallToken e, Datamodel dm) {
        if (BuildSource(e, dm) is not FacetMethod fc) throw new Exception("Expected facet expression.");
        fc.SetFacetValue(((ValueConstantToken)e.Arguments[0]).GetStringValue(), ((ValueConstantToken)e.Arguments[1]).GetStringValue());
        return fc;
    }
}

sealed class SetFacetRangeValueMethodDef : MethodDef {
    public override string[] Names => ["setfacetrangevalue"];
    public override int MinArgs => 3;
    public override MethodParamDef[] Params => [MethodParamDef.Required(MethodParamKind.Constant), MethodParamDef.Required(MethodParamKind.Constant), MethodParamDef.Required(MethodParamKind.Constant)];
    protected override IExpression Create(MethodCallToken e, Datamodel dm) {
        if (BuildSource(e, dm) is not FacetMethod fc) throw new Exception("Expected facet expression.");
        fc.SetFacetRangeValue(((ValueConstantToken)e.Arguments[0]).GetStringValue(), ((ValueConstantToken)e.Arguments[1]).GetStringValue(), ((ValueConstantToken)e.Arguments[2]).GetStringValue());
        return fc;
    }
}

sealed class SearchMethodDef : MethodDef {
    public override string[] Names => ["search", "wheresearch"];
    public override int MinArgs => 1;
    public override int MaxArgs => 6;
    public override MethodParamDef[] Params => [
        MethodParamDef.Required(MethodParamKind.Constant),
        MethodParamDef.Optional(MethodParamKind.Constant),
        MethodParamDef.Optional(MethodParamKind.Constant),
        MethodParamDef.Optional(MethodParamKind.Constant),
        MethodParamDef.Optional(MethodParamKind.Constant),
        MethodParamDef.Optional(MethodParamKind.Constant),
    ];
    protected override IExpression Create(MethodCallToken e, Datamodel dm) {
        foreach (var arg in e.Arguments) if (arg is not ValueConstantToken) throw new Exception("Only constant arguments allowed in search expression.");
        var source = BuildSource(e, dm);
        var args = e.Arguments.Cast<ValueConstantToken>().ToArray();
        var searchText = args[0].GetStringValue();
        double? semanticRatio = args.Length > 1 ? args[1].GetDoubleOrNullValue() : null;
        float? minVectorSimilarity = args.Length > 2 ? args[2].GetFloatOrNullValue() : null;
        bool? orSearch = args.Length > 3 ? args[3].GetBoolOrNullValue() : null;
        int? maxWordsEvaluated = args.Length > 4 ? args[4].GetIntOrNullValue() : null;
        int? maxHitsEvaluated = args.Length > 5 ? args[5].GetIntOrNullValue() : null;
        return e.Name.ToLower() == "wheresearch"
            ? new WhereSearchMethod(source, searchText, semanticRatio, minVectorSimilarity, orSearch, maxWordsEvaluated)
            : new SearchMethod(source, searchText, semanticRatio, minVectorSimilarity, orSearch, maxWordsEvaluated, maxHitsEvaluated);
    }
}

sealed class PageMethodDef : MethodDef {
    public override string[] Names => ["page"];
    public override int MinArgs => 2;
    public override MethodParamDef[] Params => [MethodParamDef.Required(MethodParamKind.Constant), MethodParamDef.Required(MethodParamKind.Constant)];
    protected override IExpression Create(MethodCallToken e, Datamodel dm)
        => new PageMethod(BuildSource(e, dm), ((ValueConstantToken)e.Arguments[0]).GetIntValue(), ((ValueConstantToken)e.Arguments[1]).GetIntValue());
}

sealed class TakeMethodDef : MethodDef {
    public override string[] Names => ["take"];
    public override int MinArgs => 1;
    public override MethodParamDef[] Params => [MethodParamDef.Required(MethodParamKind.Constant)];
    protected override IExpression Create(MethodCallToken e, Datamodel dm)
        => new TakeMethod(BuildSource(e, dm), ((ValueConstantToken)e.Arguments[0]).GetIntValue());
}

sealed class SkipMethodDef : MethodDef {
    public override string[] Names => ["skip"];
    public override int MinArgs => 1;
    public override MethodParamDef[] Params => [MethodParamDef.Required(MethodParamKind.Constant)];
    protected override IExpression Create(MethodCallToken e, Datamodel dm)
        => new SkipMethod(BuildSource(e, dm), ((ValueConstantToken)e.Arguments[0]).GetIntValue());
}

sealed class CountMethodDef : MethodDef {
    public override string[] Names => ["count"];
    public override int MinArgs => 0;
    protected override IExpression Create(MethodCallToken e, Datamodel dm) => new CountMethod(BuildSource(e, dm));
}

sealed class SumMethodDef : MethodDef {
    public override string[] Names => ["sum"];
    public override int MinArgs => 0;
    public override int MaxArgs => 1;
    public override MethodParamDef[] Params => [MethodParamDef.Optional(MethodParamKind.Lambda)];
    protected override IExpression Create(MethodCallToken e, Datamodel dm) {
        var source = BuildSource(e, dm);
        LambdaExpression? lambdaEx = null;
        if (e.Arguments.Count > 0) {
            if (e.Arguments[0] is not LambdaToken lambda) throw new Exception("Sum statement only accepts a lambda expression as argument.");
            lambdaEx = BuildLambda(lambda, dm, Names[0]);
        }
        return new SumMethod(source, lambdaEx);
    }
}

sealed class RelationMethodDef : MethodDef {
    public override string[] Names => ["any", "is", "has"];
    public override int MinArgs => 1;
    public override MethodParamDef[] Params => [MethodParamDef.Required(MethodParamKind.Any)];
    protected override IExpression Create(MethodCallToken e, Datamodel dm) {
        if (e.Subject == null) throw new NullReferenceException();
        var path = (e.Subject as VariableReferenceToken)?.Name ?? throw new NullReferenceException();
        return new RelationExpression(path, e.Arguments[0].ToString(), e.Name.ToLower());
    }
}

sealed class WhereInMethodDef : MethodDef {
    public override string[] Names => ["wherein"];
    public override int MinArgs => 2;
    public override MethodParamDef[] Params => [MethodParamDef.Required(MethodParamKind.Constant), MethodParamDef.Required(MethodParamKind.Constant)];
    protected override IExpression Create(MethodCallToken e, Datamodel dm) {
        if (e.Arguments[0] is not ValueConstantToken prop) throw new Exception("Only string arguments allowed as property in WhereIn expression.");
        if (e.Arguments[1] is not ValueConstantToken id) throw new Exception("Only string arguments allowed as id in WhereIn expression.");
        var propId = prop.GetPropertyId(dm);
        return new WhereInMethod(BuildSource(e, dm), propId, id.GetPropertyValues(dm, propId));
    }
}

sealed class WhereInIdsMethodDef : MethodDef {
    public override string[] Names => ["whereinids"];
    public override int MinArgs => 1;
    public override MethodParamDef[] Params => [MethodParamDef.Required(MethodParamKind.Constant)];
    protected override IExpression Create(MethodCallToken e, Datamodel dm) {
        if (e.Arguments[0] is not ValueConstantToken id) throw new Exception("WhereInIds requires a constant argument.");
        return new WhereInIdsMethod(BuildSource(e, dm), id.GetGuids());
    }
}

sealed class RelatesAnyMethodDef : MethodDef {
    public override string[] Names => ["relatesany"];
    public override int MinArgs => 2;
    public override MethodParamDef[] Params => [MethodParamDef.Required(MethodParamKind.Constant), MethodParamDef.Required(MethodParamKind.Constant)];
    protected override IExpression Create(MethodCallToken e, Datamodel dm) {
        if (e.Arguments[0] is not ValueConstantToken prop) throw new Exception("Only string arguments allowed as property in RelatesAny expression.");
        if (e.Arguments[1] is not ValueConstantToken id) throw new Exception("Only string arguments allowed as id in RelatesAny expression.");
        return new RelatesAnyMethod(BuildSource(e, dm), dm, prop.GetStringValue(), id.GetStringValue());
    }
}

sealed class RelatesMethodDef : MethodDef {
    public override string[] Names => ["relates"];
    public override int MinArgs => 2;
    public override MethodParamDef[] Params => [MethodParamDef.Required(MethodParamKind.Constant), MethodParamDef.Required(MethodParamKind.Constant)];
    protected override IExpression Create(MethodCallToken e, Datamodel dm) {
        if (e.Arguments[0] is not ValueConstantToken prop) throw new Exception("Only string arguments allowed as property in Relates expression.");
        if (e.Arguments[1] is not ValueConstantToken id) throw new Exception("Only string arguments allowed as id in Relates expression.");
        return new RelatesMethod(BuildSource(e, dm), dm, prop.GetStringValue(), id.GetStringValue());
    }
}

sealed class RelatesNotMethodDef : MethodDef {
    public override string[] Names => ["relatesnot"];
    public override int MinArgs => 2;
    public override MethodParamDef[] Params => [MethodParamDef.Required(MethodParamKind.Constant), MethodParamDef.Required(MethodParamKind.Constant)];
    protected override IExpression Create(MethodCallToken e, Datamodel dm) {
        if (e.Arguments[0] is not ValueConstantToken prop) throw new Exception("Only string arguments allowed as property in RelatesNot expression.");
        if (e.Arguments[1] is not ValueConstantToken id) throw new Exception("Only string arguments allowed as id in RelatesNot expression.");
        return new RelatesNotMethod(BuildSource(e, dm), dm, prop.GetStringValue(), id.GetStringValue());
    }
}

sealed class IncludeMethodDef : MethodDef {
    public override string[] Names => ["include"];
    public override int MinArgs => 1;
    public override MethodParamDef[] Params => [MethodParamDef.Required(MethodParamKind.Constant)];
    protected override IExpression Create(MethodCallToken e, Datamodel dm) {
        if (e.Arguments[0] is not ValueConstantToken prop) throw new Exception("Only string arguments allowed as property in include expression.");
        return new IncludeMethod(BuildSource(e, dm), dm, prop.GetStringValue());
    }
}

sealed class InRangeMethodDef : MethodDef {
    public override string[] Names => ["inrange"];
    public override int MinArgs => 2;
    public override MethodParamDef[] Params => [MethodParamDef.Required(MethodParamKind.Any), MethodParamDef.Required(MethodParamKind.Any)];
    protected override IExpression Create(MethodCallToken e, Datamodel dm) {
        if (e.Subject == null) throw new NullReferenceException();
        var path = (e.Subject as VariableReferenceToken)?.Name ?? throw new NullReferenceException();
        return new RangeExpression(path, e.Arguments[0].ToString(), e.Arguments[1].ToString());
    }
}

sealed class WhereCultureMethodDef : MethodDef {
    public override string[] Names => ["whereculture"];
    public override int MinArgs => 1;
    public override MethodParamDef[] Params => [MethodParamDef.Required(MethodParamKind.Any)];
    protected override IExpression Create(MethodCallToken e, Datamodel dm) => new WhereCultureMethod(BuildSource(e, dm), e.Arguments[0].ToString());
}

sealed class WhereCultureFallbackMethodDef : MethodDef {
    public override string[] Names => ["whereculturefallback"];
    public override int MinArgs => 1;
    public override MethodParamDef[] Params => [MethodParamDef.Required(MethodParamKind.Any)];
    protected override IExpression Create(MethodCallToken e, Datamodel dm) => new WhereCultureFallbackMethod(BuildSource(e, dm), e.Arguments[0].ToString());
}

sealed class WhereHiddenMethodDef : MethodDef {
    public override string[] Names => ["wherehidden"];
    public override int MinArgs => 1;
    public override MethodParamDef[] Params => [MethodParamDef.Required(MethodParamKind.Any)];
    protected override IExpression Create(MethodCallToken e, Datamodel dm) => new WhereHiddenMethod(BuildSource(e, dm), e.Arguments[0].ToString());
}

// ── Dispatcher ───────────────────────────────────────────────────────────────

internal class BuildMethod {

    private static readonly Dictionary<string, MethodDef> _registry = BuildRegistry(
        new SelectMethodDef(), new SelectIdMethodDef(), new WhereMethodDef(), new WhereTypesMethodDef(),
        new OrderByMethodDef(), new FacetsMethodDef(), new AddFacetMethodDef(), new AddValueFacetMethodDef(),
        new AddRangeFacetMethodDef(), new SetFacetValueMethodDef(), new SetFacetRangeValueMethodDef(),
        new SearchMethodDef(), new PageMethodDef(), new TakeMethodDef(), new SkipMethodDef(),
        new CountMethodDef(), new SumMethodDef(), new RelationMethodDef(), new WhereInMethodDef(),
        new WhereInIdsMethodDef(), new RelatesAnyMethodDef(), new RelatesMethodDef(), new RelatesNotMethodDef(),
        new IncludeMethodDef(), new InRangeMethodDef(), new WhereCultureMethodDef(),
        new WhereCultureFallbackMethodDef(), new WhereHiddenMethodDef()
    );

    private static Dictionary<string, MethodDef> BuildRegistry(params MethodDef[] defs)
        => defs.SelectMany(d => d.Names.Select(n => (n, d))).ToDictionary(x => x.n, x => x.d);

    public static IExpression BuildMethodCall(MethodCallToken e, Datamodel dm)
        => _registry.TryGetValue(e.Name.ToLower(), out var def)
            ? def.Build(e, dm)
            : throw new NotSupportedException($"The method \"{e}\" is not supported.");
}
