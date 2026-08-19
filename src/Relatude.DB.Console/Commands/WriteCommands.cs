using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Nodes;

namespace Relatude.DB.Cli.Commands;

/// <summary>
/// The two write operations that are useful without writing code: seeding nodes from JSON and deleting
/// nodes by id. Scalar members only - relations, references, files and embedded values need the typed API,
/// where the compiler can check what is being related to what.
/// </summary>
public static class WriteCommands {

    public static async Task<int> InsertAsync(CommandArgs args) {
        args.Accept([.. Target.Options, .. ModelSource.Options, .. StoreHost.Options, "type", "file", "flush"]);
        var typeName = args.Require("type");
        var json = args.Get("file") is string file
            ? File.ReadAllText(Path.GetFullPath(file, Directory.GetCurrentDirectory()))
            : args.SinglePositional("json") ?? throw new UsageException("No JSON given, and no --file.");
        JsonDocument document;
        try {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        } catch (JsonException err) {
            throw new UsageException("The JSON could not be read: " + err.Message);
        }
        using var _ = document;
        var objects = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToArray()
            : [document.RootElement];
        foreach (var o in objects) {
            if (o.ValueKind != JsonValueKind.Object) throw new UsageException("Expected a JSON object, or an array of them.");
        }
        var target = Target.Resolve(args);
        using var host = await StoreHost.OpenAsync(args, target);
        var dm = host.Datamodel;
        var nodeType = resolveNodeType(dm, typeName);
        if (nodeType.IsInnerNode) throw new CliException(nodeType.CodeName + " is an embedded type: it only exists inside another node.");
        var clrType = findClrType(dm, nodeType);

        var transaction = host.Store.CreateTransaction();
        var ids = new List<Guid>();
        var index = 0;
        foreach (var element in objects) {
            var node = create(host.Store, clrType);
            foreach (var member in element.EnumerateObject()) {
                try {
                    setMember(node, nodeType, member, dm);
                } catch (Exception err) when (err is not CliException) {
                    throw new CliException($"Object {index}, member \"{member.Name}\": {err.Message}", err);
                }
            }
            transaction.Insert(node, out var id);
            ids.Add(id);
            index++;
        }
        try {
            transaction.Execute(args.Flag("flush"));
        } catch (Exception err) {
            throw new CliException("The transaction was rejected: " + err.Message, err);
        }
        if (args.Flag("json")) Output.Json(new { Inserted = ids.Count, Type = nodeType.FullName, Ids = ids });
        else {
            Output.WriteLine("Inserted " + ids.Count + " " + nodeType.CodeName + " node(s):");
            foreach (var id in ids) Output.WriteLine("  " + id);
        }
        return 0;
    }

    public static async Task<int> DeleteAsync(CommandArgs args) {
        args.Accept([.. Target.Options, .. ModelSource.Options, .. StoreHost.Options, "id", "yes", "flush"]);
        var given = args.GetAll("id");
        if (given.Length == 0) throw new UsageException("No --id given.");
        if (!args.Flag("yes")) throw new CliException("Deleting cannot be undone. Pass --yes to go ahead.");
        var target = Target.Resolve(args);
        using var host = await StoreHost.OpenAsync(args, target);
        var transaction = host.Store.CreateTransaction();
        var deleted = new List<string>();
        foreach (var id in given) {
            if (Guid.TryParse(id, out var guid)) {
                if (!host.Store.Datastore.Exists(guid)) throw new CliException("No node with id " + guid);
                transaction.Delete(guid);
            } else if (int.TryParse(id, out var intId)) {
                if (!host.Store.Datastore.Exists(intId)) throw new CliException("No node with internal id " + intId);
                transaction.Delete(intId);
            } else {
                throw new UsageException("--id takes a guid or an internal integer id, not \"" + id + "\".");
            }
            deleted.Add(id);
        }
        try {
            transaction.Execute(args.Flag("flush"));
        } catch (Exception err) {
            throw new CliException("The transaction was rejected: " + err.Message, err);
        }
        if (args.Flag("json")) Output.Json(new { Deleted = deleted.Count, Ids = deleted });
        else Output.WriteLine("Deleted " + deleted.Count + " node(s).");
        return 0;
    }

    static NodeTypeModel resolveNodeType(Datamodel dm, string name) {
        if (dm.NodeTypesByFullName.TryGetValue(name, out var byFullName)) return byFullName;
        if (dm.NodeTypesByShortName.TryGetValue(name, out var byShortName)) {
            if (byShortName.Length == 1) return byShortName[0];
            throw new CliException("More than one type is named \"" + name + "\": "
                + string.Join(", ", byShortName.Select(t => t.FullName)) + ". Use the full name.");
        }
        var known = dm.NodeTypes.Values.Where(t => !ModelSource.IsNative(t)).Select(t => t.CodeName).Order();
        throw new CliException("No node type named \"" + name + "\". Known types: "
            + (known.Any() ? string.Join(", ", known) : "(none)"));
    }
    static Type findClrType(Datamodel dm, NodeTypeModel nodeType) {
        foreach (var assembly in dm.Assemblies) {
            var type = assembly.GetType(nodeType.FullName, false, false);
            if (type != null) return type;
        }
        throw new CliException("The type " + nodeType.FullName + " is in the datamodel but its assembly is not loaded here. "
            + "Name the model assembly with --assembly or its folder with --bin.");
    }
    /// <summary>
    /// A new node instance. For an interface model this is the implementation generated at start up, which
    /// is why the mapper has to make it rather than Activator.
    /// </summary>
    static object create(NodeStore store, Type clrType) {
        // NewObjectFromType has non-generic overloads too, so GetMethod(name) alone is ambiguous
        var method = typeof(NodeMapper).GetMethods()
            .First(m => m.Name == nameof(NodeMapper.NewObjectFromType) && m.IsGenericMethodDefinition)
            .MakeGenericMethod(clrType);
        try {
            return method.Invoke(store.Mapper, [null])!;
        } catch (TargetInvocationException err) {
            throw new CliException("Could not create a " + clrType.Name + ": " + (err.InnerException?.Message ?? err.Message), err);
        }
    }

    static void setMember(object node, NodeTypeModel nodeType, JsonProperty member, Datamodel dm) {
        var property = node.GetType().GetProperty(member.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
            ?? throw new CliException("Object of type " + nodeType.CodeName + " has no member \"" + member.Name + "\". Members: "
                + string.Join(", ", nodeType.AllProperties.Values.Where(p => !p.Internal).Select(p => p.CodeName).Order()));
        if (nodeType.AllPropertiesByName.TryGetValue(member.Name, out var model)) {
            var unsupported = model.PropertyType switch {
                PropertyType.Relation => "a relation",
                PropertyType.Reference or PropertyType.References => "a reference",
                PropertyType.Embedded => "an embedded value",
                PropertyType.File => "a file",
                _ => null,
            };
            if (unsupported != null) {
                throw new CliException(nodeType.CodeName + "." + model.CodeName + " is " + unsupported
                    + ", which this command cannot set. Insert the node without it, then use the query API from code.");
            }
        }
        if (!property.CanWrite) throw new CliException("\"" + member.Name + "\" cannot be written.");
        property.SetValue(node, convert(member.Value, property.PropertyType, member.Name));
    }

    /// <summary>JSON to the CLR type of the member, with an error that names the member when it does not fit.</summary>
    static object? convert(JsonElement value, Type type, string name) {
        if (value.ValueKind == JsonValueKind.Null) {
            if (type.IsValueType) throw new CliException("\"" + name + "\" is a " + type.Name + " and cannot be null.");
            return null;
        }
        if (type == typeof(GeoCoordinate)) return geo(value, name);
        if (type.IsArray) {
            if (value.ValueKind != JsonValueKind.Array) throw new CliException("\"" + name + "\" needs an array.");
            var elementType = type.GetElementType()!;
            var items = value.EnumerateArray().ToArray();
            var array = Array.CreateInstance(elementType, items.Length);
            for (var i = 0; i < items.Length; i++) array.SetValue(convert(items[i], elementType, name + "[" + i + "]"), i);
            return array;
        }
        if (type.IsEnum) {
            if (value.ValueKind == JsonValueKind.Number) return Enum.ToObject(type, value.GetInt64());
            var text = value.GetString() ?? string.Empty;
            if (Enum.TryParse(type, text, true, out var parsed)) return parsed;
            throw new CliException("\"" + name + "\": " + type.Name + " has no value \"" + text + "\". Values: "
                + string.Join(", ", Enum.GetNames(type)));
        }
        try {
            if (type == typeof(string)) return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            if (type == typeof(bool)) return value.ValueKind == JsonValueKind.String ? bool.Parse(value.GetString()!) : value.GetBoolean();
            if (type == typeof(int)) return number(value, s => int.Parse(s, CultureInfo.InvariantCulture), v => v.GetInt32());
            if (type == typeof(long)) return number(value, s => long.Parse(s, CultureInfo.InvariantCulture), v => v.GetInt64());
            if (type == typeof(double)) return number(value, s => double.Parse(s, CultureInfo.InvariantCulture), v => v.GetDouble());
            if (type == typeof(float)) return number(value, s => float.Parse(s, CultureInfo.InvariantCulture), v => v.GetSingle());
            if (type == typeof(decimal)) return number(value, s => decimal.Parse(s, CultureInfo.InvariantCulture), v => v.GetDecimal());
            if (type == typeof(byte)) return number(value, s => byte.Parse(s, CultureInfo.InvariantCulture), v => v.GetByte());
            if (type == typeof(Guid)) return value.ValueKind == JsonValueKind.String ? Guid.Parse(value.GetString()!) : value.GetGuid();
            if (type == typeof(DateTime)) return DateTime.Parse(value.GetString() ?? value.ToString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            if (type == typeof(DateTimeOffset)) return DateTimeOffset.Parse(value.GetString() ?? value.ToString(), CultureInfo.InvariantCulture);
            if (type == typeof(TimeSpan)) return TimeSpan.Parse(value.GetString() ?? value.ToString(), CultureInfo.InvariantCulture);
        } catch (Exception err) when (err is FormatException or InvalidOperationException or OverflowException or ArgumentException) {
            throw new CliException("\"" + name + "\": " + value.ToString() + " is not a valid " + type.Name + ".");
        }
        throw new CliException("\"" + name + "\" is of type " + type.Name + ", which this command cannot set. "
            + "Use the typed query API from code for it.");
    }
    static object number(JsonElement value, Func<string, object> fromText, Func<JsonElement, object> fromNumber)
        => value.ValueKind == JsonValueKind.String ? fromText(value.GetString()!) : fromNumber(value);
    static GeoCoordinate geo(JsonElement value, string name) {
        if (value.ValueKind == JsonValueKind.Array) {
            var parts = value.EnumerateArray().ToArray();
            if (parts.Length != 2) throw new CliException("\"" + name + "\" takes [latitude, longitude].");
            return new GeoCoordinate(parts[0].GetDouble(), parts[1].GetDouble());
        }
        if (value.ValueKind == JsonValueKind.Object) {
            double read(string a, string b) {
                foreach (var p in value.EnumerateObject()) {
                    if (string.Equals(p.Name, a, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(p.Name, b, StringComparison.OrdinalIgnoreCase)) return p.Value.GetDouble();
                }
                throw new CliException("\"" + name + "\" is missing " + a + ".");
            }
            return new GeoCoordinate(read("latitude", "lat"), read("longitude", "lon"));
        }
        throw new CliException("\"" + name + "\" takes [latitude, longitude] or { latitude, longitude }.");
    }
}
