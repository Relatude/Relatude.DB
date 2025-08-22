using OneOf;
using WAF.Query.ExpressionToString.ExpressionTreeToString;
using System;
using static WAF.Query.ExpressionToString.ExpressionTreeToString.RendererNames;

namespace WAF.Query.ExpressionToString.ExpressionTreeToString.Util.Extensions {
    internal static class OneOfStringRendererExtensions {
        internal static string ResolveRendererKey(this OneOf<string, BuiltinRenderer> rendererArg) =>
            rendererArg.IsT0 ?
                rendererArg.AsT0 :
                rendererArg.AsT1 switch {
                    BuiltinRenderer.CSharp => CSharp,
                    BuiltinRenderer.VisualBasic => VisualBasic,
                    BuiltinRenderer.FactoryMethods => FactoryMethods,
                    BuiltinRenderer.ObjectNotation => ObjectNotation,
                    BuiltinRenderer.TextualTree => TextualTree,
                    BuiltinRenderer.ToString => ToStringRenderer,
                    BuiltinRenderer.DebugView => DebugView,
                    BuiltinRenderer.DynamicLinq => DynamicLinq,
                    _ => throw new ArgumentException("Unknown renderer.")
                };
    }
}
