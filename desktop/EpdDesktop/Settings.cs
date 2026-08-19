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

    // ── Token 用量（newapi 中转站） ──
    public bool TokenEnabled { get; set; }                          // 默认 false
    public string TokenApiBase { get; set; } = "https://newapi.liubaitech.cn";
    public string? TokenAccessToken { get; set; }                   // 用户粘贴的访问令牌（明文，与现有设置一致）
    public int TokenUpdateHours { get; set; } = 4;                  // 更新间隔（小时）；Sanitize 钳制 1..24
    public string TokenQuietStart { get; set; } = "22:00";          // 免打扰开始 HH:mm
    public string TokenQuietEnd { get; set; } = "08:00";            // 免打扰结束 HH:mm
    public TokenUsage? TokenUsage { get; set; }                     // 上次采集结果 + 增量累计
}

/// <summary>Token 用量采集结果（存于 AppSettings.TokenUsage，随 settings.json 持久化）。</summary>
public class TokenUsage
{
    public string Month { get; set; } = "";       // "yyyy-MM"；fetcher 检测切换并重置
    public long MonthTokens { get; set; }         // 本月累计 token（prompt+completion）
    public long MonthQuota { get; set; }          // 本月消耗 quota（显示时 ÷500000 = 元）
    public long DayTokens { get; set; }           // 今日累计 token（created_at >= 本地今日 0 点）
    public long DayQuota { get; set; }            // 今日消耗 quota（stat 今日窗口）
    public string DayReset { get; set; } = "";    // "yyyy-MM-dd"；fetcher 检测跨日重置
    public long BalanceQuota { get; set; }        // 账户余额 quota（user/self）
    public long LastLogAt { get; set; }           // 已统计到的最新一条日志 unix 秒（增量游标）
    public DateTime FetchedAt { get; set; }       // 上次成功更新
    public bool BaselineComplete { get; set; }    // 本月基准是否已建完
    public bool Partial { get; set; }             // 本次更新是否有页失败（显示"部分"）
    public int Pages { get; set; }                // 最近一次更新的页数（增量页数或基准总页数）
    public string? LastError { get; set; }        // 上次失败原因（仅内存，不随失败落盘）
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
        if (s.TokenUpdateHours < 1 || s.TokenUpdateHours > 24) s.TokenUpdateHours = 4;
        if (!TimeOnly.TryParse(s.TokenQuietStart, out _)) s.TokenQuietStart = "22:00";
        if (!TimeOnly.TryParse(s.TokenQuietEnd, out _)) s.TokenQuietEnd = "08:00";
        if (string.IsNullOrWhiteSpace(s.TokenApiBase)) s.TokenApiBase = "https://newapi.liubaitech.cn";
        return s;
    }
}
