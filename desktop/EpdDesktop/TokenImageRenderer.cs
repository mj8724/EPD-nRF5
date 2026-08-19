using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;

namespace EpdDesktop;

/// <summary>
/// Token 用量面板渲染（推送图片模式，对应汇率图的 Render/Render2）。
/// 布局：红色标题 + 今日使用 / 本月使用 两大数字（居中）+ 底部最近请求/更新时间。不显示金额。
/// 三色屏用红色标题与分隔线（Render2），黑白屏全黑（Render）。
/// </summary>
public static class TokenImageRenderer
{
    /// <summary>渲染并打包为黑白平面（无红；标题为黑）。</summary>
    public static byte[] Render(int width, int height, TokenUsage? usage, DateTime now)
        => Pack1bpp(DrawBitmap(width, height, usage, now, useRed: false), width, height);

    /// <summary>渲染并打包为黑白+红色双平面（三色屏；标题/分隔线为红）。</summary>
    public static (byte[] bw, byte[] red) Render2(int width, int height, TokenUsage? usage, DateTime now)
        => Pack1bpp2(DrawBitmap(width, height, usage, now, useRed: true), width, height);

    private static Bitmap DrawBitmap(int width, int height, TokenUsage? usage, DateTime now, bool useRed)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.Clear(Color.White);

            using var titleFont = new Font("Segoe UI", 18f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var labelFont = new Font("Segoe UI", 18f, GraphicsUnit.Pixel);
            using var valueFont = new Font("Segoe UI", 34f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var smallFont = new Font("Segoe UI", 14f, GraphicsUnit.Pixel);
            using var redBrush = new SolidBrush(Color.FromArgb(0xFF, 0x00, 0x00));
            using var blackBrush = new SolidBrush(Color.Black);

            var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near };

            string Day() => usage == null ? "—"
                : $"{TokenUsageFetcher.FmtTokens(usage.DayTokens)} token";
            string Month() => usage == null ? "—"
                : $"{TokenUsageFetcher.FmtTokens(usage.MonthTokens)} token";
            string Recent() => usage == null || usage.LastLogAt == 0 ? "—"
                : $"最近请求: {DateTimeOffset.FromUnixTimeSeconds(usage.LastLogAt).ToLocalTime():MM-dd HH:mm}";
            string Updated() => usage == null || usage.FetchedAt == default ? "—"
                : $"更新于: {usage.FetchedAt:MM-dd HH:mm}";

            var title = $"Token 用量 · {now.ToString("yyyy/M/d", CultureInfo.InvariantCulture)}";
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near })
            {
                g.DrawString(title, titleFont, useRed ? redBrush : blackBrush, new RectangleF(0, 4, width, 20), sf);
            }
            g.FillRectangle(useRed ? redBrush : blackBrush, 4, 28, width - 8, 2);

            // 今日使用（居中大数字）
            g.DrawString("今日使用", labelFont, blackBrush, new RectangleF(0, 48, width, 24), centered);
            g.DrawString(Day(), valueFont, blackBrush, new RectangleF(0, 76, width, 40), centered);
            // 本月使用
            g.DrawString("本月使用", labelFont, blackBrush, new RectangleF(0, 152, width, 24), centered);
            g.DrawString(Month(), valueFont, blackBrush, new RectangleF(0, 180, width, 40), centered);

            g.DrawString(Recent(), smallFont, blackBrush, new RectangleF(0, 246, width, 20), centered);
            g.DrawString(Updated(), smallFont, blackBrush, new RectangleF(0, 270, width, 20), centered);
        }
        return bmp;
    }

    private static (byte[] bw, byte[] red) Pack1bpp2(Bitmap bmp, int width, int height)
        => RateImageRenderer.Pack1bpp2(bmp, width, height);

    private static byte[] Pack1bpp(Bitmap bmp, int width, int height)
        => RateImageRenderer.Pack1bpp(bmp, width, height);
}
