using System.Text;

namespace PrintBridge.Ipp;

/// <summary>
/// IPP binary protocol tags, operation codes, and status codes (RFC 8010 & RFC 8011).
/// </summary>
public static class IppEncoding
{
    // Delimiter tags
    public const byte TagOperationAttributes = 0x01;
    public const byte TagJobAttributes       = 0x02;
    public const byte TagEndAttributes       = 0x03;
    public const byte TagPrinterAttributes   = 0x04;
    public const byte TagUnsupported         = 0x05;

    // Value tags
    public const byte TagInteger             = 0x21;
    public const byte TagBoolean             = 0x22;
    public const byte TagEnum                = 0x23;
    public const byte TagOctetString         = 0x30;
    public const byte TagDateTime            = 0x31;
    public const byte TagResolution          = 0x32;
    public const byte TagRangeOfInteger      = 0x33;
    public const byte TagTextWithoutLang     = 0x41;
    public const byte TagNameWithoutLang     = 0x42;
    public const byte TagKeyword             = 0x44;
    public const byte TagUri                 = 0x45;
    public const byte TagCharset             = 0x47;
    public const byte TagNaturalLanguage     = 0x48;
    public const byte TagMimeMediaType       = 0x49;

    // Operations
    public const ushort OpPrintJob             = 0x0002;
    public const ushort OpValidateJob          = 0x0004;
    public const ushort OpCreateJob            = 0x0005;
    public const ushort OpSendDocument         = 0x0006;
    public const ushort OpCancelJob            = 0x0008;
    public const ushort OpGetJobAttributes     = 0x0009;
    public const ushort OpGetJobs              = 0x000A;
    public const ushort OpGetPrinterAttributes = 0x000B;

    // Status Codes
    public const ushort StatusOk               = 0x0000;
    public const ushort StatusClientBadRequest = 0x0400;
    public const ushort StatusClientNotFound   = 0x0406;
    public const ushort StatusServerError      = 0x0500;
}

public readonly struct IppValue
{
    public byte Tag { get; }
    public byte[] RawBytes { get; }

    public IppValue(byte tag, byte[] raw)
    {
        Tag = tag;
        RawBytes = raw;
    }

    public string StringValue => Encoding.UTF8.GetString(RawBytes);

    public int IntValue =>
        RawBytes.Length >= 4
            ? (RawBytes[0] << 24) | (RawBytes[1] << 16) | (RawBytes[2] << 8) | RawBytes[3]
            : 0;

    public bool BoolValue => RawBytes.Length > 0 && RawBytes[0] != 0;
}

/// <summary>
/// Parser for binary IPP requests from iOS/Android clients.
/// </summary>
public sealed class IppRequest
{
    public byte VersionMajor { get; set; }
    public byte VersionMinor { get; set; }
    public ushort OperationId { get; set; }
    public int RequestId { get; set; }
    public Dictionary<string, List<IppValue>> OperationAttributes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<IppValue>> JobAttributes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[]? DocumentData { get; set; }

    public string? GetOperationAttribute(string name)
    {
        if (OperationAttributes.TryGetValue(name, out var list) && list.Count > 0)
            return list[0].StringValue;
        return null;
    }

    public int? GetOperationInt(string name)
    {
        if (OperationAttributes.TryGetValue(name, out var list) && list.Count > 0)
        {
            var value = list[0];
            if (value.RawBytes.Length == 4)
                return value.IntValue;
            if (value.RawBytes.Length == 1)
                return value.RawBytes[0];
            if (int.TryParse(value.StringValue, out var parsed))
                return parsed;
        }

        return null;
    }

    public bool? GetOperationBool(string name)
    {
        if (OperationAttributes.TryGetValue(name, out var list) && list.Count > 0)
        {
            var value = list[0];
            if (value.RawBytes.Length == 1)
                return value.RawBytes[0] != 0;
            if (bool.TryParse(value.StringValue, out var parsed))
                return parsed;
            return value.BoolValue;
        }

        return null;
    }

    public static IppRequest Parse(byte[] data)
    {
        var req = new IppRequest();
        if (data.Length < 8)
            return req;

        int pos = 0;
        req.VersionMajor = data[pos++];
        req.VersionMinor = data[pos++];
        req.OperationId = (ushort)((data[pos++] << 8) | data[pos++]);
        req.RequestId = (data[pos++] << 24) | (data[pos++] << 16) | (data[pos++] << 8) | data[pos++];

        Dictionary<string, List<IppValue>>? currentGroup = null;
        string? lastName = null;

        while (pos < data.Length)
        {
            byte tag = data[pos++];

            // Delimiter Tag
            if (tag <= 0x0F)
            {
                if (tag == IppEncoding.TagEndAttributes)
                {
                    if (pos < data.Length)
                    {
                        req.DocumentData = new byte[data.Length - pos];
                        Array.Copy(data, pos, req.DocumentData, 0, req.DocumentData.Length);
                    }
                    break;
                }

                currentGroup = tag switch
                {
                    IppEncoding.TagOperationAttributes => req.OperationAttributes,
                    IppEncoding.TagJobAttributes => req.JobAttributes,
                    _ => null
                };
                lastName = null;
                continue;
            }

            if (pos + 2 > data.Length) break;
            ushort nameLen = (ushort)((data[pos++] << 8) | data[pos++]);
            string name = nameLen > 0
                ? Encoding.UTF8.GetString(data, pos, nameLen)
                : (lastName ?? string.Empty);
            pos += nameLen;

            if (pos + 2 > data.Length) break;
            ushort valLen = (ushort)((data[pos++] << 8) | data[pos++]);
            byte[] valBytes = new byte[valLen];
            if (valLen > 0 && pos + valLen <= data.Length)
            {
                Array.Copy(data, pos, valBytes, 0, valLen);
            }
            pos += valLen;

            var val = new IppValue(tag, valBytes);
            if (currentGroup != null)
            {
                if (!currentGroup.ContainsKey(name))
                    currentGroup[name] = new List<IppValue>();
                currentGroup[name].Add(val);
            }

            if (nameLen > 0) lastName = name;
        }

        return req;
    }
}

/// <summary>
/// Fluent binary serializer for IPP responses.
/// </summary>
public sealed class IppResponseBuilder
{
    private readonly MemoryStream _ms = new();

    public IppResponseBuilder(byte versionMajor, byte versionMinor, ushort statusCode, int requestId)
    {
        _ms.WriteByte(versionMajor);
        _ms.WriteByte(versionMinor);
        WriteUInt16BE(statusCode);
        WriteInt32BE(requestId);
    }

    public IppResponseBuilder BeginOperationAttributes()
    {
        _ms.WriteByte(IppEncoding.TagOperationAttributes);
        return this;
    }

    public IppResponseBuilder BeginPrinterAttributes()
    {
        _ms.WriteByte(IppEncoding.TagPrinterAttributes);
        return this;
    }

    public IppResponseBuilder BeginJobAttributes()
    {
        _ms.WriteByte(IppEncoding.TagJobAttributes);
        return this;
    }

    public IppResponseBuilder AddCharset(string name, string value) =>
        AddString(IppEncoding.TagCharset, name, value);

    public IppResponseBuilder AddNaturalLanguage(string name, string value) =>
        AddString(IppEncoding.TagNaturalLanguage, name, value);

    public IppResponseBuilder AddKeyword(string name, string value) =>
        AddString(IppEncoding.TagKeyword, name, value);

    public IppResponseBuilder AddKeywordValue(string value) =>
        AddContinuationString(IppEncoding.TagKeyword, value);

    public IppResponseBuilder AddMimeMediaType(string name, string value) =>
        AddString(IppEncoding.TagMimeMediaType, name, value);

    public IppResponseBuilder AddMimeMediaTypeValue(string value) =>
        AddContinuationString(IppEncoding.TagMimeMediaType, value);

    public IppResponseBuilder AddUri(string name, string value) =>
        AddString(IppEncoding.TagUri, name, value);

    public IppResponseBuilder AddText(string name, string value) =>
        AddString(IppEncoding.TagTextWithoutLang, name, value);

    public IppResponseBuilder AddTextValue(string value) =>
        AddContinuationString(IppEncoding.TagTextWithoutLang, value);

    public IppResponseBuilder AddName(string name, string value) =>
        AddString(IppEncoding.TagNameWithoutLang, name, value);

    public IppResponseBuilder AddNameValue(string value) =>
        AddContinuationString(IppEncoding.TagNameWithoutLang, value);

    public IppResponseBuilder AddInteger(string name, int value)
    {
        _ms.WriteByte(IppEncoding.TagInteger);
        WriteString(name);
        WriteUInt16BE(4);
        WriteInt32BE(value);
        return this;
    }

    public IppResponseBuilder AddIntegerValue(int value)
    {
        _ms.WriteByte(IppEncoding.TagInteger);
        WriteUInt16BE(0); // continuation
        WriteUInt16BE(4);
        WriteInt32BE(value);
        return this;
    }

    public IppResponseBuilder AddEnum(string name, int value)
    {
        _ms.WriteByte(IppEncoding.TagEnum);
        WriteString(name);
        WriteUInt16BE(4);
        WriteInt32BE(value);
        return this;
    }

    public IppResponseBuilder AddEnumValue(int value)
    {
        _ms.WriteByte(IppEncoding.TagEnum);
        WriteUInt16BE(0); // continuation
        WriteUInt16BE(4);
        WriteInt32BE(value);
        return this;
    }

    public IppResponseBuilder AddBoolean(string name, bool value)
    {
        _ms.WriteByte(IppEncoding.TagBoolean);
        WriteString(name);
        WriteUInt16BE(1);
        _ms.WriteByte(value ? (byte)1 : (byte)0);
        return this;
    }

    public IppResponseBuilder AddRangeOfInteger(string name, int lower, int upper)
    {
        _ms.WriteByte(IppEncoding.TagRangeOfInteger);
        WriteString(name);
        WriteUInt16BE(8);
        WriteInt32BE(lower);
        WriteInt32BE(upper);
        return this;
    }

    public byte[] Build()
    {
        _ms.WriteByte(IppEncoding.TagEndAttributes);
        return _ms.ToArray();
    }

    private IppResponseBuilder AddString(byte tag, string name, string value)
    {
        _ms.WriteByte(tag);
        WriteString(name);
        WriteString(value);
        return this;
    }

    private IppResponseBuilder AddContinuationString(byte tag, string value)
    {
        _ms.WriteByte(tag);
        WriteUInt16BE(0); // name-length 0 indicates continuation attribute
        WriteString(value);
        return this;
    }

    private void WriteString(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        WriteUInt16BE((ushort)bytes.Length);
        _ms.Write(bytes);
    }

    private void WriteUInt16BE(ushort v)
    {
        _ms.WriteByte((byte)(v >> 8));
        _ms.WriteByte((byte)(v & 0xFF));
    }

    private void WriteInt32BE(int v)
    {
        _ms.WriteByte((byte)((v >> 24) & 0xFF));
        _ms.WriteByte((byte)((v >> 16) & 0xFF));
        _ms.WriteByte((byte)((v >> 8) & 0xFF));
        _ms.WriteByte((byte)(v & 0xFF));
    }
}
