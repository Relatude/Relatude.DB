using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.IO.Compression;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

// This interface generates a schema based on the properties of a class
// Property types must match the ValueType enum,
// and for ObjectValue types, the schema must be provided recursively
public interface IUrlSchema {
    ValueSchema[] GetSchema<T>();
}

// This interface generates a compressed URL string based on the provided schema and instance
// The generator will take a Guid in its constructor to use as a key for compression
// The out out does not need to encrypted, but it should be signed with a hash to prevent tampering
// It must be super fast and secure, use cache and support multithreading
public interface IUrlGenerator {
    string GetCompressedUrlString(ValueSchema[] schema, object instance);
}

// This class ties everything together,
// it will use the IUrlSchema to get the schema for a type,
// and then use the IUrlGenerator to generate the compressed URL string
// It too must be super fast and secure, use cache and support multithreading
public interface IUrlSerializer {
    string Compress<T>(T instance);
    T DeCompress<T>(string urlString);
}

public enum ValueType {
    DoubleValue,
    IntegerValue,
    StringValue,
    BooleanValue,
    EnumValue,
    ObjectValue,
}

public class ValueSchema {
    public string PropertyName { get; set; } = string.Empty;
    public ValueType ValueType { get; set; }
    public Type? EnumType { get; set; }
    public ValueSchema[]? SubSchema { get; set; }
    public bool IsNullable { get; set; }
    public bool IsArray { get; set; }
}

public class PropertyValue {
    public string Name { get; set; } = string.Empty;
    public object? Value { get; set; }
}

public sealed class UrlSchema : IUrlSchema {
    public ValueSchema[] GetSchema<T>() {
        return UrlTypeCache.GetOrAdd(typeof(T)).Schema;
    }
}

public sealed class UrlGenerator : IUrlGenerator {
    private readonly byte[] _keyBytes;

    public UrlGenerator(Guid key) {
        _keyBytes = key.ToByteArray();
    }

    public string GetCompressedUrlString(ValueSchema[] schema, object instance) {
        ArgumentNullException.ThrowIfNull(instance);
        return UrlBinaryCodec.Encode(instance, UrlTypeCache.GetOrAdd(instance.GetType()), _keyBytes);
    }
}

public sealed class UrlSerializer : IUrlSerializer {
    private readonly IUrlSchema _schema;
    private readonly IUrlGenerator _generator;
    private readonly byte[] _keyBytes;

    public UrlSerializer(Guid key)
        : this(new UrlSchema(), new UrlGenerator(key), key) {
    }

    public UrlSerializer(IUrlSchema schema, Guid key)
        : this(schema, new UrlGenerator(key), key) {
    }

    public UrlSerializer(IUrlSchema schema, IUrlGenerator generator, Guid key) {
        _schema = schema;
        _generator = generator;
        _keyBytes = key.ToByteArray();
    }

    public string Compress<T>(T instance) {
        return _generator.GetCompressedUrlString(_schema.GetSchema<T>(), instance!);
    }

    public T DeCompress<T>(string urlString) {
        return (T)UrlBinaryCodec.Decode(urlString, UrlTypeCache.GetOrAdd(typeof(T)), _keyBytes)!;
    }
}

internal static class UrlBinaryCodec {
    private const byte FormatVersion = 2;
    private const byte CompressedFlag = 1;
    private const int HeaderLength = 6;
    private const int SignatureLength = 16;

    public static string Encode(object instance, TypeMetadata metadata, byte[] keyBytes) {
        var rawBuffer = new ArrayBufferWriter<byte>(256);
        WriteObject(rawBuffer, instance, metadata, metadata.IsNullable);

        ReadOnlySpan<byte> rawData = rawBuffer.WrittenSpan;
        byte[]? rentedCompressed = null;

        try {
            byte flags = 0;
            ReadOnlySpan<byte> payload = rawData;

            if (rawData.Length > 0) {
                rentedCompressed = ArrayPool<byte>.Shared.Rent(BrotliEncoder.GetMaxCompressedLength(rawData.Length));
                if (BrotliEncoder.TryCompress(rawData, rentedCompressed, out var compressedLength, quality: 1, window: 22) && compressedLength < rawData.Length) {
                    flags = CompressedFlag;
                    payload = rentedCompressed.AsSpan(0, compressedLength);
                }
            }

            var finalBuffer = new byte[HeaderLength + payload.Length + SignatureLength];
            finalBuffer[0] = FormatVersion;
            finalBuffer[1] = flags;
            BinaryPrimitives.WriteInt32LittleEndian(finalBuffer.AsSpan(2, 4), rawData.Length);
            payload.CopyTo(finalBuffer.AsSpan(HeaderLength));

            var signature = HMACSHA256.HashData(keyBytes, finalBuffer.AsSpan(0, HeaderLength + payload.Length));
            signature.AsSpan(0, SignatureLength).CopyTo(finalBuffer.AsSpan(HeaderLength + payload.Length));

            return Base64UrlEncode(finalBuffer);
        } finally {
            if (rentedCompressed is not null) {
                ArrayPool<byte>.Shared.Return(rentedCompressed);
            }
        }
    }

    public static object? Decode(string urlString, TypeMetadata metadata, byte[] keyBytes) {
        ArgumentException.ThrowIfNullOrEmpty(urlString);

        var finalBuffer = Base64UrlDecode(urlString);
        if (finalBuffer.Length < HeaderLength + SignatureLength) {
            throw new InvalidOperationException("The payload is too short.");
        }

        var payloadLength = finalBuffer.Length - SignatureLength;
        var expectedSignature = HMACSHA256.HashData(keyBytes, finalBuffer.AsSpan(0, payloadLength));
        if (!CryptographicOperations.FixedTimeEquals(expectedSignature.AsSpan(0, SignatureLength), finalBuffer.AsSpan(payloadLength, SignatureLength))) {
            throw new InvalidOperationException("The payload signature is invalid.");
        }

        if (finalBuffer[0] != FormatVersion) {
            throw new InvalidOperationException($"Unsupported payload version '{finalBuffer[0]}'.");
        }

        var flags = finalBuffer[1];
        var rawLength = BinaryPrimitives.ReadInt32LittleEndian(finalBuffer.AsSpan(2, 4));
        if (rawLength < 0) {
            throw new InvalidOperationException("The payload length is invalid.");
        }

        var payload = finalBuffer.AsSpan(HeaderLength, payloadLength - HeaderLength);
        byte[]? rentedRaw = null;

        try {
            ReadOnlySpan<byte> rawData;
            if ((flags & CompressedFlag) == CompressedFlag) {
                rentedRaw = ArrayPool<byte>.Shared.Rent(rawLength);
                if (!BrotliDecoder.TryDecompress(payload, rentedRaw, out var bytesWritten) || bytesWritten != rawLength) {
                    throw new InvalidOperationException("The payload could not be decompressed.");
                }

                rawData = rentedRaw.AsSpan(0, rawLength);
            } else {
                if (payload.Length != rawLength) {
                    throw new InvalidOperationException("The payload length does not match the header.");
                }

                rawData = payload;
            }

            var offset = 0;
            var result = ReadObject(rawData, ref offset, metadata, metadata.IsNullable);
            if (offset != rawData.Length) {
                throw new InvalidOperationException("The payload contains unread data.");
            }

            return result;
        } finally {
            if (rentedRaw is not null) {
                ArrayPool<byte>.Shared.Return(rentedRaw);
            }
        }
    }

    private static void WriteObject(ArrayBufferWriter<byte> writer, object? instance, TypeMetadata metadata, bool allowNull) {
        if (allowNull) {
            if (instance is null) {
                WriteByte(writer, 0);
                return;
            }

            WriteByte(writer, 1);
        }

        WriteVarUInt(writer, (ulong)metadata.Members.Length);

        foreach (var member in metadata.Members) {
            WriteUtf8(writer, member.NameUtf8);
            var valueBuffer = new ArrayBufferWriter<byte>(64);
            WriteMemberValue(valueBuffer, member.Getter(instance!), member);
            WriteUInt32(writer, checked((uint)valueBuffer.WrittenCount));
            valueBuffer.WrittenSpan.CopyTo(writer.GetSpan(valueBuffer.WrittenCount));
            writer.Advance(valueBuffer.WrittenCount);
        }
    }

    private static object? ReadObject(ReadOnlySpan<byte> data, ref int offset, TypeMetadata metadata, bool allowNull) {
        if (allowNull && ReadByte(data, ref offset) == 0) {
            return null;
        }

        var instance = metadata.Creator();
        var propertyCount = checked((int)ReadVarUInt(data, ref offset));
        for (var i = 0; i < propertyCount; i++) {
            var propertyName = ReadString(data, ref offset);
            var valueLength = checked((int)ReadUInt32(data, ref offset));
            EnsureAvailable(data, offset, valueLength);

            if (metadata.MemberMap.TryGetValue(propertyName, out var member)) {
                var localOffset = 0;
                var value = ReadMemberValue(data.Slice(offset, valueLength), ref localOffset, member);
                if (localOffset != valueLength) {
                    throw new InvalidOperationException($"The property '{propertyName}' payload is invalid.");
                }

                member.Setter(instance, value);
            }

            offset += valueLength;
        }

        return instance;
    }

    private static void WriteMemberValue(ArrayBufferWriter<byte> writer, object? value, MemberMetadata member) {
        if (member.IsArray) {
            WriteArray(writer, value, member);
            return;
        }

        if (member.IsNullable) {
            if (value is null) {
                WriteByte(writer, 0);
                return;
            }

            WriteByte(writer, 1);
        }

        var targetType = member.RuntimeType;
        switch (member.ValueType) {
            case ValueType.StringValue:
                WriteString(writer, (string)value!);
                break;
            case ValueType.BooleanValue:
                WriteByte(writer, (bool)value! ? (byte)1 : (byte)0);
                break;
            case ValueType.IntegerValue:
                WriteInteger(writer, value!, targetType);
                break;
            case ValueType.DoubleValue:
                WriteFloatingPoint(writer, value!, targetType);
                break;
            case ValueType.EnumValue:
                WriteEnum(writer, value!, member.EnumType!);
                break;
            case ValueType.ObjectValue:
                WriteObject(writer, value, member.ObjectMetadata!, false);
                break;
            default:
                throw new NotSupportedException($"Unsupported member type '{member.ValueType}'.");
        }
    }

    private static object? ReadMemberValue(ReadOnlySpan<byte> data, ref int offset, MemberMetadata member) {
        if (member.IsArray) {
            return ReadArray(data, ref offset, member);
        }

        if (member.IsNullable && ReadByte(data, ref offset) == 0) {
            return null;
        }

        return member.ValueType switch {
            ValueType.StringValue => ReadString(data, ref offset),
            ValueType.BooleanValue => ReadByte(data, ref offset) != 0,
            ValueType.IntegerValue => ReadInteger(data, ref offset, member.RuntimeType),
            ValueType.DoubleValue => ReadFloatingPoint(data, ref offset, member.RuntimeType),
            ValueType.EnumValue => ReadEnum(data, ref offset, member.EnumType!),
            ValueType.ObjectValue => ReadObject(data, ref offset, member.ObjectMetadata!, false),
            _ => throw new NotSupportedException($"Unsupported member type '{member.ValueType}'.")
        };
    }

    private static void WriteArray(ArrayBufferWriter<byte> writer, object? value, MemberMetadata member) {
        if (value is null) {
            WriteByte(writer, 0);
            return;
        }

        WriteByte(writer, 1);
        var array = (Array)value;
        WriteVarUInt(writer, (ulong)array.Length);

        for (var i = 0; i < array.Length; i++) {
            var element = array.GetValue(i);
            if (member.ElementIsNullable) {
                if (element is null) {
                    WriteByte(writer, 0);
                    continue;
                }

                WriteByte(writer, 1);
            }

            WriteArrayElement(writer, element!, member);
        }
    }

    private static object? ReadArray(ReadOnlySpan<byte> data, ref int offset, MemberMetadata member) {
        if (ReadByte(data, ref offset) == 0) {
            return null;
        }

        var length = checked((int)ReadVarUInt(data, ref offset));
        var array = Array.CreateInstance(member.ElementRuntimeType, length);

        for (var i = 0; i < length; i++) {
            if (member.ElementIsNullable && ReadByte(data, ref offset) == 0) {
                array.SetValue(null, i);
                continue;
            }

            array.SetValue(ReadArrayElement(data, ref offset, member), i);
        }

        return array;
    }

    private static void WriteArrayElement(ArrayBufferWriter<byte> writer, object value, MemberMetadata member) {
        switch (member.ValueType) {
            case ValueType.StringValue:
                WriteString(writer, (string)value);
                break;
            case ValueType.BooleanValue:
                WriteByte(writer, (bool)value ? (byte)1 : (byte)0);
                break;
            case ValueType.IntegerValue:
                WriteInteger(writer, value, member.ElementRuntimeType);
                break;
            case ValueType.DoubleValue:
                WriteFloatingPoint(writer, value, member.ElementRuntimeType);
                break;
            case ValueType.EnumValue:
                WriteEnum(writer, value, member.EnumType!);
                break;
            case ValueType.ObjectValue:
                WriteObject(writer, value, member.ObjectMetadata!, false);
                break;
            default:
                throw new NotSupportedException($"Unsupported array element type '{member.ValueType}'.");
        }
    }

    private static object ReadArrayElement(ReadOnlySpan<byte> data, ref int offset, MemberMetadata member) {
        return member.ValueType switch {
            ValueType.StringValue => ReadString(data, ref offset),
            ValueType.BooleanValue => ReadByte(data, ref offset) != 0,
            ValueType.IntegerValue => ReadInteger(data, ref offset, member.ElementRuntimeType),
            ValueType.DoubleValue => ReadFloatingPoint(data, ref offset, member.ElementRuntimeType),
            ValueType.EnumValue => ReadEnum(data, ref offset, member.EnumType!),
            ValueType.ObjectValue => ReadObject(data, ref offset, member.ObjectMetadata!, false)!,
            _ => throw new NotSupportedException($"Unsupported array element type '{member.ValueType}'.")
        };
    }

    private static void WriteString(ArrayBufferWriter<byte> writer, string value) {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        WriteVarUInt(writer, (ulong)byteCount);
        var span = writer.GetSpan(byteCount);
        var written = Encoding.UTF8.GetBytes(value, span);
        writer.Advance(written);
    }

    private static void WriteUtf8(ArrayBufferWriter<byte> writer, ReadOnlySpan<byte> utf8Bytes) {
        WriteVarUInt(writer, (ulong)utf8Bytes.Length);
        utf8Bytes.CopyTo(writer.GetSpan(utf8Bytes.Length));
        writer.Advance(utf8Bytes.Length);
    }

    private static string ReadString(ReadOnlySpan<byte> data, ref int offset) {
        var byteCount = checked((int)ReadVarUInt(data, ref offset));
        EnsureAvailable(data, offset, byteCount);
        var value = Encoding.UTF8.GetString(data.Slice(offset, byteCount));
        offset += byteCount;
        return value;
    }

    private static void WriteInteger(ArrayBufferWriter<byte> writer, object value, Type type) {
        if (TypeTraits.IsUnsignedInteger(type)) {
            WriteVarUInt(writer, Convert.ToUInt64(value, CultureInfo.InvariantCulture));
            return;
        }

        WriteVarUInt(writer, ZigZagEncode(Convert.ToInt64(value, CultureInfo.InvariantCulture)));
    }

    private static object ReadInteger(ReadOnlySpan<byte> data, ref int offset, Type type) {
        var value = ReadVarUInt(data, ref offset);
        if (TypeTraits.IsUnsignedInteger(type)) {
            return TypeTraits.ConvertUnsigned(value, type);
        }

        return TypeTraits.ConvertSigned(ZigZagDecode(value), type);
    }

    private static void WriteFloatingPoint(ArrayBufferWriter<byte> writer, object value, Type type) {
        switch (Type.GetTypeCode(type)) {
            case TypeCode.Single:
                WriteUInt32(writer, BitConverter.SingleToUInt32Bits((float)value));
                break;
            case TypeCode.Double:
                WriteUInt64(writer, BitConverter.DoubleToUInt64Bits((double)value));
                break;
            case TypeCode.Decimal:
                foreach (var part in decimal.GetBits((decimal)value)) {
                    WriteUInt32(writer, unchecked((uint)part));
                }
                break;
            default:
                throw new NotSupportedException($"Floating point type '{type}' is not supported.");
        }
    }

    private static object ReadFloatingPoint(ReadOnlySpan<byte> data, ref int offset, Type type) {
        return Type.GetTypeCode(type) switch {
            TypeCode.Single => BitConverter.UInt32BitsToSingle(ReadUInt32(data, ref offset)),
            TypeCode.Double => BitConverter.UInt64BitsToDouble(ReadUInt64(data, ref offset)),
            TypeCode.Decimal => new decimal(new[]
            {
                unchecked((int)ReadUInt32(data, ref offset)),
                unchecked((int)ReadUInt32(data, ref offset)),
                unchecked((int)ReadUInt32(data, ref offset)),
                unchecked((int)ReadUInt32(data, ref offset))
            }),
            _ => throw new NotSupportedException($"Floating point type '{type}' is not supported.")
        };
    }

    private static void WriteEnum(ArrayBufferWriter<byte> writer, object value, Type enumType) {
        WriteInteger(writer, Convert.ChangeType(value, Enum.GetUnderlyingType(enumType), CultureInfo.InvariantCulture)!, Enum.GetUnderlyingType(enumType));
    }

    private static object ReadEnum(ReadOnlySpan<byte> data, ref int offset, Type enumType) {
        var underlyingType = Enum.GetUnderlyingType(enumType);
        return Enum.ToObject(enumType, ReadInteger(data, ref offset, underlyingType));
    }

    private static void WriteVarUInt(ArrayBufferWriter<byte> writer, ulong value) {
        while (value >= 0x80) {
            WriteByte(writer, (byte)(value | 0x80));
            value >>= 7;
        }

        WriteByte(writer, (byte)value);
    }

    private static ulong ReadVarUInt(ReadOnlySpan<byte> data, ref int offset) {
        ulong result = 0;
        var shift = 0;

        while (true) {
            if (offset >= data.Length || shift >= 70) {
                throw new InvalidOperationException("The variable length integer is invalid.");
            }

            var current = data[offset++];
            result |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0) {
                return result;
            }

            shift += 7;
        }
    }

    private static ulong ZigZagEncode(long value) {
        return (ulong)((value << 1) ^ (value >> 63));
    }

    private static long ZigZagDecode(ulong value) {
        return (long)((value >> 1) ^ (ulong)-(long)(value & 1));
    }

    private static void WriteByte(ArrayBufferWriter<byte> writer, byte value) {
        writer.GetSpan(1)[0] = value;
        writer.Advance(1);
    }

    private static byte ReadByte(ReadOnlySpan<byte> data, ref int offset) {
        EnsureAvailable(data, offset, 1);
        return data[offset++];
    }

    private static void WriteUInt32(ArrayBufferWriter<byte> writer, uint value) {
        var span = writer.GetSpan(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(span, value);
        writer.Advance(sizeof(uint));
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, ref int offset) {
        EnsureAvailable(data, offset, sizeof(uint));
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, sizeof(uint)));
        offset += sizeof(uint);
        return value;
    }

    private static void WriteUInt64(ArrayBufferWriter<byte> writer, ulong value) {
        var span = writer.GetSpan(sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(span, value);
        writer.Advance(sizeof(ulong));
    }

    private static ulong ReadUInt64(ReadOnlySpan<byte> data, ref int offset) {
        EnsureAvailable(data, offset, sizeof(ulong));
        var value = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, sizeof(ulong)));
        offset += sizeof(ulong);
        return value;
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> data, int offset, int count) {
        if ((uint)offset > (uint)data.Length || count < 0 || data.Length - offset < count) {
            throw new InvalidOperationException("The payload ended unexpectedly.");
        }
    }

    private static string Base64UrlEncode(byte[] buffer) {
        return Convert.ToBase64String(buffer).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string value) {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        var padding = base64.Length % 4;
        if (padding != 0) {
            base64 = base64.PadRight(base64.Length + 4 - padding, '=');
        }

        return Convert.FromBase64String(base64);
    }
}

internal static class UrlTypeCache {
    private static readonly ConcurrentDictionary<Type, TypeMetadata> Cache = new();

    public static TypeMetadata GetOrAdd(Type type) {
        return Cache.GetOrAdd(type, static typeToBuild => BuildType(typeToBuild, new HashSet<Type>()));
    }

    private static TypeMetadata BuildType(Type type, HashSet<Type> stack) {
        if (Cache.TryGetValue(type, out var cached)) {
            return cached;
        }

        if (!stack.Add(type)) {
            throw new NotSupportedException($"Recursive type '{type}' is not supported.");
        }

        try {
            if (TypeTraits.ResolveKind(type).ValueType != ValueType.ObjectValue) {
                throw new NotSupportedException($"Top-level type '{type}' must be an object, class, or struct.");
            }

            var members = type
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
                .Cast<MemberInfo>()
                .Concat(type.GetFields(BindingFlags.Instance | BindingFlags.Public).Where(field => !field.IsStatic))
                .OrderBy(member => member.Name, StringComparer.Ordinal)
                .Select(member => BuildMemberMetadata(member, stack))
                .ToArray();

            var metadata = new TypeMetadata(
                type,
                !type.IsValueType || Nullable.GetUnderlyingType(type) is not null,
                CreateFactory(type),
                members,
                members.Select(static member => member.Schema).ToArray(),
                members.ToFrozenDictionary(static member => member.Name, StringComparer.Ordinal));

            Cache.TryAdd(type, metadata);
            return metadata;
        } finally {
            stack.Remove(type);
        }
    }

    private static MemberMetadata BuildMemberMetadata(MemberInfo member, HashSet<Type> stack) {
        var memberType = member switch {
            PropertyInfo property => property.PropertyType,
            FieldInfo field => field.FieldType,
            _ => throw new NotSupportedException($"Member '{member.Name}' is not supported.")
        };

        var isArray = memberType.IsArray && memberType.GetArrayRank() == 1;
        var elementType = isArray ? memberType.GetElementType()! : memberType;
        var isNullable = !memberType.IsValueType || Nullable.GetUnderlyingType(memberType) is not null;
        var elementNullable = !elementType.IsValueType || Nullable.GetUnderlyingType(elementType) is not null;
        var runtimeType = Nullable.GetUnderlyingType(memberType) ?? memberType;
        var elementRuntimeType = Nullable.GetUnderlyingType(elementType) ?? elementType;
        var kind = TypeTraits.ResolveKind(elementRuntimeType);
        var objectMetadata = kind.ValueType == ValueType.ObjectValue ? BuildType(elementRuntimeType, stack) : null;

        var schema = new ValueSchema {
            PropertyName = member.Name,
            ValueType = kind.ValueType,
            EnumType = kind.EnumType,
            SubSchema = objectMetadata?.Schema,
            IsNullable = isNullable,
            IsArray = isArray
        };

        return new MemberMetadata(
            member.Name,
            Encoding.UTF8.GetBytes(member.Name),
            runtimeType,
            elementRuntimeType,
            isNullable,
            isArray,
            elementNullable,
            kind.ValueType,
            kind.EnumType,
            objectMetadata,
            schema,
            CreateGetter(member),
            CreateSetter(member));
    }

    private static Func<object> CreateFactory(Type type) {
        if (type.IsValueType) {
            return () => Activator.CreateInstance(type)!;
        }

        var constructor = type.GetConstructor(Type.EmptyTypes);
        if (constructor is not null) {
            var newExpression = Expression.New(constructor);
            var convertExpression = Expression.Convert(newExpression, typeof(object));
            return Expression.Lambda<Func<object>>(convertExpression).Compile();
        }

        return () => RuntimeHelpers.GetUninitializedObject(type);
    }

    private static Func<object, object?> CreateGetter(MemberInfo member) {
        if (member.DeclaringType?.IsValueType == true) {
            return member switch {
                PropertyInfo property => property.GetValue,
                FieldInfo field => field.GetValue,
                _ => throw new NotSupportedException($"Member '{member.Name}' is not supported.")
            };
        }

        var instance = Expression.Parameter(typeof(object), "instance");
        var typedInstance = Expression.Convert(instance, member.DeclaringType!);
        var access = member switch {
            PropertyInfo property => Expression.Property(typedInstance, property),
            FieldInfo field => Expression.Field(typedInstance, field),
            _ => throw new NotSupportedException($"Member '{member.Name}' is not supported.")
        };

        return Expression.Lambda<Func<object, object?>>(Expression.Convert(access, typeof(object)), instance).Compile();
    }

    private static Action<object, object?> CreateSetter(MemberInfo member) {
        if (member.DeclaringType?.IsValueType == true) {
            return member switch {
                PropertyInfo property => property.SetValue,
                FieldInfo field => field.SetValue,
                _ => throw new NotSupportedException($"Member '{member.Name}' is not supported.")
            };
        }

        var memberType = member switch {
            PropertyInfo property => property.PropertyType,
            FieldInfo field => field.FieldType,
            _ => throw new NotSupportedException($"Member '{member.Name}' is not supported.")
        };

        var instance = Expression.Parameter(typeof(object), "instance");
        var value = Expression.Parameter(typeof(object), "value");
        var typedInstance = Expression.Convert(instance, member.DeclaringType!);
        var typedValue = Expression.Convert(value, memberType);
        var access = member switch {
            PropertyInfo property => Expression.Property(typedInstance, property),
            FieldInfo field => Expression.Field(typedInstance, field),
            _ => throw new NotSupportedException($"Member '{member.Name}' is not supported.")
        };

        return Expression.Lambda<Action<object, object?>>(Expression.Assign(access, typedValue), instance, value).Compile();
    }
}

internal sealed class TypeMetadata {
    public TypeMetadata(Type type, bool isNullable, Func<object> creator, MemberMetadata[] members, ValueSchema[] schema, FrozenDictionary<string, MemberMetadata> memberMap) {
        Type = type;
        IsNullable = isNullable;
        Creator = creator;
        Members = members;
        Schema = schema;
        MemberMap = memberMap;
    }

    public Type Type { get; }
    public bool IsNullable { get; }
    public Func<object> Creator { get; }
    public MemberMetadata[] Members { get; }
    public ValueSchema[] Schema { get; }
    public FrozenDictionary<string, MemberMetadata> MemberMap { get; }
}

internal sealed class MemberMetadata {
    public MemberMetadata(
        string name,
        byte[] nameUtf8,
        Type runtimeType,
        Type elementRuntimeType,
        bool isNullable,
        bool isArray,
        bool elementIsNullable,
        ValueType valueType,
        Type? enumType,
        TypeMetadata? objectMetadata,
        ValueSchema schema,
        Func<object, object?> getter,
        Action<object, object?> setter) {
        Name = name;
        NameUtf8 = nameUtf8;
        RuntimeType = runtimeType;
        ElementRuntimeType = elementRuntimeType;
        IsNullable = isNullable;
        IsArray = isArray;
        ElementIsNullable = elementIsNullable;
        ValueType = valueType;
        EnumType = enumType;
        ObjectMetadata = objectMetadata;
        Schema = schema;
        Getter = getter;
        Setter = setter;
    }

    public string Name { get; }
    public byte[] NameUtf8 { get; }
    public Type RuntimeType { get; }
    public Type ElementRuntimeType { get; }
    public bool IsNullable { get; }
    public bool IsArray { get; }
    public bool ElementIsNullable { get; }
    public ValueType ValueType { get; }
    public Type? EnumType { get; }
    public TypeMetadata? ObjectMetadata { get; }
    public ValueSchema Schema { get; }
    public Func<object, object?> Getter { get; }
    public Action<object, object?> Setter { get; }
}

internal static class TypeTraits {
    public static (ValueType ValueType, Type? EnumType) ResolveKind(Type type) {
        if (type == typeof(string)) {
            return (ValueType.StringValue, null);
        }

        if (type == typeof(bool)) {
            return (ValueType.BooleanValue, null);
        }

        if (type.IsEnum) {
            return (ValueType.EnumValue, type);
        }

        if (IsInteger(type)) {
            return (ValueType.IntegerValue, null);
        }

        if (IsFloatingPoint(type)) {
            return (ValueType.DoubleValue, null);
        }

        if (type == typeof(object)) {
            throw new NotSupportedException("System.Object members are not supported because their runtime shape is unknown.");
        }

        return (ValueType.ObjectValue, null);
    }

    public static bool IsUnsignedInteger(Type type) {
        return type == typeof(byte)
            || type == typeof(ushort)
            || type == typeof(uint)
            || type == typeof(ulong)
            || type == typeof(nuint);
    }

    public static object ConvertUnsigned(ulong value, Type type) {
        if (type == typeof(byte)) return checked((byte)value);
        if (type == typeof(ushort)) return checked((ushort)value);
        if (type == typeof(uint)) return checked((uint)value);
        if (type == typeof(ulong)) return value;
        if (type == typeof(nuint)) return checked((nuint)value);
        throw new NotSupportedException($"Unsigned integer type '{type}' is not supported.");
    }

    public static object ConvertSigned(long value, Type type) {
        if (type == typeof(sbyte)) return checked((sbyte)value);
        if (type == typeof(short)) return checked((short)value);
        if (type == typeof(int)) return checked((int)value);
        if (type == typeof(long)) return value;
        if (type == typeof(nint)) return checked((nint)value);
        throw new NotSupportedException($"Signed integer type '{type}' is not supported.");
    }

    private static bool IsInteger(Type type) {
        return type == typeof(byte)
            || type == typeof(sbyte)
            || type == typeof(short)
            || type == typeof(ushort)
            || type == typeof(int)
            || type == typeof(uint)
            || type == typeof(long)
            || type == typeof(ulong)
            || type == typeof(nint)
            || type == typeof(nuint);
    }

    private static bool IsFloatingPoint(Type type) {
        return type == typeof(float)
            || type == typeof(double)
            || type == typeof(decimal);
    }
}

