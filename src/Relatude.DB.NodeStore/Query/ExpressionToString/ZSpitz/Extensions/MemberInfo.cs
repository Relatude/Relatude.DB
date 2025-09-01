using OneOf;
using Relatude.DB.Query.ExpressionToString.ZSpitz.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Relatude.DB.Query.ExpressionToString.ZSpitz.Extensions {
    public static class MemberInfoExtensions {
        public static bool HasAttribute<TAttribute>(this MemberInfo mi, bool inherit = false) where TAttribute : Attribute =>
            mi.GetCustomAttributes(typeof(TAttribute), inherit).Any();

        private static (bool isPublic, bool isStatic) bindingFlagsInfo(params MethodBase[] methods) {
            if (methods.None()) { throw new ArgumentException($"'{nameof(methods)} cannot be empty."); }
            var isStatic = false;
            var isPublic = false;
            methods.Where(x => x is { }).ForEach((x, index) => {
                if (index == 0) { isStatic = x.IsStatic; }
                if (x.IsPublic) {
                    isPublic = true;
                    return;
                }
            });
            return (isPublic, isStatic);
        }

        private static BindingFlags getBindingFlags(this MemberInfo mi) {
            var (isPublic, isStatic) = mi switch {
                ConstructorInfo ci => (ci.IsPublic, ci.IsStatic),
                EventInfo ei => bindingFlagsInfo(ei.AddMethod!, ei.RemoveMethod!, ei.RaiseMethod!),
                FieldInfo fi => (fi.IsPublic, fi.IsStatic),
                MethodInfo mthdi => (mthdi.IsPublic, mthdi.IsStatic),
                PropertyInfo pi => bindingFlagsInfo(pi.GetMethod!, pi.SetMethod!),
                //Type t => (t.IsPublic, t.I)
                _ => throw new NotImplementedException()
            };
            return
                (isPublic ? BindingFlags.Public : BindingFlags.NonPublic) |
                (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        }

        private static readonly HashSet<BindingFlags> defaultLookups = new() {
            BindingFlags.Public | BindingFlags.Instance,
            BindingFlags.Public | BindingFlags.Static
        };

        // .NET Framework is missing some of the GetMethod/GetConstructor overloads
        internal static (OneOf<MethodInfo, string> method, object?[] args) GetInputs(this MemberInfo mi) {
            var reflectedType = mi.ReflectedType!;
            var parameters = new List<(Type type, object? value)>();
            if (!(mi is ConstructorInfo)) {
                parameters.Add(typeof(string), mi.Name);
            }

            var flags = mi.getBindingFlags();

            if (mi is FieldInfo || mi is EventInfo) {
                // EventInfo and FieldInfo have two overloads: (string), and (string, bindingFlags)
                // If we need the BinidingFlags parameter, we'll use that overload
                if (flags.NotIn(defaultLookups)) { parameters.Add(typeof(BindingFlags), flags); }
            } else { // ConstrucctorInfo, MethodInfo, PropertyInfo
                var typesParameter = (mi switch {
                    ConstructorInfo ci => ci.GetParameters(),
                    MethodInfo mthdi => mthdi.GetParameters(),
                    PropertyInfo pi => pi.GetIndexParameters(),
                    _ => throw new NotImplementedException()
                }).Select(x => x.ParameterType).ToArray();

                var membersByName = (mi switch {
                    ConstructorInfo _ => reflectedType.GetConstructors(flags),
                    MethodInfo mthdi => reflectedType.GetMethods(flags).Where(x => x.Name == mi.Name),
                    PropertyInfo _ => reflectedType.GetIndexers(true, flags),
                    _ => Enumerable.Empty<MemberInfo>()
                }).ToList();

                var otherMemberCount = membersByName.Count - 1;

                if (mi is PropertyInfo) {
                    if (otherMemberCount > 0 && flags.NotIn(defaultLookups)) {
                        // string, BindingFlags, Binder, Type, Type[], ParameterModifier[]
                        parameters.AddRange(
                            (typeof(BindingFlags), flags),
                            (typeof(Binder), null),
                            (typeof(Type), null),
                            (typeof(Type[]), typesParameter),
                            (typeof(ParameterModifier[]), null)
                        );
                    } else {
                        // string
                        // string, Type[],
                        // string, BindingFlags
                        if (otherMemberCount > 0) { parameters.Add(typeof(Type[]), typesParameter); }
                        if (flags.NotIn(defaultLookups)) { parameters.Add(typeof(BindingFlags), flags); }
                    }
                } else if (mi is ConstructorInfo) {
                    if (flags.NotIn(defaultLookups)) {
                        // BindingFlags, Binder, Type[], ParameterModifier[]
                        parameters.AddRange(
                            (typeof(BindingFlags), flags),
                            (typeof(Binder), null),
                            (typeof(Type[]), typesParameter),
                            (typeof(ParameterModifier[]), null)
                        );
                    } else {
                        // Type[]
                        parameters.Add(typeof(Type[]), typesParameter);
                    }
                } else if (mi is MethodInfo mthdi) {
                    var arity = mthdi.GetGenericArguments().Length;
                    var methodsByName = membersByName.Cast<MethodInfo>().Select(x => new {
                        arity = x.GetGenericArguments().Length,
                        x
                    }).ToList();

                    if (methodsByName.Any(x => x.arity != arity)) {
                        parameters.Add((typeof(int), arity));
                    } else {
                        arity = -1;
                    }

                    if (
                        flags.NotIn(defaultLookups) &&
                        (otherMemberCount > 0 || arity > -1)
                    ) {
                        // we need binding flags, and either the type array or the arity
                        // we therefore can't use the simpler overloads
                        // string, int, BindingFlags, Binder, Type[], ParameterModifier[] 
                        // string, BindingFlags, Binder, Type[], ParameterModifier[]
                        parameters.AddRange(
                            (typeof(BindingFlags), flags),
                            (typeof(Binder), null),
                            (typeof(Type[]), typesParameter),
                            (typeof(ParameterModifier[]), null)
                        );
                    } else if (arity > -1) {
                        // string, int, Type[]
                        parameters.Add(typeof(Type[]), typesParameter);
                    } else {
                        // string
                        // string, Type[]
                        // string, BindingFlags
                        if (otherMemberCount > 0) { parameters.Add(typeof(Type[]), typesParameter); }
                        if (flags.NotIn(defaultLookups)) { parameters.Add(typeof(BindingFlags), flags); }
                    }
                } else {
                    throw new NotImplementedException();
                }
            }

            var methodName = mi switch {
                FieldInfo _ => "GetField",
                EventInfo _ => "GetEvent",
                PropertyInfo _ => "GetProperty",
                ConstructorInfo _ => "GetConstructor",
                MethodInfo _ => "GetMethod",
                _ => throw new NotImplementedException()
            };

            var method = reflectedType.GetType().GetMethod(methodName, parameters.Select(x => x.type).ToArray());
            var values = parameters.Select(x => x.value).ToArray();
            return method is { } ?
                (method, values) :
                (methodName, values);
        }
    }
}
