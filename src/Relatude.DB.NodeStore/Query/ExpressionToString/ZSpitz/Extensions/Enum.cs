using System;
using System.Collections.Generic;
using System.Linq;

namespace Relatude.DB.Query.ExpressionToString.ZSpitz.Extensions {
    // https://stackoverflow.com/a/5543006/111794
    internal static class EnumExtensions {
        public static IEnumerable<Enum> GetFlags(this Enum value) =>
            getFlags(value, Enum.GetValues(value.GetType()).Cast<Enum>().ToArray());

        public static IEnumerable<Enum> GetIndividualFlags(this Enum value) =>
            getFlags(value, getFlagValues(value.GetType()).ToArray());

        private static IEnumerable<Enum> getFlags(Enum value, Enum[] values) {
            var bits = Convert.ToInt64(value);
            List<Enum> results = new();
            for (var i = values.Length - 1; i >= 0; i--) {
                var mask = Convert.ToInt64(values[i]);
                if (i == 0 && mask == 0L) {
                    break;
                }

                if ((bits & mask) == mask) {
                    results.Add(values[i]);
                    bits -= mask;
                }
            }

            return
                bits != 0L ?
                    Enumerable.Empty<Enum>() :
                Convert.ToInt64(value) != 0L ?
                    results.Reverse<Enum>() :
                bits == Convert.ToInt64(value) && values.Length > 0 && Convert.ToInt64(values[0]) == 0L ?
                    values.Take(1) :
                    Enumerable.Empty<Enum>();
        }

        private static IEnumerable<Enum> getFlagValues(Type enumType) {
            long flag = 0x1;
            foreach (var value in Enum.GetValues(enumType).Cast<Enum>()) {
                var bits = Convert.ToInt64(value);
                if (bits == 0L) {
                    //yield return value;
                    continue; // skip the zero value
                }

                while (flag < bits) {
                    flag <<= 1;
                }

                if (flag == bits) {
                    yield return value;
                }
            }
        }
    }
}
