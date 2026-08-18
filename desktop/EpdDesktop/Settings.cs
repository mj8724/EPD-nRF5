using System.Text.Json;

namespace EpdDesktop;

/// <summary>最近一次成功抓取的汇率缓存（离线兜底，含历史以保留 YTD/MTD 与折线）。</summary>
public class RateCache
{
    public string Date { get; set; } = "";
    public DateTime FetchedAt { get; set; }         // 抓取动作发生时间
    public Dictionary<string, double> Rates { get; set; } = new();
    public Dictionary<string, List<RatePoint>> History { get; set; } = new();
}

/// <summary>应用设置，持久化到 %APPDATA%\EpdDesktop\settings.json。</summary>
public class AppSettings
{
    public string? DeviceAddress { get; set; }      // 蓝牙地址 ulong 的十进制字符串，null = 未配置
    public string? DeviceName { get; set; }         // 如 "NRF_EPD_A1B2"
    public string PushTime { get; set; } = "09:00"; // HH:mm 24h
    public int PanelWidth { get; set; } = 400;
    public int PanelHeight { get; set; } = 300;
    public bool ThreeColor { get; set; }            // 三色屏：推送空红色平面
    public bool AutoStart { get; set; } = true;     // 默认开（用户要求开机自启）
    public bool ScheduledPushEnabled { get; set; } = true;
    public string? LastPushDate { get; set; }       // "yyyy-MM-dd"，仅成功推送时更新
    public string? LastFetchDate { get; set; }      // "yyyy-MM-dd"，每日自动抓取成功时更新（推送前 30 分钟）
    public RateCache? Cache { get; set; }           // 最近一次成功抓取的汇率
}

/// <summary>设置持久化与读取。</summary>
public static class Settings
{
    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EpdDesktop");

    public static string FilePath { get; } = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts);
                if (s != null) return Sanitize(s);
            }
        }
        catch (Exception e)
        {
            Log.Warn($"读取设置失败，使用默认值: {e.Message}");
        }
        return Sanitize(new AppSettings());
    }

    public static void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s, JsonOpts));
        }
        catch (Exception e)
        {
            Log.Warn($"保存设置失败: {e.Message}");
        }
    }

    private static AppSettings Sanitize(AppSettings s)
    {
        if (s.PanelWidth < 64 || s.PanelHeight < 64 || s.PanelWidth > 2000 || s.PanelHeight > 2000)
        {
            s.PanelWidth = 400;
            s.PanelHeight = 300;
        }
        if (!TimeOnly.TryParse(s.PushTime, out _)) s.PushTime = "09:00";
        return s;
    }
}
