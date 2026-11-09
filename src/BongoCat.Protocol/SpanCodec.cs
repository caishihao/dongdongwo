using System.Buffers.Binary;
using System.Text;

namespace BongoCat.Protocol;

/// <summary>
/// 协议专用 UTF8 编码器：throwOnInvalidBytes=true（严格 schema 纪律——非法字节序列
/// 必须拒绝而不是替换成 U+FFFD，06-multiplayer.md §7"非法即断开"）。
/// </summary>
internal static class StrictUtf8
{
    public static readonly UTF8Encoding Encoding = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
}

/// <summary>
/// Span 顺序写入器（ADR-005 模式：手写二进制编解码，零反射、零 LINQ）。
/// ref struct：不逃逸到堆，全部操作在调用方提供的栈/池缓冲区上完成。
/// 多字节标量统一 little-endian（与 ADR-005 帧头 u32 payloadLen 一致）。
/// </summary>
internal ref struct SpanWriter
{
    private readonly Span<byte> _buffer;
    private int _pos;

    public SpanWriter(Span<byte> buffer)
    {
        _buffer = buffer;
        _pos = 0;
    }

    /// <summary>已写入字节数。</summary>
    public int WrittenBytes => _pos;

    public void WriteU8(byte value)
    {
        _buffer[_pos] = value;
        _pos += 1;
    }

    public void WriteU16(ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer[_pos..], value);
        _pos += 2;
    }

    public void WriteU32(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer[_pos..], value);
        _pos += 4;
    }

    public void WriteI64(long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(_buffer[_pos..], value);
        _pos += 8;
    }

    /// <summary>写入 f64（归一化坐标用 double 保证与存档同精度，不做 float 截断）。</summary>
    public void WriteF64(double value)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(_buffer[_pos..], value);
        _pos += 8;
    }

    /// <summary>
    /// 写入 UTF8 字符串（u8 字节长度前缀 ≤255）。超长返回 false（调用方断言合法输入，
    /// 协议层不静默截断——截断会让两端对"发了什么"产生分歧）。
    /// </summary>
    public bool TryWriteString(string value)
    {
        int byteCount;
        try
        {
            byteCount = StrictUtf8.Encoding.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            return false; // 含代理项孤儿等非法内容
        }

        if (byteCount > byte.MaxValue || _pos + 1 + byteCount > _buffer.Length)
        {
            return false;
        }

        WriteU8((byte)byteCount);
        StrictUtf8.Encoding.GetBytes(value, _buffer[_pos..]);
        _pos += byteCount;
        return true;
    }
}

/// <summary>Span 顺序读取器，与 <see cref="SpanWriter"/> 对偶。全部 Try 模式：失败即视为非法帧。</summary>
internal ref struct SpanReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _pos;

    public SpanReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _pos = 0;
    }

    public bool TryReadU8(out byte value)
    {
        if (_pos + 1 > _buffer.Length)
        {
            value = 0;
            return false;
        }

        value = _buffer[_pos];
        _pos += 1;
        return true;
    }

    public bool TryReadU16(out ushort value)
    {
        if (_pos + 2 > _buffer.Length)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer[_pos..]);
        _pos += 2;
        return true;
    }

    public bool TryReadU32(out uint value)
    {
        if (_pos + 4 > _buffer.Length)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer[_pos..]);
        _pos += 4;
        return true;
    }

    public bool TryReadI64(out long value)
    {
        if (_pos + 8 > _buffer.Length)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadInt64LittleEndian(_buffer[_pos..]);
        _pos += 8;
        return true;
    }

    public bool TryReadF64(out double value)
    {
        if (_pos + 8 > _buffer.Length)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadDoubleLittleEndian(_buffer[_pos..]);
        _pos += 8;
        return true;
    }

    /// <summary>读取 u8 长度前缀 UTF8 字符串。长度超余量或含非法 UTF8 序列 → false。</summary>
    public bool TryReadString(out string value)
    {
        value = string.Empty;
        if (!TryReadU8(out byte length) || _pos + length > _buffer.Length)
        {
            return false;
        }

        ReadOnlySpan<byte> bytes = _buffer.Slice(_pos, length);
        try
        {
            value = StrictUtf8.Encoding.GetString(bytes);
            _pos += length;
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
