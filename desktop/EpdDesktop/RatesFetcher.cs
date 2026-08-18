using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EpdDesktop;

public sealed class RatesFetchException : Exception
{
    public RatesFetchException(string message) : base(message) { }
    public RatesFetchException(string message, Exception inner) : base(message, inner) { }
}

public record RatePoint(string Date, double RateInv);

/// <summary>一次抓取/计算得到的完整汇率数据。</summary>
public class RatesData
{
    public string Date { get; set; } = "";
    public DateTime FetchedAt { get; set; } = DateTime.Now; // 抓取动作发生时间
    public Dictionary<string, double> Today { get; set; } = new();             // code -> 1外币 = X CNY
    public Dictionary<string, List<RatePoint>> History { get; set; } = new();  // code -> 历史点(rateInv)
    public Dictionary<string, double?> Ytd { get; set; } = new();              // code -> 今年波动 %
    public Dictionary<string, double?> Mtd { get; set; } = new();              // code -> 本月波动 %
    public bool FromCache { get; set; }
    public string? CacheDate { get; set; }
}

/// <summary>
/// 汇率抓取，镜像 quotes.js 数据源与口径：
/// 今日 8 币种来自 fawazahmed0/currency-api（jsdelivr CDN）；
/// 一年历史（YTD/MTD/折线图）来自 frankfurter.dev（仅 5 币种，VND/KZT/KES 无历史 → 变化列显示 —）。
/// </summary>
public static class RatesFetcher
{
    public static readonly string[] Currencies = { "MYR", "PHP", "THB", "VND", "IDR", "SGD", "KZT", "KES" };

    /// <summary>frankfurter.dev 支持的 5 币种。</summary>
    private static readonly string[] FfCodes = { "MYR", "THB", "SGD", "PHP", "IDR" };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    static RatesFetcher()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36 EpdDesktop/1.0");
    }

    /// <summary>抓取并计算；网络失败（含一次重试）后回退到设置里的缓存，无缓存则抛 RatesFetchException。</summary>
    public static async Task<RatesData> FetchAsync(AppSettings settings, Action<string>? progress = null)
    {
        try
        {
            var (date, today, history) = await FetchNetworkWithRetryAsync(progress);
            progress?.Invoke("数据抓取完成，正在计算波动…");
            var data = new RatesData { Date = date, Today = today, History = history };
            ComputeChanges(data);
            data.FromCache = false;
            return data;
        }
        catch (Exception e)
        {
            Log.Warn($"汇率抓取失败: {e.Message}");
            var cache = settings.Cache;
            if (cache is { Rates.Count: > 0 })
            {
                var data = new RatesData
                {
                    Date = cache.Date,
                    FetchedAt = cache.FetchedAt == default ? DateTime.Now : cache.FetchedAt,
                    FromCache = true,
                    CacheDate = cache.Date,
                };
                foreach (var (k, v) in cache.Rates) data.Today[k] = v;
                foreach (var (k, v) in cache.History) data.History[k] = v;
                ComputeChanges(data); // 缓存含历史 → YTD/MTD/折线完整
                return data;
            }
            throw new RatesFetchException("汇率获取失败，且无缓存可用", e);
        }
    }

    /// <summary>今日 + 一年历史（frankfurter）+ 采样历史（jsdelivr，VND/KZT/KES）并行抓取（对应 web 的 Promise.all），失败重试一次。</summary>
    private static async Task<(string date, Dictionary<string, double> today, Dictionary<string, List<RatePoint>> history)>
        FetchNetworkWithRetryAsync(Action<string>? progress = null)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                progress?.Invoke("正在抓取今日汇率（8 币种）…");
                var todayTask = FetchTodayAsync();
                var histTask = FetchFrankfurterHistoryAsync();
                var sampledTask = FetchSampledHistoryAsync(); // 采样失败不致命（catch 内仅警告）
                await Task.WhenAll(todayTask, histTask);
                progress?.Invoke("今日汇率 ✓，正在抓取一年历史（5 币种日线 + 3 币种采样）…");
                var (date, today) = await todayTask;
                var history = await histTask;
                var sampled = await sampledTask;
                foreach (var (code, points) in sampled)
                    if (!history.ContainsKey(code)) history[code] = points;
                progress?.Invoke($"历史数据 ✓（{history.Values.Sum(h => h.Count)} 个数据点）");
                return (date, today, history);
            }
            catch (Exception e)
            {
                last = e;
                if (attempt == 1) await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
        throw last!;
    }

    /// <summary>今日汇率：fawazahmed0 cny.json，返回 1外币 = X CNY。</summary>
    private static async Task<(string date, Dictionary<string, double> rates)> FetchTodayAsync()
    {
        var url = $"https://cdn.jsdelivr.net/npm/@fawazahmed0/currency-api@latest/v1/currencies/cny.json?ts={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var date = root.TryGetProperty("date", out var d) ? d.GetString() ?? "" : "";
        if (!root.TryGetProperty("cny", out var cny)) throw new RatesFetchException("响应缺少 cny 字段");

        var rates = new Dictionary<string, double>();
        foreach (var code in Currencies)
        {
            if (cny.TryGetProperty(code.ToLowerInvariant(), out var v) && v.TryGetDouble(out var rate) && rate > 0)
                rates[code] = 1.0 / rate;
        }
        if (rates.Count == 0) throw new RatesFetchException("今日汇率无有效数据");
        return (date, rates);
    }

    /// <summary>一年历史：frankfurter.dev，转换为 rateInv（1外币 = X CNY）并按日期排序。</summary>
    private static async Task<Dictionary<string, List<RatePoint>>> FetchFrankfurterHistoryAsync()
    {
        var end = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var start = DateTime.Today.AddYears(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var symbols = string.Join(",", FfCodes);
        var url = $"https://api.frankfurter.dev/v1/{start}..{end}?base=CNY&symbols={symbols}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        var history = new Dictionary<string, List<RatePoint>>();
        foreach (var c in FfCodes) history[c] = new List<RatePoint>();

        if (root.TryGetProperty("rates", out var rates) && rates.ValueKind == JsonValueKind.Object)
        {
            foreach (var day in rates.EnumerateObject())
            {
                foreach (var code in FfCodes)
                {
                    if (day.Value.TryGetProperty(code, out var v) && v.TryGetDouble(out var rate) && rate > 0)
                        history[code].Add(new RatePoint(day.Name, 1.0 / rate));
                }
            }
        }
        foreach (var c in FfCodes) history[c].Sort((a, b) => string.CompareOrdinal(a.Date, b.Date));
        return history;
    }

    /// <summary>
    /// jsdelivr 版本采样（对应 quotes.js _fetchSampledHistory）：给 VND/KZT/KES 补约 26 个历史点。
    /// 版本号形如 "2026.8.15"，按 14 天采样，6 并发拉取。失败仅警告（不阻断主流程）。
    /// </summary>
    private static async Task<Dictionary<string, List<RatePoint>>> FetchSampledHistoryAsync()
    {
        var needCodes = Currencies.Where(c => !FfCodes.Contains(c)).ToArray();
        var history = new Dictionary<string, List<RatePoint>>();
        foreach (var c in needCodes) history[c] = new List<RatePoint>();
        if (needCodes.Length == 0) return history;

        try
        {
            // 1. 版本列表
            var listUrl = $"https://data.jsdelivr.com/v1/packages/npm/@fawazahmed0/currency-api?ts={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            using var listReq = new HttpRequestMessage(HttpMethod.Get, listUrl);
            listReq.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var listRes = await Http.SendAsync(listReq, HttpCompletionOption.ResponseHeadersRead);
            listRes.EnsureSuccessStatusCode();
            using var listDoc = JsonDocument.Parse(await listRes.Content.ReadAsStringAsync());

            // 2. 一年内版本，按日期升序
            var cutoff = DateTime.Today.AddYears(-1);
            var dated = new List<(string version, DateTime date)>();
            if (listDoc.RootElement.TryGetProperty("versions", out var versions) && versions.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in versions.EnumerateArray())
                {
                    // 元素是对象 {"version": "2026.8.17", ...}，不是字符串
                    if (x.ValueKind != JsonValueKind.Object || !x.TryGetProperty("version", out var vp)) continue;
                    var ver = vp.GetString();
                    if (ver == null || !TryParseVersionDate(ver, out var d) || d < cutoff) continue;
                    dated.Add((ver, d));
                }
            }
            dated.Sort((a, b) => a.date.CompareTo(b.date));
            if (dated.Count == 0) return history;

            // 3. 每 14 天采样一个点，确保含最新版本
            var sampled = new List<(string version, DateTime date)>();
            DateTime? last = null;
            foreach (var item in dated)
            {
                if (last == null || (item.date - last.Value).TotalDays >= 14)
                {
                    sampled.Add(item);
                    last = item.date;
                }
            }
            if (sampled[^1].version != dated[^1].version) sampled.Add(dated[^1]);

            // 4. 6 并发拉取每个版本的 cny.json
            var lowerCodes = needCodes.Select(c => c.ToLowerInvariant()).ToArray();
            for (int i = 0; i < sampled.Count; i += 6)
            {
                var batch = sampled.Skip(i).Take(6).ToArray();
                var results = await Task.WhenAll(batch.Select(b => FetchVersionCnyAsync(b.version)));
                for (int j = 0; j < batch.Length; j++)
                {
                    var cny = results[j];
                    if (cny == null) continue;
                    var date = batch[j].date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    for (int k = 0; k < lowerCodes.Length; k++)
                    {
                        if (cny.TryGetValue(lowerCodes[k], out var rate) && rate > 0)
                            history[needCodes[k]].Add(new RatePoint(date, 1.0 / rate));
                    }
                }
            }
        }
        catch (Exception e)
        {
            Log.Warn($"版本采样失败（VND/KZT/KES 折线缺失）: {e.Message}");
        }
        foreach (var c in needCodes) history[c].Sort((a, b) => string.CompareOrdinal(a.Date, b.Date));
        return history;
    }

    private static async Task<Dictionary<string, double>?> FetchVersionCnyAsync(string version)
    {
        try
        {
            var url = $"https://cdn.jsdelivr.net/npm/@fawazahmed0/currency-api@{version}/v1/currencies/cny.json";
            using var res = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("cny", out var cny) || cny.ValueKind != JsonValueKind.Object) return null;
            var result = new Dictionary<string, double>();
            foreach (var p in cny.EnumerateObject())
            {
                if (p.Value.TryGetDouble(out var rate) && rate > 0) result[p.Name] = rate;
            }
            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解析 "2026.8.15" 格式的版本日期。</summary>
    private static bool TryParseVersionDate(string version, out DateTime date)
    {
        date = default;
        var parts = version.Split('.');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out var y) || !int.TryParse(parts[1], out var m) || !int.TryParse(parts[2], out var d)) return false;
        if (m < 1 || m > 12 || d < 1 || d > 31) return false;
        date = new DateTime(y, m, d);
        return true;
    }

    /// <summary>生成逐币种更新状态文本（用户可见：8 国是否全部更新）。</summary>
    public static string FormatStatus(RatesData data)
    {
        var sb = new StringBuilder();
        int ok = 0;
        foreach (var code in Currencies)
        {
            bool hasRate = data.Today.TryGetValue(code, out var rate);
            double? ytd = data.Ytd.TryGetValue(code, out var y) ? y : null;
            double? mtd = data.Mtd.TryGetValue(code, out var m) ? m : null;
            int pts = data.History.TryGetValue(code, out var h) ? h.Count : 0;
            if (hasRate) ok++;
            var rateStr = hasRate ? rate.ToString("F4", CultureInfo.InvariantCulture) : "✗";
            var ytdStr = ytd.HasValue ? $"{Arrow(ytd.Value)}{Math.Abs(ytd.Value).ToString("F2", CultureInfo.InvariantCulture)}%" : "—";
            var mtdStr = mtd.HasValue ? $"{Arrow(mtd.Value)}{Math.Abs(mtd.Value).ToString("F2", CultureInfo.InvariantCulture)}%" : "—";
            sb.AppendLine($"  {code}: 汇率 {rateStr}  今年{ytdStr}  本月{mtdStr}  折线{pts}点");
        }
        // 数据源每日发布前一交易日汇率（与 Web 版一致），抓取时间与数据日期分开标注
        var fetched = data.FetchedAt == default ? "" : $" · 抓取于 {data.FetchedAt:MM-dd HH:mm}";
        return $"数据日期 {data.Date}{(data.FromCache ? "（缓存）" : "（在线）")}{fetched}\n" +
               $"{ok}/{Currencies.Length} 币种更新成功\n" + sb.ToString().TrimEnd();
    }

    private static string Arrow(double v) => v >= 0 ? "↑" : "↓";

    /// <summary>计算 YTD/MTD：与 periodStart 绝对距离最近、且不晚于今天的历史点（对应 _calcChange）。</summary>
    public static void ComputeChanges(RatesData data)
    {
        var now = DateTime.Today;
        data.Ytd = CalcChange(data, new DateTime(now.Year, 1, 1), now);
        data.Mtd = CalcChange(data, new DateTime(now.Year, now.Month, 1), now);
    }

    private static Dictionary<string, double?> CalcChange(RatesData data, DateTime periodStart, DateTime now)
    {
        var result = new Dictionary<string, double?>();
        foreach (var code in Currencies)
        {
            if (!data.Today.TryGetValue(code, out var todayRate))
            {
                result[code] = null;
                continue;
            }
            double? closestRate = null;
            long closestDiff = long.MaxValue;
            if (data.History.TryGetValue(code, out var hist))
            {
                foreach (var h in hist)
                {
                    if (!DateTime.TryParseExact(h.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var hDate)) continue;
                    if (hDate > now) continue;
                    var diff = Math.Abs((hDate - periodStart).Ticks);
                    if (diff < closestDiff)
                    {
                        closestDiff = diff;
                        closestRate = h.RateInv;
                    }
                }
            }
            result[code] = closestRate.HasValue ? (todayRate - closestRate.Value) / closestRate.Value * 100 : null;
        }
        return result;
    }
}
