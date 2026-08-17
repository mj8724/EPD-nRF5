using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Radios;

namespace EpdDesktop;

/// <summary>
/// 命令行诊断（无需托盘界面）：
/// --scan            扫描 BLE 广播 10 秒，列出所有设备并标记 NRF_EPD 匹配；
/// --probe &lt;地址&gt;  连接指定设备（十进制 ulong 或 12 位十六进制），读取固件版本并验证 MTU 通知解析。
/// </summary>
internal static class ScanProbe
{
    public static async Task<string> ScanAsync(int seconds = 10)
    {
        var sb = new StringBuilder();
        try
        {
            var radios = await Radio.GetRadiosAsync();
            var bt = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            sb.AppendLine(bt != null ? $"蓝牙射频: {bt.Name}  状态: {bt.State}" : "未找到蓝牙射频");
        }
        catch (Exception e)
        {
            sb.AppendLine($"查询射频失败: {e.Message}");
        }
        sb.AppendLine($"扫描 {seconds} 秒，匹配名称前缀 NRF_EPD 或服务 UUID {BleClient.SvcUuid}");

        var found = new Dictionary<ulong, (string name, short rssi, List<Guid> uuids)>();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        var done = new ManualResetEventSlim(false);
        watcher.Received += (_, e) =>
        {
            var adv = e.Advertisement;
            lock (found)
            {
                if (!found.TryGetValue(e.BluetoothAddress, out var cur))
                    found[e.BluetoothAddress] = (adv.LocalName, e.RawSignalStrengthInDBm, adv.ServiceUuids.ToList());
                else if (!string.IsNullOrEmpty(adv.LocalName) && string.IsNullOrEmpty(cur.name))
                    found[e.BluetoothAddress] = (adv.LocalName, cur.rssi, adv.ServiceUuids.ToList());
                else if (cur.uuids.Count == 0 && adv.ServiceUuids.Count > 0)
                    found[e.BluetoothAddress] = (cur.name, cur.rssi, adv.ServiceUuids.ToList());
            }
        };
        watcher.Stopped += (_, _) => done.Set();
        try
        {
            watcher.Start();
        }
        catch (Exception e)
        {
            sb.AppendLine($"启动扫描失败（蓝牙可能已关闭）: {e.Message}");
            return sb.ToString();
        }
        done.Wait(TimeSpan.FromSeconds(seconds));
        try { watcher.Stop(); } catch { /* 忽略 */ }

        lock (found)
        {
            if (found.Count == 0)
            {
                sb.AppendLine("未发现任何 BLE 广播设备");
                return sb.ToString();
            }
            sb.AppendLine($"共 {found.Count} 个设备（按信号强度排序）：");
            foreach (var (addr, (name, rssi, uuids)) in found.OrderByDescending(f => f.Value.rssi))
            {
                bool match = name.StartsWith("NRF_EPD", StringComparison.OrdinalIgnoreCase)
                             || uuids.Contains(BleClient.SvcUuid);
                var label = string.IsNullOrEmpty(name) ? "(未广播名称)" : name;
                sb.AppendLine($"{(match ? "[匹配]" : "      ")} {addr:X12}  {label}  RSSI={rssi}dBm  UUIDs=[{string.Join(",", uuids)}]");
            }
        }
        return sb.ToString();
    }

    /// <summary>--fetch：抓取汇率、保存缓存（含历史），打印各币种完整数据统计。</summary>
    public static async Task<string> FetchAsync()
    {
        var sb = new StringBuilder();
        try
        {
            var settings = Settings.Load();
            var data = await RatesFetcher.FetchAsync(settings);
            settings.Cache = new RateCache { Date = data.Date, Rates = data.Today, History = data.History };
            Settings.Save(settings);
            sb.AppendLine($"抓取成功: {data.Date}{(data.FromCache ? "（来自缓存）" : "")}");
            foreach (var code in RatesFetcher.Currencies)
            {
                double? rate = data.Today.TryGetValue(code, out var r) ? r : null;
                double? ytd = data.Ytd.TryGetValue(code, out var y) ? y : null;
                double? mtd = data.Mtd.TryGetValue(code, out var m) ? m : null;
                int hist = data.History.TryGetValue(code, out var h) ? h.Count : 0;
                var rateStr = rate?.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) ?? "—";
                var ytdStr = ytd?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? "—";
                var mtdStr = mtd?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? "—";
                sb.AppendLine($"  {code}: 1={rateStr} CNY  YTD={ytdStr}%  MTD={mtdStr}%  历史点={hist}");
            }
        }
        catch (Exception e)
        {
            sb.AppendLine($"抓取失败: {e.Message}");
        }
        return sb.ToString();
    }

    public static async Task<string> PushAsync(ulong address)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"连接 {address:X12} 并推送汇率图 …");
        try
        {
            var settings = Settings.Load();
            var data = await RatesFetcher.FetchAsync(settings);
            sb.AppendLine(data.FromCache ? $"汇率来自缓存 ({data.Date})" : $"汇率 {data.Date}");
            var result = await EpdPusher.PushRatesAsync(address, data, settings);
            // 设备型号自动校准设置
            if (result.ModelId != 0xFF &&
                (settings.PanelWidth != result.Width || settings.PanelHeight != result.Height))
            {
                settings.PanelWidth = result.Width;
                settings.PanelHeight = result.Height;
                Settings.Save(settings);
            }
            sb.AppendLine($"推送成功: {result.Width}x{result.Height} {(result.ThreeColor ? "三色" : "黑白")} " +
                          $"model=0x{result.ModelId:X2} MTU={result.Mtu} RLE={result.Rle}");
            sb.AppendLine("已保持连接等待面板刷新完成（约 25 秒）");
        }
        catch (Exception e)
        {
            sb.AppendLine($"推送失败: {e.Message}");
        }
        return sb.ToString();
    }

    public static async Task<string> ProbeAsync(ulong address)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"连接 {address:X12} …");
        try
        {
            using var ble = new BleClient();
            await ble.ConnectAsync(address);
            var ver = await ble.ReadVersionAsync();
            sb.AppendLine($"连接成功: {ble.DeviceName}");
            sb.AppendLine($"固件版本: 0x{ver:X2}");
            await ble.SendInitAsync();
            sb.AppendLine($"MTU={ble.Mtu}  RLE={ble.RleSupport}（INIT 后通知解析）");
            sb.AppendLine("Probe 完成（仅连接+版本+INIT，未推送图像，未改动显示）");
        }
        catch (Exception e)
        {
            sb.AppendLine($"Probe 失败: {e.Message}");
        }
        return sb.ToString();
    }
}
