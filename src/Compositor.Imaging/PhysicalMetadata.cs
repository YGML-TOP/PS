namespace Compositor.Imaging;

/// <summary>
/// 给 PNG 写入物理分辨率（pHYs 块）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须手写：</b>Mac 版把 DPI 交给 ImageIO 的
/// <c>kCGImagePropertyDPIWidth/Height</c> 属性字典，由系统写进 PNG。
/// SkiaSharp 没有对应的编码选项，而 DPI 是 <c>.comp</c> 互通必须保住的东西——
/// 它决定打开工程时 1 物理单位对应多少像素，丢了会让所有图层缩放关系对不上。
/// 所以这里直接按 PNG 规范插一个 pHYs 块。
/// </para>
/// <para>
/// 规范：pHYs 数据为 9 字节 —— 4 字节每米水平像素数、4 字节每米垂直像素数、
/// 1 字节单位标识（1 = 米）。位置必须在 IDAT 之前，通常紧跟 IHDR。
/// </para>
/// </remarks>
internal static class PhysicalMetadata
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private const string PngSignatureHex = "89504E470D0A1A0A";

    /// <summary>
    /// 把 DPI 写进 PNG 的 pHYs 块。已有则替换，没有则插到 IHDR 之后。
    /// </summary>
    /// <param name="png">完整的 PNG 字节。</param>
    /// <param name="dpi">每英寸像素数。</param>
    /// <returns>带 pHYs 的 PNG 字节；输入不是合法 PNG 时原样返回。</returns>
    public static byte[] ApplyPngDpi(byte[] png, double dpi)
    {
        ArgumentNullException.ThrowIfNull(png);

        if (png.Length < Signature.Length + 12
            || !png.AsSpan(0, Signature.Length).SequenceEqual(Signature))
        {
            // 不是 PNG（或被截断）就原样返回：让上层去报 encode/unreadable，
            // 这里静默跳过比抛异常更合适——DPI 是元数据，不是编码成败。
            return png;
        }

        if (double.IsNaN(dpi) || double.IsInfinity(dpi) || dpi <= 0)
        {
            return png;
        }

        var chunk = BuildPhysChunk(dpi);

        // 扫一遍块列表：已有 pHYs 就原地替换；没有就记下 IHDR 之后的插入点。
        // 注意不能在遇到 IHDR 后就 break —— 第一次调用插进去的 pHYs 正好紧跟 IHDR，
        // 提前 break 会让第二次调用看不见它，于是又插一块，文件越滚越大。
        var offset = Signature.Length;
        var insertAt = -1;

        while (offset + 8 <= png.Length)
        {
            var length = ReadBigEndianInt32(png, offset);
            if (length < 0 || offset + 12L + length > png.Length)
            {
                break;
            }

            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            var dataStart = offset + 8;

            if (type == "pHYs")
            {
                return Splice(png, offset, 12 + length, chunk);
            }

            if (type == "IHDR" && insertAt < 0)
            {
                insertAt = offset + 12 + length;
            }

            offset = dataStart + length + 4;
        }

        if (insertAt < 0)
        {
            return png;
        }

        return Splice(png, insertAt, 0, chunk);
    }

    private static byte[] BuildPhysChunk(double dpi)
    {
        // 每米像素数 = dpi / 0.0254（1 英寸 = 0.0254 米）。
        var perMetre = (int)System.Math.Round(dpi / 0.0254d, MidpointRounding.AwayFromZero);
        if (perMetre < 1)
        {
            perMetre = 1;
        }

        var payload = new byte[9];
        WriteBigEndianInt32(payload, 0, perMetre);
        WriteBigEndianInt32(payload, 4, perMetre);
        payload[8] = 1; // 单位 = 米

        return BuildChunk("pHYs", payload);
    }

    private static byte[] BuildChunk(string type, byte[] payload)
    {
        var chunk = new byte[12 + payload.Length];
        WriteBigEndianInt32(chunk, 0, payload.Length);
        System.Text.Encoding.ASCII.GetBytes(type, 0, 4, chunk, 4);
        payload.CopyTo(chunk, 8);

        // CRC 覆盖「类型 + 数据」，不含长度字段。
        var crc = Crc32.Compute(chunk.AsSpan(4, 4 + payload.Length));
        WriteBigEndianInt32(chunk, 8 + payload.Length, unchecked((int)crc));
        return chunk;
    }

    private static byte[] Splice(byte[] source, int offset, int removeLength, byte[] insert)
    {
        var result = new byte[source.Length - removeLength + insert.Length];
        System.Buffer.BlockCopy(source, 0, result, 0, offset);
        System.Buffer.BlockCopy(insert, 0, result, offset, insert.Length);
        System.Buffer.BlockCopy(source, offset + removeLength, result, offset + insert.Length,
            source.Length - offset - removeLength);
        return result;
    }

    private static int ReadBigEndianInt32(byte[] buffer, int offset) =>
        (buffer[offset] << 24)
        | (buffer[offset + 1] << 16)
        | (buffer[offset + 2] << 8)
        | buffer[offset + 3];

    private static void WriteBigEndianInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)((value >> 24) & 0xFF);
        buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 3] = (byte)(value & 0xFF);
    }

    private static class Crc32
    {
        private const uint Polynomial = 0xEDB88320u;

        private static readonly uint[] Table = BuildTable();

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return crc ^ 0xFFFFFFFFu;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var value = i;
                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? (value >> 1) ^ Polynomial : value >> 1;
                }

                table[i] = value;
            }

            return table;
        }
    }
}