namespace WAF.Common {
    public class SearchSetOperations {
        public static Dictionary<int, double> Intersection(List<Dictionary<int, double>> sets) {
            if (sets.Count == 1) return sets[0];
            if (sets.Count == 0) return new Dictionary<int, double>();
            // start with the smallest collection
            sets = sets.OrderBy(r => r.Count).ToList();
            var intersection = sets[0];
            for (var i = 1; i < sets.Count; i++) {
                var toRemove = new List<int>();
                foreach (var id in intersection.Keys) {
                    if (sets[i].TryGetValue(id, out var score)) {
                        intersection[id] += score; // combine scores from both hits
                    } else {
                        toRemove.Add(id);
                    }
                }
                foreach (var id in toRemove) intersection.Remove(id);
            }
            return intersection;
        }
        public static Dictionary<int, double> Union(List<Dictionary<int, double>> sets) {
            if (sets.Count == 1) return sets[0];
            if (sets.Count == 0) return new Dictionary<int, double>();
            var union = new Dictionary<int, double>();
            foreach (var set in sets) {
                foreach (var kv in set) {
                    if (union.TryGetValue(kv.Key, out var score)) {
                        union[kv.Key] += score;
                    } else {
                        union[kv.Key] = score;
                    }
                }
            }
            return union;
        }
        public static SearchHit[] Intersection(List<SearchHit[]> sets) {
            if (sets.Count == 1) return sets[0];
            if (sets.Count == 0) return new SearchHit[] { };
            // start with the smallest collection
            sets = sets.OrderBy(r => r.Length).ToList();
            var intersection = sets[0].ToDictionary(kv => kv.NodeId, kv => kv.Score);
            for (var i = 1; i < sets.Count; i++) {
                var toRemove = new List<int>();
                var scoreById = sets[i].ToDictionary(kv => kv.NodeId, kv => kv.Score);
                foreach (var id in intersection.Keys) {
                    if (scoreById.TryGetValue(id, out var score)) {
                        intersection[id] += score; // combine scores from both hits
                    } else {
                        toRemove.Add(id);
                    }
                }
                foreach (var id in toRemove) intersection.Remove(id);
            }
            return intersection.Select(kv => new SearchHit(kv.Key, kv.Value)).ToArray();
        }
        public static HashSet<int> Intersection(List<HashSet<int>> sets) {
            if (sets.Count == 1) return sets[0];
            if (sets.Count == 0) return [];
            sets = sets.OrderBy(r => r.Count).ToList();
            var intersection = sets[0]; // be careful, allowing modification of input set...
            for (var i = 1; i < sets.Count; i++) {
                var toRemove = new List<int>();
                foreach (var id in intersection) {
                    if (!sets[i].Contains(id)) toRemove.Add(id);
                }
                foreach (var id in toRemove) intersection.Remove(id);
            }
            return intersection;
        }
        public static HashSet<int> Union(List<HashSet<int>> sets) {
            if (sets.Count == 1) return sets[0];
            if (sets.Count == 0) return [];
            var union = new HashSet<int>();
            foreach (var set in sets) {
                foreach (var id in set) union.Add(id);
            }
            return union;
        }
    }
}
