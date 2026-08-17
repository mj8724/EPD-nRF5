namespace EpdDesktop;

/// <summary>
/// RLE 压缩/解压，逐字节移植 html/js/rle.js，与固件 EPD_service.c 解码器兼容。
/// 重复段：控制字节 0x80|(len-3) + 1 值字节，len 3..130。
/// 字面段：控制字节 len-1 + len 字面字节，len 1..128。
/// </summary>
public static class Rle
{
    /// <summary>RLE 压缩整段数据（对应 rleCompress）。</summary>
    public static byte[] Compress(byte[] data, int maxLiteralSize = 128)
    {
        var result = new List<byte>();
        int i = 0;
        while (i < data.Length)
        {
            int runLen = 1;
            while (i + runLen < data.Length && runLen < 130 && data[i + runLen] == data[i]) runLen++;

            if (runLen >= 3)
            {
                result.Add((byte)(0x80 | (runLen - 3)));
                result.Add(data[i]);
                i += runLen;
            }
            else
            {
                int literalStart = i;
                int literalLen = 0;
                while (i < data.Length && literalLen < maxLiteralSize)
                {
                    // 后续 3 个连续相同字节 → 结束字面段，交给重复段
                    if (i + 2 < data.Length && data[i] == data[i + 1] && data[i] == data[i + 2]) break;
                    literalLen++;
                    i++;
                }
                if (literalLen == 0)
                {
                    // 防御分支（正常不会走到）：单字节字面段
                    result.Add(0x00);
                    result.Add(data[i++]);
                }
                else
                {
                    result.Add((byte)(literalLen - 1));
                    for (int j = literalStart; j < literalStart + literalLen; j++) result.Add(data[j]);
                }
            }
        }
        return result.ToArray();
    }

    /// <summary>
    /// 压缩后在 RLE 码字边界切分（对应 rleCompressMTU）。
    /// 每块都是完整、可独立解码的 RLE 流，且 ≤ maxChunkSize；码字绝不跨块。
    /// </summary>
    public static List<byte[]> CompressChunks(byte[] data, int maxChunkSize)
    {
        int maxLit = Math.Min(maxChunkSize - 1, 128);
        var input = Compress(data, maxLit);
        var chunks = new List<byte[]>();
        int i = 0, start = 0;
        while (i < input.Length)
        {
            byte control = input[i];
            // 码长：重复 = 2 字节，字面 = 1 + (control+1) 字节
            int codeLen = (control & 0x80) != 0 ? 2 : (control + 2);

            // 加入此码会超限且当前块已有内容 → 落块（超长单码独占一块）
            if (i - start + codeLen > maxChunkSize && i > start)
            {
                chunks.Add(input[start..i]);
                start = i;
            }
            i += codeLen;
        }
        if (i > start) chunks.Add(input[start..i]);
        return chunks;
    }

    /// <summary>解压（对应固件 rle_decompress_from，用于自检）。</summary>
    public static byte[] Decompress(byte[] data, int expectedLength)
    {
        var result = new List<byte>(expectedLength);
        int i = 0;
        while (i < data.Length)
        {
            byte control = data[i++];
            if ((control & 0x80) != 0)
            {
                int count = (control & 0x7F) + 3;
                byte value = data[i++];
                for (int k = 0; k < count; k++) result.Add(value);
            }
            else
            {
                int count = control + 1;
                for (int k = 0; k < count; k++) result.Add(data[i++]);
            }
        }
        return result.ToArray();
    }
}
