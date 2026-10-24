namespace YuumiX.Core.Common;

/// <summary>
/// 纯托管 PNG 编码器（BGRA 像素 → PNG 字节）。
///
/// 多杀截图在 Core 落地（GDI BitBlt P/Invoke，见 <c>Automation/GdiScreenCapture</c>），
/// 但 Core 的 TFM 是 <c>net10.0</c>（ADR-09）：System.Drawing 的 PNG 编码会引入 Windows 专属
/// 包与 CA1416 告警外溢，而 PNG 格式本身只有 IHDR/IDAT/IEND 三个块——手写约百行纯代码
/// 换来零依赖与可单测（CRC32 与结构断言），是这里的最优解。
///
/// 布局：8 字节签名 + IHDR + IDAT（zlib 封装的 DEFLATE 扫描线）+ IEND。
/// 每行扫描线前置 1 字节过滤类型（固定 0 = None）；输入 BGRA（GDI 位图序），输出 RGBA。
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// 编码 32 位 BGRA 像素为 PNG。
    /// </summary>
    /// <param name="bgraPixels">像素数据，按行排列，每像素 4 字节（B/G/R/A）。</param>
    /// <param name="width">图像宽（像素）。</param>
    /// <param name="height">图像高（像素）。</param>
    /// <param name="stride">输入每行字节数（可大于 width*4，GDI 行对齐时使用）。</param>
    public static byte[] Encode(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (stride < width * 4)
        {
            throw new ArgumentException("stride 小于 width*4", nameof(stride));
        }

        var minRow = Math.Min(width * 4, stride);
        if (bgraPixels.Length < stride * (height - 1) + minRow)
        {
            throw new ArgumentException("像素缓冲区小于 stride*height", nameof(bgraPixels));
        }

        // 扫描线：每行 1 字节过滤头 + RGBA 行数据
        var raw = new byte[height * (1 + width * 4)];
        for (var y = 0; y < height; y++)
        {
            int srcRow = y * stride;
            int dstRow = y * (1 + width * 4);
            raw[dstRow] = 0; // 过滤类型 None
            for (var x = 0; x < width; x++)
            {
                int src = srcRow + x * 4;
                int dst = dstRow + 1 + x * 4;
                raw[dst] = bgraPixels[src + 2];     // R
                raw[dst + 1] = bgraPixels[src + 1]; // G
                raw[dst + 2] = bgraPixels[src];     // B
                raw[dst + 3] = bgraPixels[src + 3]; // A
            }
        }

        using var output = new MemoryStream();
        output.Write(Signature);

        // IHDR：宽、高、位深 8、颜色类型 6（RGBA）、压缩 0、过滤 0、隔行 0
        Span<byte> ihdr = stackalloc byte[13];
        WriteBigEndian(ihdr, 0, width);
        WriteBigEndian(ihdr, 4, height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 6;   // color type RGBA
        ihdr[10] = 0;  // compression
        ihdr[11] = 0;  // filter
        ihdr[12] = 0;  // interlace
        WriteChunk(output, "IHDR"u8, ihdr);

        WriteChunk(output, "IDAT"u8, CompressZlib(raw));
        WriteChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);

        return output.ToArray();
    }

    /// <summary>写一个 PNG 数据块：长度 + 类型 + 数据 + CRC32（类型与数据一起算）。</summary>
    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> lengthBytes = stackalloc byte[4];
        WriteBigEndian(lengthBytes, 0, data.Length);
        output.Write(lengthBytes);

        Span<byte> typeBytes = stackalloc byte[4];
        type.CopyTo(typeBytes);
        output.Write(typeBytes);
        if (data.Length > 0)
        {
            output.Write(data);
        }

        var crcInput = new byte[4 + data.Length];
        type.CopyTo(crcInput);
        data.CopyTo(crcInput.AsSpan(4));
        Span<byte> crcBytes = stackalloc byte[4];
        WriteBigEndian(crcBytes, 0, (int)Crc32(crcInput));
        output.Write(crcBytes);
    }

    /// <summary>zlib 封装：2 字节头 + 原始 DEFLATE + Adler32 校验。</summary>
    private static byte[] CompressZlib(byte[] data)
    {
        using var output = new MemoryStream();
        // CMF=0x78（32K 窗口 + deflate），FLG=0x01 使 (CMF*256+FLG) % 31 == 0 且无预置字典
        output.WriteByte(0x78);
        output.WriteByte(0x01);
        using (var deflate = new System.IO.Compression.DeflateStream(
            output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(data, 0, data.Length);
        }

        uint adler = Adler32(data);
        output.WriteByte((byte)(adler >> 24));
        output.WriteByte((byte)(adler >> 16));
        output.WriteByte((byte)(adler >> 8));
        output.WriteByte((byte)adler);
        return output.ToArray();
    }

    private static void WriteBigEndian(Span<byte> target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    /// <summary>标准 CRC-32（IEEE 802.3，PNG 数据块校验算法）。</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return ~crc;
    }

    /// <summary>Adler-32（zlib 封装的校验算法）。</summary>
    public static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint ModAdler = 65521;
        uint a = 1, b = 0;
        foreach (var t in data)
        {
            a = (a + t) % ModAdler;
            b = (b + a) % ModAdler;
        }

        return (b << 16) | a;
    }
}
