namespace EpdDesktop;

public sealed record PushResult(int Width, int Height, bool ThreeColor, byte ModelId, int Mtu, bool Rle);

/// <summary>
/// 共享推送流程：连接 → 版本检查 → INIT（等 mtu/model 通知）→ 按面板型号决定分辨率与颜色模式渲染 →
/// 发送黑白平面（三色屏再发红色平面）→ REFRESH。托盘与 CLI --push 共用。
/// </summary>
public static class EpdPusher
{
    public static async Task<PushResult> PushRatesAsync(ulong addr, RatesData data, AppSettings settings)
        => await PushAsync(addr, settings, (w, h, threeColor) =>
            threeColor
                ? RateImageRenderer.Render2(w, h, DateTime.Today, BuildRows(data))
                : (RateImageRenderer.Render(w, h, DateTime.Today, BuildRows(data)), null));

    /// <summary>Token 用量面板推送（PICTURE 模式，与汇率图同流程）。</summary>
    public static async Task<PushResult> PushTokenAsync(ulong addr, TokenUsage usage, AppSettings settings)
        => await PushAsync(addr, settings, (w, h, threeColor) =>
            threeColor
                ? TokenImageRenderer.Render2(w, h, usage, DateTime.Now)
                : (TokenImageRenderer.Render(w, h, usage, DateTime.Now), null));

    /// <summary>共享推送流程：连接 → 版本检查 → INIT（等 mtu/model 通知）→ 按面板型号决定分辨率与颜色模式渲染 →
    /// 发送黑白平面（三色屏再发红色平面）→ REFRESH → 保持连接覆盖完整刷新时间（写响应先于刷新完成返回，
    /// 立即断开会触发固件 sleep 中断刷新 → 灰屏中间态）。</summary>
    private static async Task<PushResult> PushAsync(
        ulong addr, AppSettings settings, Func<int, int, bool, (byte[] bw, byte[]? red)> render)
    {
        using var ble = new BleClient();
        await ble.ConnectAsync(addr);
        var ver = await ble.ReadVersionAsync();
        if (ver < BleClient.MinAppVersion)
            throw new InvalidOperationException(
                $"固件版本过旧（0x{ver:X2}），需要 v1.6（0x16）以上，请先通过 Web 界面升级固件");
        await ble.SendInitAsync();

        // 设备型号优先于用户设置；未知型号回退设置
        var panel = PanelInfo.FromModelId(ble.ModelId);
        int w = panel?.Width ?? settings.PanelWidth;
        int h = panel?.Height ?? settings.PanelHeight;
        bool threeColor = panel?.ThreeColor ?? settings.ThreeColor;

        var (bwPlane, redPlane) = render(w, h, threeColor);

        await ble.SendImageAsync(bwPlane, isRed: false);
        if (redPlane != null) await ble.SendImageAsync(redPlane, isRed: true);
        await ble.SendRefreshAsync();
        var result = new PushResult(w, h, threeColor, ble.ModelId, ble.Mtu, ble.RleSupport);

        // 关键：REFREFSH 的 GATT 写响应在固件开始刷新时即返回，立即断开会触发固件 on_disconnect →
        // drv->sleep() 中断面板刷新，屏幕停在灰色中间态（web 端推送后保持连接直到刷新完成）。
        // 因此这里保持连接覆盖完整刷新时间再释放。
        await Task.Delay(TimeSpan.FromSeconds(25));
        return result;
    }

    public static List<RateImageRenderer.Row> BuildRows(RatesData data)
    {
        var rows = new List<RateImageRenderer.Row>(8);
        foreach (var code in RatesFetcher.Currencies)
        {
            double? rate = data.Today.TryGetValue(code, out var r) ? r : null;
            double? ytd = data.Ytd.TryGetValue(code, out var y) ? y : null;
            double? mtd = data.Mtd.TryGetValue(code, out var m) ? m : null;
            var spark = data.History.TryGetValue(code, out var hist)
                ? hist.Select(p => p.RateInv).ToList()
                : new List<double>();
            rows.Add(new RateImageRenderer.Row(code, rate, ytd, mtd, spark));
        }
        return rows;
    }
}
