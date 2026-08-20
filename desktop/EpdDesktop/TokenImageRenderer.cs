using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;

namespace EpdDesktop;

/// <summary>
/// Token 用量面板渲染（推送图片模式，对应汇率图的 Render/Render2）。
/// 布局：红色标题 + 两列四格（今日/本月/今年/全站 token 数，无金额）+ 底部最近请求/更新时间。
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
            using var labelFont = new Font("Segoe UI", 16f, GraphicsUnit.Pixel);
            using var valueFont = new Font("Segoe UI", 30f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var smallFont = new Font("Segoe UI", 13f, GraphicsUnit.Pixel);
            using var redBrush = new SolidBrush(Color.FromArgb(0xFF, 0x00, 0x00));
            using var blackBrush = new SolidBrush(Color.Black);

            var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near };

            // 数值（无 token 后缀，列宽 190 内可放下；基准未完成显示统计中）
            string Val(long tokens, bool baselineOk) => !baselineOk ? "统计中…"
                : TokenUsageFetcher.FmtTokens(tokens);
            string Day() => usage == null ? "—" : Val(usage.DayTokens, true);
            string Month() => usage == null ? "—" : Val(usage.MonthTokens, true);
            string Year() => usage == null ? "—" : Val(usage.YearTokens, usage.YearBaselineComplete);
            string Site() => usage == null ? "—" : Val(usage.SiteTokens, usage.SiteBaselineComplete);
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

            // 两列四格（左列 x20，右列 x215，各宽 190）
            const int col1 = 20;
            const int col2 = 215;
            const int labelRow1 = 42;
            const int valueRow1 = 66;
            const int labelRow2 = 122;
            const int valueRow2 = 146;
            g.DrawString("今日使用", labelFont, blackBrush, col1, labelRow1);
            g.DrawString(Day(), valueFont, blackBrush, col1, valueRow1);
            g.DrawString("本月使用", labelFont, blackBrush, col2, labelRow1);
            g.DrawString(Month(), valueFont, blackBrush, col2, valueRow1);
            g.DrawString("今年使用", labelFont, blackBrush, col1, labelRow2);
            g.DrawString(Year(), valueFont, blackBrush, col1, valueRow2);
            g.DrawString("全站使用", labelFont, blackBrush, col2, labelRow2);
            g.DrawString(Site(), valueFont, blackBrush, col2, valueRow2);

            g.DrawString($"{Recent()}  ·  {Updated()}", smallFont, blackBrush, new RectangleF(0, 262, width, 20), centered);
        }
        return bmp;
    }

    private static (byte[] bw, byte[] red) Pack1bpp2(Bitmap bmp, int width, int height)
        => RateImageRenderer.Pack1bpp2(bmp, width, height);

    private static byte[] Pack1bpp(Bitmap bmp, int width, int height)
        => RateImageRenderer.Pack1bpp(bmp, width, height);
}
