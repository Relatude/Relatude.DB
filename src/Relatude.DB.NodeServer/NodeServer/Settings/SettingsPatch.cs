using System.Text;
using System.Text.Json.Nodes;

namespace Relatude.DB.NodeServer.Settings;

/// <summary>
/// The difference between two settings trees, as JSON in the same shape as relatude.db.json, and the
/// merge that applies one. It is what relatude.db.overrides.json holds, and what the server keeps in
/// memory to take the values set by application code out again before it saves.
///
/// <para>Objects are compared key by key and only the keys that differ are written. A list whose
/// elements all carry a distinct <c>Id</c> - storage providers, file stores, model sources, index
/// engines, databases - is compared element by element: an element the target changed is written as
/// its <c>Id</c> plus the fields that changed, a new one in full with <c>"$added": true</c>, and a
/// missing one as its <c>Id</c> with <c>"$removed": true</c>. A list without ids is written whole when
/// it differs. Order inside a list is not part of a patch: a merge keeps the order of the tree it
/// merges into and appends the added elements at the end. An object that has lost keys - a dictionary
/// such as <c>CompletionModelsByKey</c>; a settings object always writes every property - is written
/// whole with <c>"$replace": true</c>, since a key-by-key merge could never take one away.</para>
///
/// <para>Paths name a setting the way <see cref="SettingsAccessor"/> and
/// <see cref="SettingsOverlay.IsOverridden"/> do: <c>ContainerSettings[8f1c...].IOSettings[2b7e...].Path</c>,
/// elements addressed by their id.</para>
/// </summary>
public static class SettingsPatch {
    public const string AddedMarker = "$added";
    public const string RemovedMarker = "$removed";
    public const string ReplaceMarker = "$replace";

    public enum EntryKind {
        /// <summary>A value replaced: a scalar, or an object or list set as a whole.</summary>
        Value,
        /// <summary>A list element the patch adds; <see cref="Entry.Value"/> is the whole element.</summary>
        Added,
        /// <summary>A list element the patch removes.</summary>
        Removed,
    }

    /// <summary>One thing a patch changes. <see cref="Value"/> is what it sets, without markers.</summary>
    public sealed record Entry(string Path, EntryKind Kind, JsonNode? Value);

    /// <summary>What <paramref name="to"/> changes compared with <paramref name="from"/>. Merging the
    /// result into a copy of <paramref name="from"/> gives a tree equal to <paramref name="to"/>, apart
    /// from the order of list elements.</summary>
    public static JsonObject Diff(JsonObject from, JsonObject to) => diffObject(from, to);

    /// <summary>Applies a patch in place. A change to a list element the tree does not have - one that
    /// was patched and has since been removed from relatude.db.json - is reported and skipped, never
    /// added as a half element.</summary>
    public static void Merge(JsonObject target, JsonObject patch, Action<string>? warn = null) => mergeObject(target, patch, "", warn);

    /// <summary>Everything a patch changes in <paramref name="baseRoot"/>, one entry per setting, list
    /// element added or list element removed - the same reading <see cref="Merge"/> gives it, so a
    /// change that would be skipped there is not listed here either.</summary>
    public static List<Entry> Entries(JsonObject patch, JsonObject baseRoot) {
        var entries = new List<Entry>();
        walkObject(patch, baseRoot, "", e => { entries.Add(e); return true; });
        return entries;
    }

    /// <summary>The part of a patch made of the entries <paramref name="keep"/> accepts.</summary>
    public static JsonObject Select(JsonObject patch, JsonObject baseRoot, Func<Entry, bool> keep) => walkObject(patch, baseRoot, "", keep);

    /// <summary>The value at a path, or null when anything on the way is missing.</summary>
    public static JsonNode? Read(JsonObject root, string path) {
        JsonNode? current = root;
        foreach (var (property, id) in parse(path)) {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(property, out current)) return null;
            if (id == null) continue;
            if (current is not JsonArray array) return null;
            var index = indexOfId(array, id.Value);
            if (index < 0) return null;
            current = array[index];
        }
        return current;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="candidate"/> or lies below it.</summary>
    public static bool IsAtOrBelow(string path, string candidate) {
        if (!path.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)) return false;
        if (path.Length == candidate.Length) return true;
        var next = path[candidate.Length];
        return next == '.' || next == '[';
    }

    // ---- diff ----

    static JsonObject diffObject(JsonObject from, JsonObject to) {
        var result = new JsonObject();
        foreach (var (key, toValue) in to) {
            from.TryGetPropertyValue(key, out var fromValue);
            if (JsonNode.DeepEquals(fromValue, toValue)) continue;
            if (fromValue is JsonObject fromObject && toValue is JsonObject toObject) {
                if (fromObject.Any(kv => !toObject.ContainsKey(kv.Key))) {
                    var replaced = (JsonObject)toObject.DeepClone();
                    replaced[ReplaceMarker] = true;
                    result[key] = replaced;
                    continue;
                }
                var inner = diffObject(fromObject, toObject);
                if (inner.Count > 0) result[key] = inner;
            } else if (fromValue is JsonArray fromArray && toValue is JsonArray toArray && isKeyed(fromArray) && isKeyed(toArray)) {
                var inner = diffArray(fromArray, toArray);
                if (inner.Count > 0) result[key] = inner;
            } else {
                result[key] = whole(toValue);
            }
        }
        return result;
    }

    // A list set as a whole carries the added marker on its elements when they have ids, so the patch
    // still reads as additions should relatude.db.json later grow a list of its own there.
    static JsonNode? whole(JsonNode? value) {
        if (value is not JsonArray array || array.Count == 0 || !isKeyed(array)) return value?.DeepClone();
        var result = new JsonArray();
        foreach (var element in array) result.Add(added((JsonObject)element!));
        return result;
    }

    static JsonArray diffArray(JsonArray from, JsonArray to) {
        var result = new JsonArray();
        var fromById = new Dictionary<Guid, JsonObject>();
        foreach (var element in from) fromById[idOf(element)!.Value] = (JsonObject)element!;
        var present = new HashSet<Guid>();
        foreach (var element in to) {
            var obj = (JsonObject)element!;
            var id = idOf(obj)!.Value;
            present.Add(id);
            if (!fromById.TryGetValue(id, out var before)) {
                result.Add(added(obj));
                continue;
            }
            if (JsonNode.DeepEquals(before, obj)) continue;
            var inner = diffObject(before, obj);
            if (inner.Count == 0) continue;
            var patch = new JsonObject { ["Id"] = id.ToString() };
            foreach (var (key, value) in inner.ToArray()) {
                inner.Remove(key);
                patch[key] = value;
            }
            result.Add(patch);
        }
        foreach (var element in from) {
            var id = idOf(element)!.Value;
            if (!present.Contains(id)) result.Add(new JsonObject { ["Id"] = id.ToString(), [RemovedMarker] = true });
        }
        return result;
    }

    static JsonObject added(JsonObject element) {
        var result = new JsonObject { ["Id"] = idOf(element)!.Value.ToString(), [AddedMarker] = true };
        foreach (var (key, value) in element) {
            if (key is "Id" or AddedMarker or RemovedMarker) continue;
            result[key] = value?.DeepClone();
        }
        return result;
    }

    // ---- merge ----

    static void mergeObject(JsonObject target, JsonObject patch, string path, Action<string>? warn) {
        foreach (var (key, value) in patch) {
            var childPath = join(path, key);
            target.TryGetPropertyValue(key, out var current);
            if (value is JsonObject patchObject && current is JsonObject currentObject && !flag(patchObject, ReplaceMarker)) mergeObject(currentObject, patchObject, childPath, warn);
            else if (value is JsonArray patchArray && current is JsonArray currentArray && isElementPatch(patchArray) && isKeyed(currentArray)) mergeArray(currentArray, patchArray, childPath, warn);
            else target[key] = strip(value);
        }
    }

    static void mergeArray(JsonArray target, JsonArray patch, string path, Action<string>? warn) {
        foreach (var element in patch) {
            var obj = (JsonObject)element!;
            var id = idOf(obj)!.Value;
            var elementPath = path + "[" + id + "]";
            var index = indexOfId(target, id);
            if (flag(obj, RemovedMarker)) {
                if (index >= 0) target.RemoveAt(index);
                continue;
            }
            if (index >= 0) {
                // an element added here that relatude.db.json has since been given (moved there, say)
                // is no longer an addition: what is left of it is a change to that element
                mergeObject((JsonObject)target[index]!, fields(obj), elementPath, warn);
                continue;
            }
            if (flag(obj, AddedMarker)) {
                target.Add(strip(obj));
                continue;
            }
            warn?.Invoke("A change to " + elementPath + " was skipped: that element is no longer in the settings it applies to.");
        }
    }

    /// <summary>A value as it is taken over whole: without the markers, and without any element they
    /// say is removed.</summary>
    internal static JsonNode? strip(JsonNode? value) {
        switch (value) {
            case JsonObject obj: {
                var result = new JsonObject();
                foreach (var (key, inner) in obj) {
                    if (key is AddedMarker or RemovedMarker or ReplaceMarker) continue;
                    result[key] = strip(inner);
                }
                return result;
            }
            case JsonArray array: {
                var result = new JsonArray();
                foreach (var inner in array) {
                    if (inner is JsonObject o && flag(o, RemovedMarker)) continue;
                    result.Add(strip(inner));
                }
                return result;
            }
            default:
                return value?.DeepClone();
        }
    }

    // an element's own fields, without the id that addresses it or the markers
    static JsonObject fields(JsonObject element) {
        var result = new JsonObject();
        foreach (var (key, value) in element) {
            if (key is "Id" or AddedMarker or RemovedMarker or ReplaceMarker) continue;
            result[key] = value?.DeepClone();
        }
        return result;
    }

    // ---- reading a patch ----

    // Walks a patch the way mergeObject reads it, hands every entry to keep, and returns the patch made
    // of the entries it kept.
    static JsonObject walkObject(JsonObject patch, JsonObject? baseObject, string path, Func<Entry, bool> keep) {
        var result = new JsonObject();
        foreach (var (key, value) in patch) {
            var childPath = join(path, key);
            JsonNode? baseValue = null;
            baseObject?.TryGetPropertyValue(key, out baseValue);
            if (value is JsonObject patchObject && baseValue is JsonObject baseChild && !flag(patchObject, ReplaceMarker)) {
                var inner = walkObject(patchObject, baseChild, childPath, keep);
                if (inner.Count > 0) result[key] = inner;
            } else if (value is JsonArray patchArray && baseValue is JsonArray baseArray && isElementPatch(patchArray) && isKeyed(baseArray)) {
                var inner = walkArray(patchArray, baseArray, childPath, keep);
                if (inner.Count > 0) result[key] = inner;
            } else if (keep(new Entry(childPath, EntryKind.Value, strip(value)))) {
                result[key] = value?.DeepClone();
            }
        }
        return result;
    }

    static JsonArray walkArray(JsonArray patch, JsonArray baseArray, string path, Func<Entry, bool> keep) {
        var result = new JsonArray();
        foreach (var element in patch) {
            var obj = (JsonObject)element!;
            var id = idOf(obj)!.Value;
            var elementPath = path + "[" + id + "]";
            var index = indexOfId(baseArray, id);
            if (flag(obj, RemovedMarker)) {
                if (index >= 0 && keep(new Entry(elementPath, EntryKind.Removed, null))) result.Add(obj.DeepClone());
                continue;
            }
            if (index >= 0) {
                var inner = walkObject(fields(obj), (JsonObject)baseArray[index]!, elementPath, keep);
                if (inner.Count == 0) continue;
                var kept = new JsonObject { ["Id"] = id.ToString() };
                foreach (var (key, value) in inner.ToArray()) {
                    inner.Remove(key);
                    kept[key] = value;
                }
                result.Add(kept);
                continue;
            }
            if (flag(obj, AddedMarker) && keep(new Entry(elementPath, EntryKind.Added, strip(obj)))) result.Add(obj.DeepClone());
        }
        return result;
    }

    // ---- helpers ----

    /// <summary>True when every element is an object with an id of its own, none shared: a list that
    /// can be compared element by element. An empty list qualifies.</summary>
    static bool isKeyed(JsonArray array) {
        var seen = new HashSet<Guid>();
        foreach (var element in array) {
            var id = idOf(element);
            if (id == null || !seen.Add(id.Value)) return false;
        }
        return true;
    }

    // a patch never writes an empty element list (nothing changed means nothing written), so an empty
    // list in a patch can only mean "this list, empty" and is taken whole
    static bool isElementPatch(JsonArray array) {
        if (array.Count == 0) return false;
        foreach (var element in array) if (idOf(element) == null) return false;
        return true;
    }

    static bool flag(JsonObject obj, string marker)
        => obj.TryGetPropertyValue(marker, out var value) && value is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    internal static Guid? idOf(JsonNode? element) {
        if (element is not JsonObject obj) return null;
        if (!obj.TryGetPropertyValue("Id", out var value) || value is not JsonValue v) return null;
        return v.TryGetValue<string>(out var s) && Guid.TryParse(s, out var id) ? id : null;
    }

    static int indexOfId(JsonArray array, Guid id) {
        for (var i = 0; i < array.Count; i++) if (idOf(array[i]) == id) return i;
        return -1;
    }

    static string join(string path, string key) => path.Length == 0 ? key : path + "." + key;

    static IEnumerable<(string Property, Guid? Id)> parse(string path) {
        foreach (var segment in path.Split('.')) {
            var open = segment.IndexOf('[');
            if (open < 0) {
                yield return (segment, null);
                continue;
            }
            var close = segment.IndexOf(']', open);
            if (close < 0 || !Guid.TryParse(segment[(open + 1)..close], out var id)) {
                yield return (segment, null);
                continue;
            }
            yield return (segment[..open], id);
        }
    }

    /// <summary>A path as a reader would say it: the ids shortened to their first eight characters.</summary>
    public static string Display(string path) {
        var sb = new StringBuilder();
        var i = 0;
        while (i < path.Length) {
            var open = path.IndexOf('[', i);
            if (open < 0) {
                sb.Append(path, i, path.Length - i);
                break;
            }
            var close = path.IndexOf(']', open);
            if (close < 0) {
                sb.Append(path, i, path.Length - i);
                break;
            }
            sb.Append(path, i, open - i + 1);
            var inside = path[(open + 1)..close];
            sb.Append(inside.Length > 8 && Guid.TryParse(inside, out _) ? inside[..8] : inside);
            sb.Append(']');
            i = close + 1;
        }
        return sb.ToString();
    }
}
