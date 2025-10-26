using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using static System.Linq.Enumerable;
using Relatude.DB.Query;

namespace Relatude.DB.Query {
    public static class TypeExtensions {
        public static bool InheritsFromOrImplements<T>(this Type type) => typeof(T).IsAssignableFrom(type);
        public static bool InheritsFromOrImplementsAny(this Type type, IEnumerable<Type> types) => type.InheritsFromOrImplementsAny(types.ToArray());
        public static bool InheritsFromOrImplementsAny(this Type type, params Type[] types) => types.Any(t => t.IsAssignableFrom(type));
    }
}
