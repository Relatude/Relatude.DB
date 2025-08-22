using WAF.Query.ExpressionToString.ExpressionTreeToString;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using static WAF.Query.ExpressionToString.ZSpitz.Functions;

namespace WAF.Query.ExpressionToString.ExpressionTreeToString.Util.Extensions {
    internal static class ListInsertionPointExtensions {
        internal static InsertionPoint Get(this List<InsertionPoint> lst, string key) {
            foreach (var x in lst) {
                if (x.key == key) { return x; }
            }
            throw new KeyNotFoundException();
        }

        internal static bool TryGet(this List<InsertionPoint> lst, string key, [MaybeNull] out InsertionPoint ip) {
            foreach (var x in lst) {
                if (x.key == key) {
                    ip = x;
                    return true;
                }
            }
            ip = null;
            return false;
        }

        internal static void Add(this List<InsertionPoint> lst, string key, InsertionPoint ip) {
            if (lst.TryGet(key, out var _)) {
                throw new ArgumentException($"An item with the key {key} has already been added.");
            }
            lst.Add(ip);
        }
    }
}
