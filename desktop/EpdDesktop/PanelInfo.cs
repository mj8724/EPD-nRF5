namespace EpdDesktop;

/// <summary>model_id → 面板几何与颜色模式（对照 html/index.html 的驱动下拉表）。</summary>
public static class PanelInfo
{
    public sealed record Panel(int Width, int Height, bool ThreeColor);

    private static readonly Dictionary<byte, Panel> Table = new()
    {
        // 4.2 寸
        [0x01] = new(400, 300, false),  // UC8176 黑白
        [0x02] = new(400, 300, true),   // SSD1619 三色
        [0x03] = new(400, 300, true),   // UC8176 三色
        [0x04] = new(400, 300, false),  // SSD1619 黑白
        // 0x05 = JD79668 四色（不支持渲染，视为未知）
        // 7.5 寸
        [0x06] = new(800, 480, false),  // UC8179 黑白
        [0x07] = new(800, 480, true),   // UC8179 三色
        // 0x0C = JD79665 四色（视为未知）
        [0x08] = new(640, 384, false),  // UC8159 黑白
        [0x09] = new(640, 384, true),   // UC8159 三色
        [0x0A] = new(880, 528, false),  // SSD1677 黑白
        // 5.83 寸
        [0x0E] = new(600, 448, true),   // UC8159 三色低分
        [0x0F] = new(600, 448, false),  // UC8159 黑白低分
        [0x10] = new(648, 480, true),   // UC8179 三色
        [0x11] = new(648, 480, false),  // UC8179 黑白
    };

    /// <summary>未知型号返回 null（由用户设置兜底）。</summary>
    public static Panel? FromModelId(byte modelId) => Table.GetValueOrDefault(modelId);
}
