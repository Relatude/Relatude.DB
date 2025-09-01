using OneOf;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using static System.Linq.Enumerable;
using static Relatude.DB.Query.ExpressionToString.ZSpitz.Language;
using static Relatude.DB.Query.ExpressionToString.ZSpitz.Functions;
using System.Diagnostics.CodeAnalysis;
using Relatude.DB.Query.ExpressionToString.ZSpitz.Extensions;
using Relatude.DB.Query.ExpressionToString.ZSpitz;
using Relatude.DB.Query.ExpressionToString.OneOf;

namespace Relatude.DB.Query.ExpressionToString.ZSpitz.Extensions {
    public static class TypeExtensions {
        public static Type UnderlyingIfNullable(this Type type) => Nullable.GetUnderlyingType(type) ?? type;

        public static bool IsNullable(this Type t, bool orReferenceType = false) =>
            orReferenceType && !t.IsValueType ||
            t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>);

        private static readonly Dictionary<Type, bool> numericTypes = new() {
            [typeof(byte)] = true,
            [typeof(short)] = true,
            [typeof(int)] = true,
            [typeof(long)] = true,
            [typeof(sbyte)] = true,
            [typeof(ushort)] = true,
            [typeof(uint)] = true,
            [typeof(ulong)] = true,
            [typeof(BigInteger)] = true,
            [typeof(float)] = false,
            [typeof(double)] = false,
            [typeof(decimal)] = false
        };

        public static bool IsNumeric(this Type type) => numericTypes.ContainsKey(type);
        public static bool IsIntegral(this Type type) => numericTypes.TryGetValue(type, out var isIntegeral) && isIntegeral;
        public static bool IsNonIntegral(this Type type) => numericTypes.TryGetValue(type, out var isIntegeral) && !isIntegeral;

        // TODO implement some sort of caching here?
        private static T readStaticField<T>(string name) {
            var fld = typeof(T).GetField(name, BindingFlags.Public | BindingFlags.Static);
            if (fld is null) { throw new InvalidOperationException($"Type '{typeof(T)}' doesn't have a '{name}' field"); }
            var value = fld.GetValue(null);
            return value is not T && value is not null ?
                throw new InvalidOperationException($"Field '{name}' doesn't return a value of type '{typeof(T)}'") :
                (T)value!;
        }
        public static T MinValue<T>() => readStaticField<T>("MinValue");
        public static T MaxValue<T>() => readStaticField<T>("MaxValue");

        public static bool InheritsFromOrImplements<T>(this Type type) => typeof(T).IsAssignableFrom(type);

        public static bool InheritsFromOrImplementsAny(this Type type, IEnumerable<Type> types) => type.InheritsFromOrImplementsAny(types.ToArray());
        public static bool InheritsFromOrImplementsAny(this Type type, params Type[] types) => types.Any(t => t.IsAssignableFrom(type));

        public static bool IsClosureClass(this Type type) =>
            type != null && type.HasAttribute<CompilerGeneratedAttribute>() && type.Name.ContainsAny("DisplayClass", "Closure$");

        public static bool IsAnonymous(this Type type) =>
            type.HasAttribute<CompilerGeneratedAttribute>() && type.Name.Contains("Anonymous") && type.Name.ContainsAny("<>", "VB$") && !type.InheritsFromOrImplements<Delegate>();

        public static bool IsVBAnonymousDelegate(this Type type) =>
            type.HasAttribute<CompilerGeneratedAttribute>() && type.Name.Contains("VB$AnonymousDelegate");

        private static readonly Dictionary<Type, string> csharpKeywordTypes = new() {
            {typeof(bool), "bool"},
            {typeof(byte), "byte"},
            {typeof(sbyte), "sbyte"},
            {typeof(char), "char"},
            {typeof(decimal), "decimal"},
            {typeof(double), "double"},
            {typeof(float), "float"},
            {typeof(int), "int"},
            {typeof(uint), "uint"},
            {typeof(long), "long"},
            {typeof(ulong), "ulong"},
            {typeof(object), "object"},
            {typeof(short), "short"},
            {typeof(ushort), "ushort"},
            {typeof(string), "string"},
            {typeof(void), "void" }
        };

        private static readonly Dictionary<Type, string> vbKeywordTypes = new() {
            {typeof(bool), "Boolean"},
            {typeof(byte), "Byte"},
            {typeof(char), "Char"},
            {typeof(DateTime), "Date"},
            {typeof(decimal), "Decimal"},
            {typeof(double), "Double"},
            {typeof(int), "Integer"},
            {typeof(long), "Long"},
            {typeof(object), "Object"},
            {typeof(sbyte), "SByte"},
            {typeof(short), "Short"},
            {typeof(float), "Single"},
            {typeof(string), "String"},
            {typeof(uint), "UInteger"},
            {typeof(ulong), "ULong"},
            {typeof(ushort), "UShort"}
        };

        public static string? FriendlyName([NotNullIfNotNull("type")] this Type? type, OneOf<string, Language?> languageArg) {
            if (type is null) { return null; }

            var language = languageArg.ResolveLanguage();

            if (language.NotIn(CSharp, VisualBasic)) { return type.Name; }

            if (type.IsClosureClass()) {
                return language == CSharp ? "<closure>" : "<Closure>";
            }

            if (type.IsAnonymous()) {
                return "{ " + type.GetProperties().Joined(", ", p => {
                    var name = p.Name;
                    var typename = p.PropertyType.FriendlyName(language);
                    return language == CSharp ?
                        $"{typename} {name}" :
                        $".{name} As {typename}"; // language == VisualBasic 
                }) + " }";
            }

            if (type.IsArray) {
                (var left, var right) =
                    language == CSharp ?
                        ("[", "]") :
                        ("(", ")"); // language == VisualBasic
                var nestedArrayTypes = type.NestedArrayTypes().ToList();
                var arraySpecifiers = nestedArrayTypes.JoinedT("",
                    (current, _, index) => left + Repeat("", current.GetArrayRank()).Joined() + right
                );
                return nestedArrayTypes.Last().root!.FriendlyName(language) + arraySpecifiers;
            }

            if (!type.IsGenericType) {
                // Not sure if such a thing is possible
                if (type.IsVBAnonymousDelegate()) {
                    return "VB$AnonymousDelegate";
                }

                var dict = language == CSharp ?
                    csharpKeywordTypes :
                    vbKeywordTypes; // language == VisualBasic
                return dict.TryGetValue(type, out var ret) ?
                    ret :
                    type.Name;
            }

            if (type.GetGenericTypeDefinition() == typeof(Nullable<>)) {
                return type.UnderlyingIfNullable().FriendlyName(language) + "?";
            }

            if (type.IsGenericParameter) {
                return type.Name;
            }

            var args = type.GetGenericArguments();
            var parts = type.GetGenericArguments().Joined(", ", t => t.FriendlyName(language)!);
            var nongenericName = type.NonGenericName();
            return language == CSharp ?
                $"{nongenericName}<{parts}>" :
                $"{nongenericName}(Of {parts})";
        }

        public static string NonGenericName(this Type t) {
            var backtickIndex = t.Name.IndexOf('`');
            var nongenericName =
                t.IsVBAnonymousDelegate() ?
                    "VB$AnonymousDelegate" :
                    t.Name.Substring(0, backtickIndex);
            return nongenericName;
        }

        private static readonly Dictionary<Type, bool> tupleTypes = new() {
            { typeof(ValueTuple<>), true },
            {typeof(ValueTuple<,>), true },
            {typeof(ValueTuple<,,>), true },
            {typeof(ValueTuple<,,,>), true },
            {typeof(ValueTuple<,,,,>), true },
            {typeof(ValueTuple<,,,,,>), true },
            {typeof(ValueTuple<,,,,,,>), true },
            {typeof(ValueTuple<,,,,,,,>), true },
            { typeof(Tuple<>),false },
            {typeof(Tuple<,>),false },
            {typeof(Tuple<,,>),false },
            {typeof(Tuple<,,,>),false },
            {typeof(Tuple<,,,,>),false },
            {typeof(Tuple<,,,,,>),false },
            {typeof(Tuple<,,,,,,>),false }
        };

        public static bool IsTupleType(this Type type) =>
            type.IsTupleType(out var _);

        public static bool IsTupleType(this Type type, out bool isValueTuple) {
            isValueTuple = false;

            if (!type.IsGenericType) { return false; }
            var openType = type.UnderlyingIfNullable().GetGenericTypeDefinition();
            return tupleTypes.TryGetValue(openType, out isValueTuple);
        }

        public static IEnumerable<(Type current, Type? root)> NestedArrayTypes(this Type type) {
            var currentType = type;
            while (currentType.IsArray) {
                var nextType = currentType.GetElementType()!;
                if (nextType.IsArray) {
                    yield return (currentType, null);
                } else {
                    yield return (currentType, nextType);
                    break;
                }
                currentType = nextType;
            }
        }

        public static IEnumerable<T> GetAttributes<T>(this Type type, bool inherit) where T : Attribute =>
            type.GetCustomAttributes(typeof(T), inherit).Cast<T>();

        private static readonly BindingFlags defaultLookup = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        public static PropertyInfo[] GetIndexers(this Type type, bool inherit, BindingFlags? bindingFlags = default) {
            bindingFlags ??= defaultLookup;
            var memberName = type.GetAttributes<DefaultMemberAttribute>(inherit).FirstOrDefault()?.MemberName;
            return memberName == null ?
#if NET452
                EmptyArray<PropertyInfo>() :
#else
                Array.Empty<PropertyInfo>() :
#endif
                type.GetProperties(bindingFlags.Value).Where(x => x.Name == memberName).ToArray();
        }

        // https://stackoverflow.com/a/55244482
        /// <summary>Returns T for T[] and types that implement IEnumerable&lt;T&gt;</summary>
        public static Type? ItemType(this Type type) {
            if (type.IsArray) {
                return type.GetElementType();
            }

            // type is IEnumerable<T>;
            if (ImplIEnumT(type)) {
                return type.GetGenericArguments().First();
            }

            // type implements/extends IEnumerable<T>;
            var enumType = type.GetInterfaces().Where(ImplIEnumT).Select(t => t.GetGenericArguments().First()).FirstOrDefault();
            if (enumType != null) {
                return enumType;
            }

            // type is IEnumerable
            return
                IsIEnum(type) || type.GetInterfaces().Any(IsIEnum) ?
                    typeof(object) :
                    null;

            static bool IsIEnum(Type t) => t == typeof(System.Collections.IEnumerable);
            static bool ImplIEnumT(Type t) => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>);
        }

        public static IEnumerable<Type> BaseTypes(this Type t, bool genericDefinitions = false, bool andSelf = false) {
            if (andSelf) {
                yield return t;
            }
            if (t.IsGenericType && genericDefinitions) {
                yield return t.GetGenericTypeDefinition();
            }

            foreach (var i in t.GetInterfaces()) {
                yield return reduceToGeneric(i);
            }
            if (t.BaseType != null) {
                foreach (var baseType in t.BaseType.BaseTypes(genericDefinitions, true)) {
                    yield return reduceToGeneric(baseType);
                }
            }

            Type reduceToGeneric(Type sourceType) =>
                sourceType.IsGenericType && genericDefinitions ?
                    sourceType.GetGenericTypeDefinition() :
                    sourceType;
        }

        public static bool ContainsType(this Type t, Type value) =>
            t == value ||
                t.IsGenericType &&
                !t.IsGenericTypeDefinition &&
                (t.GetGenericTypeDefinition() == value || t.GetGenericArguments().Any(x => x.ContainsType(value)));

        private static readonly Dictionary<Type, Type[]> builtinImplicitConversions = new[] {
            (typeof(sbyte), new [] {
                typeof(short), typeof(int), typeof(long), typeof(float), typeof(double), typeof(decimal)
            }),
            (typeof(byte), new [] {
                typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal)
            }),
            (typeof(short), new [] {
                typeof(int), typeof(long), typeof(float), typeof(double), typeof(decimal)
            }),
            (typeof(ushort), new [] {
                typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal)
            }),
            (typeof(int), new [] {
                typeof(long), typeof(float), typeof(double), typeof(decimal)
            }),
            (typeof(uint), new [] {
                typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal)
            }),
            (typeof(long), new [] {
                typeof(float), typeof(double), typeof(decimal)
            }),
            (typeof(ulong), new [] {
                typeof(float), typeof(double), typeof(decimal)
            }),
            (typeof(char), new [] {
                typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal)
            }),
            (typeof(float), new [] {
                typeof(double)
            })
        }.ToDictionary();
        public static (ConversionStrategy strategy, MethodInfo? method) GetImplicitConversionTo(this Type @base, Type target) {
            MethodInfo? getConversionOnType(Type type) =>
                type
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(mi => mi.Name == "op_Implicit" && mi.ReturnType == target)
                    .FirstOrDefault(mi => mi.GetParameters().FirstOrDefault()?.ParameterType == @base);

            if (target.IsAssignableFrom(@base)) {
                return (ConversionStrategy.Assignable, null);
            }
            if (builtinImplicitConversions.TryGetValue(@base, out var targets) && target.In(targets)) {
                return (ConversionStrategy.BuiltIn, null);
            }
            var method =
                getConversionOnType(@base) ??
                getConversionOnType(target);
            return (
                method is { } ? ConversionStrategy.Method : ConversionStrategy.None,
                method
            );
        }
        // OLE added cache
        static Dictionary<Type, Dictionary<Type, bool>> _cache = [];
        // TODO consider caching the result of this function
        public static bool HasImplicitConversionTo(this Type @base, Type target) {
            lock (_cache) {
                if (!_cache.TryGetValue(@base, out var inner)) {
                    inner = new();
                    _cache[@base] = inner;
                }
                if (!inner.TryGetValue(target, out var result)) {
                    result = @base.GetImplicitConversionTo(target).strategy.In(
                        ConversionStrategy.Assignable,
                        ConversionStrategy.BuiltIn,
                        ConversionStrategy.Method
                    );
                    inner[target] = result;
                }
                return result;
            }
        }
        public static Type? OneOfType(this Type t) =>
            t.BaseTypes(false, true)
                .LastOrDefault(x => x != typeof(IOneOf) && typeof(IOneOf).IsAssignableFrom(x));

        public static Type[] OneOfSubtypes(this Type t) =>
            t.OneOfType()?.GetGenericArguments() ??
#if NET452
                EmptyArray<Type>();
#else
                Array.Empty<Type>();
#endif

        public static T CreateInstance<T>(this Type type, object[]? args = null) => (T)Activator.CreateInstance(type, args)!;
    }
}
