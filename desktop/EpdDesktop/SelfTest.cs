using System.Drawing.Imaging;
using System.Text;

namespace EpdDesktop;

/// <summary>无硬件自检：RLE 编解码 round-trip + 渲染打包断言，产出样例 PNG/BIN。</summary>
internal static class SelfTest
{
    public static string Run()
    {
        var sb = new StringBuilder();
        try
        {
            // ── RLE 固定向量 ──
            CheckRle(sb, new byte[] { 0, 0, 0, 0, 0 }, new byte[] { 0x82, 0x00 }, "重复段向量");
            CheckRle(sb, new byte[] { 1, 2, 3 }, new byte[] { 0x02, 0x01, 0x02, 0x03 }, "字面段向量");
            // 边界：130 长度重复段
            var rep130 = Enumerable.Repeat((byte)0xAB, 130).ToArray();
            CheckRle(sb, rep130, new byte[] { 0xFF, 0xAB }, "130 重复段向量");

            // ── RLE 随机 round-trip + MTU 分块 ──
            var rnd = new Random(42);
            for (int t = 0; t < 3; t++)
            {
                var raw = new byte[10240];
                rnd.NextBytes(raw);
                // 混入重复段，覆盖混合码流
                for (int k = 0; k < 500; k++)
                {
                    int p = rnd.Next(raw.Length - 3);
                    byte v = raw[p];
                    raw[p + 1] = v;
                    raw[p + 2] = v;
                }
                var comp = Rle.Compress(raw);
                var dec = Rle.Decompress(comp, raw.Length);
                if (!dec.SequenceEqual(raw)) throw new Exception($"随机数据 round-trip #{t} 不一致");
                foreach (int maxChunk in new[] { 18, 242, 100 })
                {
                    foreach (var chunk in Rle.CompressChunks(raw, maxChunk))
                    {
                        if (chunk.Length > maxChunk) throw new Exception($"chunk 超长 {chunk.Length} > {maxChunk}");
                        Rle.Decompress(chunk, raw.Length); // 每块可独立解码即视为通过
                    }
                }
            }
            sb.AppendLine("RLE 自检通过");

            // ── 渲染 + 打包 ──
            var rows = BuildSampleRows();
            var bw = RateImageRenderer.Render(400, 300, DateTime.Today, rows);
            int expected = (400 + 7) / 8 * 300; // 15000
            if (bw.Length != expected) throw new Exception($"打包长度 {bw.Length} != 期望 {expected}");
            var binPath = Path.Combine(Path.GetTempPath(), "epd-sample-400x300.bin");
            File.WriteAllBytes(binPath, bw);
            sb.AppendLine($"渲染自检通过: 打包 {bw.Length} 字节 -> {binPath}");

            // 三色双平面：长度一致；样本含上涨变化 → 红平面必须有红像素（0 位）
            var (bw2, red2) = RateImageRenderer.Render2(400, 300, DateTime.Today, rows);
            if (bw2.Length != expected || red2.Length != expected) throw new Exception("三色平面长度错误");
            int redPixels = red2.Count(b => b != 0xFF);
            if (redPixels == 0) throw new Exception("红平面未检出红色像素（上涨数据应渲染为红色）");
            sb.AppendLine($"三色渲染自检通过: 红像素字节数 {redPixels}");

            // Token 用量面板：长度一致；红色标题/分隔线 → 红平面必须有红像素；黑白路径长度一致
            var tUsage = new TokenUsage
            {
                DayTokens = 3_450_000, MonthTokens = 1_560_000_000,
                DayQuota = 35_000_000, MonthQuota = 3_479_000_000,
                BalanceQuota = 6_780_000_000_000, LastLogAt = DateTimeOffset.Now.ToUnixTimeSeconds(),
                FetchedAt = DateTime.Now,
            };
            var (tbw, tred) = TokenImageRenderer.Render2(400, 300, tUsage, DateTime.Now);
            if (tbw.Length != expected || tred.Length != expected) throw new Exception("Token 面板三色平面长度错误");
            int tRedPixels = tred.Count(b => b != 0xFF);
            if (tRedPixels == 0) throw new Exception("Token 面板红平面未检出红色像素（红色标题应渲染为红）");
            var tbwOnly = TokenImageRenderer.Render(400, 300, tUsage, DateTime.Now);
            if (tbwOnly.Length != expected) throw new Exception("Token 面板黑白平面长度错误");
            sb.AppendLine($"Token 面板渲染自检通过: 红像素字节数 {tRedPixels}");

            using (var bmp = RateImageRenderer.RenderBitmap(400, 300, DateTime.Today, rows))
            {
                var pngPath = Path.Combine(Path.GetTempPath(), "epd-sample-400x300.png");
                bmp.Save(pngPath, ImageFormat.Png);
                sb.AppendLine($"样例 PNG -> {pngPath}");
            }

            // 面板尺寸校验：rowH < 28 应抛错（400x200 太矮）
            try
            {
                RateImageRenderer.Render(400, 200, DateTime.Today, rows);
                throw new Exception("矮面板未按预期抛错");
            }
            catch (InvalidOperationException) { /* 预期 */ }
            sb.AppendLine("面板尺寸校验通过");

            sb.Insert(0, "自检通过\n\n");
            return sb.ToString();
        }
        catch (Exception e)
        {
            sb.AppendLine($"自检失败: {e}");
            sb.Insert(0, "自检失败\n\n");
            return sb.ToString();
        }
    }

    private static void CheckRle(StringBuilder sb, byte[] raw, byte[] expected, string label)
    {
        var comp = Rle.Compress(raw);
        if (!comp.SequenceEqual(expected))
            throw new Exception($"{label} 压缩不符: 期望 [{string.Join(",", expected)}], 实际 [{string.Join(",", comp)}]");
        var dec = Rle.Decompress(comp, raw.Length);
        if (!dec.SequenceEqual(raw)) throw new Exception($"{label} round-trip 不一致");
        sb.AppendLine($"  {label}: OK");
    }

    private static List<RateImageRenderer.Row> BuildSampleRows()
    {
        var rnd = new Random(7);
        var rows = new List<RateImageRenderer.Row>();
        var codes = RatesFetcher.Currencies;
        foreach (var code in codes)
        {
            double rate = 0.5 + rnd.NextDouble() * 4.5;
            double? ytd = rnd.NextDouble() < 0.8 ? (rnd.NextDouble() * 12 - 4) : null;
            double? mtd = rnd.NextDouble() < 0.8 ? (rnd.NextDouble() * 8 - 3) : null;
            var spark = new List<double>();
            int n = 20 + rnd.Next(200);
            double v = rate * (0.9 + rnd.NextDouble() * 0.2);
            for (int i = 0; i < n; i++)
            {
                v *= 1 + (rnd.NextDouble() - 0.48) * 0.02;
                spark.Add(v);
            }
            rows.Add(new RateImageRenderer.Row(code, rate, ytd, mtd, spark));
        }
        return rows;
    }
}
