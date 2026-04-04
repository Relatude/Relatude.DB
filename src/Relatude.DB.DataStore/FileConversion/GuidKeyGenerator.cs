using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Relatude.DB.Web;

/// <summary>
/// Deterministically generates a Guid based on the structure and values of an object. This can be used to create consistent keys for objects that may not have a natural unique identifier, such as file adjustments. The generated Guid will be the same for objects with the same structure and values, regardless of their reference identity. It handles cycles in object graphs by detecting them and writing a special marker. The hashing is done using SHA256, and the first 16 bytes of the hash are used to create the Guid.
/// </summary>
public static class GuidKeyGenerator {
    private enum ValueType {
        DoubleValue,
        IntegerValue,
        StringValue,
        BooleanValue,
        EnumValue,
        ObjectValue,
    }

    private delegate void MemberWriter(object instance, HashContext ctx);

    private sealed class MemberMetadata {
        public required byte[] NameUtf8 { get; init; }
        public required MemberWriter Writer { get; init; }
    }

    private sealed class TypeMetadata {
        public required Type Type { get; init; }
        public required ValueType ValueType { get; init; }
        public required bool IsArray { get; init; }
        public required byte[] HeaderBytes { get; init; }
        public Type? EnumType { get; init; }
        public Type? ElementType { get; init; }
        public MemberMetadata[]? Members { get; init; }
    }

    // A reusable context that avoids per-call allocations and batches IO
    internal sealed class HashContext {
        public readonly IncrementalHash Hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public readonly HashSet<object> Visited = new(ReferenceEqualityComparer.Instance);
        public readonly byte[] Buffer = new byte[4096];
        public int Position;

        public void EnsureCapacity(int bytes) {
            if (Position + bytes > Buffer.Length) Flush();
        }

        public void Flush() {
            if (Position > 0) {
                Hasher.AppendData(Buffer, 0, Position);
                Position = 0;
            }
        }

        public void WriteByte(byte value) {
            EnsureCapacity(1);
            Buffer[Position++] = value;
        }

        public void WriteBytes(byte[] value) {
            int len = value.Length;
            if (Position + len <= Buffer.Length) {
                value.CopyTo(Buffer.AsSpan(Position));
                Position += len;
            } else {
                Flush();
                if (len <= Buffer.Length) {
                    value.CopyTo(Buffer.AsSpan());
                    Position = len;
                } else {
                    Hasher.AppendData(value);
                }
            }
        }

        public void WriteInt32(int value) {
            EnsureCapacity(4);
            BitConverter.TryWriteBytes(Buffer.AsSpan(Position), value);
            Position += 4;
        }

        public void WriteInt64(long value) {
            EnsureCapacity(8);
            BitConverter.TryWriteBytes(Buffer.AsSpan(Position), value);
            Position += 8;
        }

        public void WriteUInt64(ulong value) {
            EnsureCapacity(8);
            BitConverter.TryWriteBytes(Buffer.AsSpan(Position), value);
            Position += 8;
        }

        public void WriteString(string value) {
            int byteCount = Encoding.UTF8.GetByteCount(value);
            WriteInt32(byteCount);
            if (byteCount == 0) return;

            if (Position + byteCount <= Buffer.Length) {
                Encoding.UTF8.GetBytes(value, Buffer.AsSpan(Position));
                Position += byteCount;
            } else {
                Flush();
                if (byteCount <= Buffer.Length) {
                    Encoding.UTF8.GetBytes(value, Buffer.AsSpan());
                    Position = byteCount;
                } else {
                    var rented = ArrayPool<byte>.Shared.Rent(byteCount);
                    Encoding.UTF8.GetBytes(value, rented);
                    Hasher.AppendData(rented, 0, byteCount);
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }
    }

    [ThreadStatic]
    private static HashContext? _threadContext;
    private static readonly ConcurrentDictionary<Type, TypeMetadata> _typeCache = new();

    /// <summary>
    /// Generates a deterministic Guid based on the structure and values of the provided object. The same object structure and values will always produce the same Guid, making it suitable for scenarios where a consistent identifier is needed for objects without a natural unique key. The method handles cycles in object graphs by detecting them and writing a special marker to ensure the hash remains consistent. The hashing is performed using SHA256, and the first 16 bytes of the resulting hash are used to create the Guid.
    /// </summary>
    /// <param name="o"></param>
    /// <returns></returns>
    public static Guid Generate(object o) {
        ArgumentNullException.ThrowIfNull(o);

        var ctx = _threadContext ??= new HashContext();

        try {
            WriteValueObject(ctx, o, o.GetType());

            ctx.Flush();
            Span<byte> hash = stackalloc byte[32];
            ctx.Hasher.GetHashAndReset(hash);

            Span<byte> guidBytes = stackalloc byte[16];
            hash[..16].CopyTo(guidBytes);
            return new Guid(guidBytes);
        } finally {
            ctx.Visited.Clear();
            ctx.Position = 0;
        }
    }

    // Ultra-fast generic static cache avoids dictionary lookups for strongly-typed properties
    private static class MetadataCache<T> {
        public static readonly TypeMetadata Metadata = GetOrAddMetadata(Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    // Strongly-typed writer avoids boxing value types entirely!
    private static void WritePropertyValue<T>(HashContext ctx, T value) {
        if (value is null) {
            ctx.WriteByte(0);
            return;
        }
        ctx.WriteByte(1);

        var metadata = MetadataCache<T>.Metadata;
        ctx.WriteBytes(metadata.HeaderBytes);

        if (metadata.IsArray) {
            WriteArrayObject(ctx, value, metadata);
            return;
        }

        switch (metadata.ValueType) {
            case ValueType.StringValue: ctx.WriteString((string)(object)value!); break;
            case ValueType.BooleanValue: ctx.WriteByte((bool)(object)value! ? (byte)1 : (byte)0); break;
            case ValueType.IntegerValue: WriteIntegerT(ctx, value); break;
            case ValueType.DoubleValue: WriteFloatingPointT(ctx, value); break;
            case ValueType.EnumValue:
                var enumType = metadata.EnumType!;
                var underlying = Enum.GetUnderlyingType(enumType);
                var enumValue = Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture)!;
                WriteIntegerObj(ctx, enumValue);
                break;
            case ValueType.ObjectValue: WriteObjectInstance(ctx, value, metadata); break;
        }
    }

    private static void WriteValueObject(HashContext ctx, object? value, Type declaredType) {
        if (value is null) {
            ctx.WriteByte(0);
            return;
        }
        ctx.WriteByte(1);

        var runtimeType = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        var metadata = GetOrAddMetadata(runtimeType);

        ctx.WriteBytes(metadata.HeaderBytes);

        if (metadata.IsArray) {
            WriteArrayObject(ctx, value, metadata);
            return;
        }

        switch (metadata.ValueType) {
            case ValueType.StringValue: ctx.WriteString((string)value); break;
            case ValueType.BooleanValue: ctx.WriteByte((bool)value ? (byte)1 : (byte)0); break;
            case ValueType.IntegerValue: WriteIntegerObj(ctx, value); break;
            case ValueType.DoubleValue: WriteFloatingPointObj(ctx, value); break;
            case ValueType.EnumValue:
                var enumType = metadata.EnumType!;
                var underlying = Enum.GetUnderlyingType(enumType);
                var enumValue = Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture)!;
                WriteIntegerObj(ctx, enumValue);
                break;
            case ValueType.ObjectValue: WriteObjectInstance(ctx, value, metadata); break;
        }
    }

    private static void WriteArrayObject(HashContext ctx, object value, TypeMetadata metadata) {
        if (value is not IEnumerable enumerable) {
            throw new InvalidOperationException($"Type '{metadata.Type}' is marked as array but is not enumerable.");
        }

        var elementType = metadata.ElementType ?? typeof(object);
        var count = 0;

        if (value is Array array) {
            count = array.Length;
            ctx.WriteInt32(count);
            for (var i = 0; i < array.Length; i++) {
                WriteValueObject(ctx, array.GetValue(i), elementType);
            }
            return;
        }

        ctx.WriteInt32(-1);
        foreach (var item in enumerable) {
            count++;
            WriteValueObject(ctx, item, elementType);
        }
        ctx.WriteInt32(count);
    }

    private static void WriteObjectInstance(HashContext ctx, object value, TypeMetadata metadata) {
        if (!metadata.Type.IsValueType && !ctx.Visited.Add(value)) {
            ctx.WriteString("#cycle");
            return;
        }

        try {
            var members = metadata.Members ?? Array.Empty<MemberMetadata>();
            ctx.WriteInt32(members.Length);

            foreach (var member in members) {
                ctx.WriteBytes(member.NameUtf8);
                member.Writer(value, ctx);
            }
        } finally {
            if (!metadata.Type.IsValueType) {
                ctx.Visited.Remove(value);
            }
        }
    }

    private static TypeMetadata GetOrAddMetadata(Type type) {
        return _typeCache.GetOrAdd(type, BuildMetadata);
    }

    private static TypeMetadata BuildMetadata(Type type) {
        var normalizedType = Nullable.GetUnderlyingType(type) ?? type;
        var isArray = normalizedType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(normalizedType);
        var elementType = isArray
            ? (normalizedType.IsArray ? normalizedType.GetElementType() : normalizedType.GetGenericArguments().FirstOrDefault()) ?? typeof(object)
            : null;

        var valueType = ResolveValueType(isArray ? (Nullable.GetUnderlyingType(elementType!) ?? elementType!) : normalizedType);

        // Pre-compute the entire header sequence (eliminates reflection & string encoding per object)
        var typeTokenUtf8 = Encoding.UTF8.GetBytes(normalizedType.AssemblyQualifiedName ?? normalizedType.FullName ?? normalizedType.Name);
        var headerBytes = new byte[4 + typeTokenUtf8.Length + 2];
        BitConverter.TryWriteBytes(headerBytes.AsSpan(0, 4), typeTokenUtf8.Length);
        typeTokenUtf8.CopyTo(headerBytes.AsSpan(4));
        headerBytes[^2] = (byte)valueType;
        headerBytes[^1] = isArray ? (byte)1 : (byte)0;

        MemberMetadata[]? members = null;
        if (!isArray && valueType == ValueType.ObjectValue) {
            members = normalizedType
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
                .Cast<MemberInfo>()
                .Concat(normalizedType.GetFields(BindingFlags.Instance | BindingFlags.Public).Where(field => !field.IsStatic))
                .OrderBy(member => member.Name, StringComparer.Ordinal)
                .Select(CreateMemberMetadata)
                .ToArray();
        }

        return new TypeMetadata {
            Type = normalizedType,
            ValueType = valueType,
            IsArray = isArray,
            HeaderBytes = headerBytes,
            EnumType = valueType == ValueType.EnumValue ? normalizedType : null,
            ElementType = elementType,
            Members = members
        };
    }

    private static MemberMetadata CreateMemberMetadata(MemberInfo member) {
        var memberType = member switch {
            PropertyInfo property => property.PropertyType,
            FieldInfo field => field.FieldType,
            _ => throw new NotSupportedException($"Member '{member.Name}' is not supported.")
        };

        return new MemberMetadata {
            NameUtf8 = Encoding.UTF8.GetBytes(member.Name),
            Writer = CreateWriter(member, memberType)
        };
    }

    private static MemberWriter CreateWriter(MemberInfo member, Type memberType) {
        var instanceParam = Expression.Parameter(typeof(object), "instance");
        var ctxParam = Expression.Parameter(typeof(HashContext), "ctx");
        var typedInstance = Expression.Convert(instanceParam, member.DeclaringType!);

        Expression access = member switch {
            PropertyInfo property => Expression.Property(typedInstance, property),
            FieldInfo field => Expression.Field(typedInstance, field),
            _ => throw new NotSupportedException()
        };

        var writeMethod = typeof(GuidKeyGenerator).GetMethod(nameof(WritePropertyValue), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(memberType);

        var call = Expression.Call(writeMethod, ctxParam, access);
        return Expression.Lambda<MemberWriter>(call, instanceParam, ctxParam).Compile();
    }

    private static ValueType ResolveValueType(Type type) {
        if (type == typeof(string)) return ValueType.StringValue;
        if (type == typeof(bool)) return ValueType.BooleanValue;
        if (type.IsEnum) return ValueType.EnumValue;

        if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
            type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong) ||
            type == typeof(nint) || type == typeof(nuint)) {
            return ValueType.IntegerValue;
        }

        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) {
            return ValueType.DoubleValue;
        }

        return ValueType.ObjectValue;
    }

    // Dead-code elimination via the JIT prevents boxing entirely for strictly matched types
    private static void WriteIntegerT<T>(HashContext ctx, T value) {
        if (typeof(T) == typeof(int)) { ctx.WriteInt32((int)(object)value!); return; }
        if (typeof(T) == typeof(long)) { ctx.WriteInt64((long)(object)value!); return; }
        if (typeof(T) == typeof(byte)) { ctx.WriteByte((byte)(object)value!); return; }
        if (typeof(T) == typeof(short)) { ctx.WriteInt32((short)(object)value!); return; }
        if (typeof(T) == typeof(uint)) { ctx.WriteInt32(unchecked((int)(uint)(object)value!)); return; }
        if (typeof(T) == typeof(ulong)) { ctx.WriteUInt64((ulong)(object)value!); return; }
        if (typeof(T) == typeof(ushort)) { ctx.WriteInt32((ushort)(object)value!); return; }
        if (typeof(T) == typeof(sbyte)) { ctx.WriteByte(unchecked((byte)(sbyte)(object)value!)); return; }
        if (typeof(T) == typeof(nint)) { ctx.WriteInt64((nint)(object)value!); return; }
        if (typeof(T) == typeof(nuint)) { ctx.WriteUInt64((nuint)(object)value!); return; }

        WriteIntegerObj(ctx, value!);
    }

    private static void WriteIntegerObj(HashContext ctx, object value) {
        switch (value) {
            case byte v: ctx.WriteByte(v); break;
            case sbyte v: ctx.WriteByte(unchecked((byte)v)); break;
            case short v: ctx.WriteInt32(v); break;
            case ushort v: ctx.WriteInt32(v); break;
            case int v: ctx.WriteInt32(v); break;
            case uint v: ctx.WriteInt32(unchecked((int)v)); break;
            case long v: ctx.WriteInt64(v); break;
            case ulong v: ctx.WriteUInt64(v); break;
            case nint v: ctx.WriteInt64(v); break;
            case nuint v: ctx.WriteUInt64(v); break;
            default: ctx.WriteString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty); break;
        }
    }

    private static void WriteFloatingPointT<T>(HashContext ctx, T value) {
        if (typeof(T) == typeof(double)) { ctx.WriteInt64(BitConverter.DoubleToInt64Bits((double)(object)value!)); return; }
        if (typeof(T) == typeof(float)) { ctx.WriteInt32(BitConverter.SingleToInt32Bits((float)(object)value!)); return; }
        if (typeof(T) == typeof(decimal)) {
            Span<int> bits = stackalloc int[4];
            decimal.GetBits((decimal)(object)value!, bits);
            ctx.WriteInt32(bits[0]); ctx.WriteInt32(bits[1]); ctx.WriteInt32(bits[2]); ctx.WriteInt32(bits[3]);
            return;
        }

        WriteFloatingPointObj(ctx, value!);
    }

    private static void WriteFloatingPointObj(HashContext ctx, object value) {
        switch (value) {
            case float v: ctx.WriteInt32(BitConverter.SingleToInt32Bits(v)); break;
            case double v: ctx.WriteInt64(BitConverter.DoubleToInt64Bits(v)); break;
            case decimal v:
                Span<int> bits = stackalloc int[4];
                decimal.GetBits(v, bits);
                ctx.WriteInt32(bits[0]); ctx.WriteInt32(bits[1]); ctx.WriteInt32(bits[2]); ctx.WriteInt32(bits[3]);
                break;
            default: ctx.WriteString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty); break;
        }
    }
}