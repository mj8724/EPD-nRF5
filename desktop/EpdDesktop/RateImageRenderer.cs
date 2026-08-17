using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;

namespace EpdDesktop;

/// <summary>
/// 汇率图渲染与 1bpp 打包。
/// 布局逐像素对照 quotes.js _renderEpdImage / _drawEpdSparkline；
/// 打包对照 dithering.js processImageData (blackWhiteColor)：MSB-first、灰度 = 0.299r+0.587g+0.114b、≥140 → 位1（白）。
/// </summary>
public static class RateImageRenderer
{
    public sealed record Row(string Code, double? Rate, double? Ytd, double? Mtd, IReadOnlyList<double> Spark);

    /// <summary>渲染并打包为 1bpp（bit=1 白）。</summary>
    public static byte[] Render(int width, int height, DateTime date, IReadOnlyList<Row> rows)
    {
        using var bmp = RenderBitmap(width, height, date, rows);
        return Pack1bpp(bmp, width, height);
    }

    /// <summary>渲染并打包为黑白+红色双平面（三色屏）。</summary>
    public static (byte[] bw, byte[] red) Render2(int width, int height, DateTime date, IReadOnlyList<Row> rows)
    {
        using var bmp = RenderBitmap(width, height, date, rows);
        return Pack1bpp2(bmp, width, height);
    }

    /// <summary>绘制 24bpp 位图（供自检输出 PNG 用）。</summary>
    public static Bitmap RenderBitmap(int width, int height, DateTime date, IReadOnlyList<Row> rows)
    {
        if (rows.Count != 8) throw new ArgumentException("需要 8 行汇率数据", nameof(rows));
        int rowH = (height - 28) / 8; // baseY=24 + 4（对应 quotes.js）
        if (rowH < 28) throw new InvalidOperationException("面板太矮，无法显示 8 行汇率");

        var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.Clear(Color.White);

            // 字体单位必须为像素（默认 Point 会放大 33%）。
            // 字体族用 Segoe UI 与 web 的 sans-serif 一致（Consolas 等宽视觉偏小）。
            using var titleFont = new Font("Segoe UI", 18f, FontStyle.Bold, GraphicsUnit.Pixel);
            var title = $"汇率 CNY · {date.ToString("yyyy/M/d", CultureInfo.InvariantCulture)}";
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near })
            {
                g.DrawString(title, titleFont, Brushes.Black, new RectangleF(0, 0, width, 20), sf);
            }

            // 分隔线
            g.FillRectangle(Brushes.Black, 4, 22, width - 8, 2);

            using var mainFont = new Font("Segoe UI", 18f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var pctFont = new Font("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.Pixel); // Bold：黑色百分比不显淡
            using var redBrush = new SolidBrush(Color.FromArgb(0xFF, 0x00, 0x00)); // 涨=红（与 web 一致）
            using var blackBrush = new SolidBrush(Color.Black);

            const int colCode = 4;
            const int colRate = 51;
            const int colYoy = 116;
            const int colMom = 187;
            const int sparkX = 258;
            int sparkW = Math.Max(50, width - sparkX - 8);
            int sparkH = rowH - 6;

            // 与 canvas 对齐：fillText 的 y 是基线，GDI DrawString 的 y 是行框顶部 → 需减 ascent
            float ascentMain = Ascent(g, mainFont);
            float ascentPct = Ascent(g, pctFont);

            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                int y = 24 + i * rowH;
                float baseline = y + rowH * 0.72f;

                g.DrawString(r.Code, mainFont, blackBrush, colCode, baseline - ascentMain);
                var rateStr = r.Rate?.ToString("F4", CultureInfo.InvariantCulture) ?? "—";
                g.DrawString(rateStr, mainFont, blackBrush, colRate, baseline - ascentMain);

                if (r.Ytd.HasValue)
                {
                    var brush = r.Ytd.Value >= 0 ? redBrush : blackBrush;
                    g.DrawString(FmtPct(r.Ytd.Value), pctFont, brush, colYoy, baseline - ascentPct);
                }
                if (r.Mtd.HasValue)
                {
                    var brush = r.Mtd.Value >= 0 ? redBrush : blackBrush;
                    g.DrawString(FmtPct(r.Mtd.Value), pctFont, brush, colMom, baseline - ascentPct);
                }

                // 迷你折线（纯黑白，无历史则跳过）
                if (r.Spark.Count >= 2 && sparkW > 20)
                {
                    DrawEpdSparkline(g, sparkX, y + 3, sparkW, sparkH, r.Spark);
                }
            }
        }
        return bmp;
    }

    private static string FmtPct(double v) =>
        $"{(v >= 0 ? "↑" : "↓")}{Math.Abs(v).ToString("F2", CultureInfo.InvariantCulture)}%";

    /// <summary>字体 ascent 像素高度（用于把基线对齐到 fillText 语义）。</summary>
    private static float Ascent(Graphics g, Font font)
    {
        var family = font.FontFamily;
        float ratio = family.GetCellAscent(font.Style) / (float)family.GetEmHeight(font.Style);
        return font.Size * ratio;
    }

    /// <summary>B/W 迷你折线：黑 1.5px 折线 + 端点圆点，裁剪在 spark 区域内（对应 _drawEpdSparkline）。</summary>
    private static void DrawEpdSparkline(Graphics g, int x, int y, int w, int h, IReadOnlyList<double> values)
    {
        double min = values.Min(), max = values.Max();
        double range = max - min;
        if (range == 0) range = 1;

        var prevClip = g.Clip;
        g.SetClip(new Rectangle(x, y, w, h));
        try
        {
            var pts = new PointF[values.Count];
            for (int i = 0; i < values.Count; i++)
            {
                float px = x + (float)(i / (double)(values.Count - 1)) * w;
                float py = y + h - (float)((values[i] - min) / range) * h;
                pts[i] = new PointF(px, py);
            }
            using var pen = new Pen(Color.Black, 1.5f);
            g.DrawLines(pen, pts);
            g.FillEllipse(Brushes.Black, pts[0].X - 1.5f, pts[0].Y - 1.5f, 3f, 3f);
            g.FillEllipse(Brushes.Black, pts[^1].X - 1.5f, pts[^1].Y - 1.5f, 3f, 3f);
        }
        finally
        {
            g.Clip = prevClip;
        }
    }

    /// <summary>
    /// 三色双平面打包（对应 dithering.js processImageData threeColor）：
    /// bw 位 = 灰度 ≥140 → 1（白）；red 位 = 红色主导（r&gt;160 且 r&gt;g 且 r&gt;b）→ 0（红），否则 1（白）。
    /// 红色像素在 bw 平面为黑位，但显示时被 red 平面掩蔽，与 web 行为一致。
    /// </summary>
    public static (byte[] bw, byte[] red) Pack1bpp2(Bitmap bmp, int width, int height)
    {
        int byteWidth = (width + 7) / 8;
        var bw = new byte[byteWidth * height];
        var red = new byte[byteWidth * height];
        var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var scan0 = data.Scan0;
            int stride = data.Stride;
            var rowBuf = new byte[stride];
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(IntPtr.Add(scan0, y * stride), rowBuf, 0, stride);
                for (int x = 0; x < width; x++)
                {
                    int idx = x * 3;
                    // Format24bppRgb 内存顺序为 B,G,R
                    int b = rowBuf[idx], g = rowBuf[idx + 1], r = rowBuf[idx + 2];
                    int grayscale = (int)Math.Round(0.299 * r + 0.587 * g + 0.114 * b);
                    int byteIndex = y * byteWidth + (x >> 3);
                    int mask = 1 << (7 - (x & 7));
                    if (grayscale >= 100) bw[byteIndex] |= (byte)mask; // 阈值 100：保留 AA 边缘，黑字不显淡
                    // 红 = 红色主导 且 灰度 < 100（与黑色同门槛）：AA 浅红边缘不判红，红字与黑字同粗细
                    bool isRed = r > 160 && r > g && r > b && grayscale < 100;
                    if (!isRed) red[byteIndex] |= (byte)mask;
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return (bw, red);
    }

    /// <summary>
    /// 1bpp 打包：MSB-first（bit7=最左像素）、bit=1 白；输出长度 = ceil(W/8)*H。
    /// 灰度阈值 100（web 用 140）：抗锯齿文字边缘灰度约 100-200，阈值 140 会把边缘切白、
    /// 黑字笔画变细显淡（红字因 red 判定宽松不受影响）→ 降到 100 保留笔画，与红色浓度匹配。
    /// </summary>
    public static byte[] Pack1bpp(Bitmap bmp, int width, int height)
    {
        const int threshold = 100;
        int byteWidth = (width + 7) / 8;
        var output = new byte[byteWidth * height];
        var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var scan0 = data.Scan0;
            int stride = data.Stride;
            var rowBuf = new byte[stride];
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(IntPtr.Add(scan0, y * stride), rowBuf, 0, stride);
                for (int x = 0; x < width; x++)
                {
                    int idx = x * 3;
                    // Format24bppRgb 内存顺序为 B,G,R
                    int b = rowBuf[idx], g = rowBuf[idx + 1], r = rowBuf[idx + 2];
                    int grayscale = (int)Math.Round(0.299 * r + 0.587 * g + 0.114 * b);
                    if (grayscale >= threshold)
                    {
                        output[y * byteWidth + (x >> 3)] |= (byte)(1 << (7 - (x & 7)));
                    }
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return output;
    }
}
