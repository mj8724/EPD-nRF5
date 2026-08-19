using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace EpdDesktop;

public sealed class TokenFetchException : Exception
{
    public TokenFetchException(string message) : base(message) { }
    public TokenFetchException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// newapi 中转站 Token 用量采集（https://newapi.liubaitech.cn，New API /api/* 接口）。
/// 本月口径 = 本地时区本月 1 日 00:00 起；token 数只能从 /api/log/self 分页累加
/// prompt_tokens + completion_tokens（type=2 消费日志，page_size 上限 100）。
/// quota（金额）以 /api/log/self/stat 为准（页面累加会与 stat 重复计数，故不累加）。
/// </summary>
public static class TokenUsageFetcher
{
    public const int PageSize = 100;           // 接口上限
    public const int ParallelPages = 6;        // 建基准并发

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    static TokenUsageFetcher()
    {
        // 必须带浏览器 UA：无 UA 的请求被 WAF 拦 403
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) EpdDesktop/1.0");
    }

    /// <summary>
    /// 一次完整更新：月切换 → stat 快照 → 增量分页（无基准时并行建全月基准）。
    /// 失败抛 TokenFetchException（调用方负责不落盘）。
    /// </summary>
    public static async Task UpdateAsync(TokenUsage usage, string apiBase, string accessToken,
                                         Action<string>? progress = null, CancellationToken ct = default)
    {
        var now = DateTime.Now;
        var month = now.ToString("yyyy-MM");
        var today = now.ToString("yyyy-MM-dd");
        var baseUrl = apiBase.TrimEnd('/');

        // 1. 月切换：跨月重置（本月初次使用时 Month 为空同样进入）
        if (usage.Month != month)
        {
            usage.Month = month;
            usage.MonthTokens = 0;
            usage.MonthQuota = 0;
            usage.LastLogAt = 0;
            usage.BaselineComplete = false;
            usage.Partial = false;
            usage.Pages = 0;
        }
        usage.Partial = false; // 本次更新是否失败，每轮重置

        // 1b. 日切换：跨日重置今日统计（月累计保留）；跨日后的首次增量需重扫今日全窗口重建日累计
        var daySwitched = usage.DayReset != today;
        if (daySwitched)
        {
            usage.DayReset = today;
            usage.DayTokens = 0;
            usage.DayQuota = 0;
        }

        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0,
            TimeZoneInfo.Local.GetUtcOffset(now)).ToUnixTimeSeconds();
        var dayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0,
            TimeZoneInfo.Local.GetUtcOffset(now)).ToUnixTimeSeconds();
        var endTs = DateTimeOffset.Now.ToUnixTimeSeconds();

        // 2. stat 快照（并行 3 请求，秒回；任何失败抛异常中止本轮）
        var statTask = GetJsonAsync(Http, $"{baseUrl}/api/log/self/stat?type=2&start_timestamp={monthStart}&end_timestamp={endTs}", accessToken);
        var dayStatTask = GetJsonAsync(Http, $"{baseUrl}/api/log/self/stat?type=2&start_timestamp={dayStart}&end_timestamp={endTs}", accessToken);
        var selfTask = GetJsonAsync(Http, $"{baseUrl}/api/user/self", accessToken);
        await Task.WhenAll(statTask, dayStatTask, selfTask);
        using (var statDoc = await statTask)
        using (var dayStatDoc = await dayStatTask)
        using (var selfDoc = await selfTask)
        {
            if (!statDoc.RootElement.TryGetProperty("data", out var statData)
                || !statData.TryGetProperty("quota", out var quotaEl) || quotaEl.ValueKind != JsonValueKind.Number)
                throw new TokenFetchException("stat 接口返回异常（缺少 data.quota）");
            usage.MonthQuota = quotaEl.GetInt64();
            if (!dayStatDoc.RootElement.TryGetProperty("data", out var dayData)
                || !dayData.TryGetProperty("quota", out var dayQuotaEl) || dayQuotaEl.ValueKind != JsonValueKind.Number)
                throw new TokenFetchException("stat 接口返回异常（今日窗口缺少 data.quota）");
            usage.DayQuota = dayQuotaEl.GetInt64();
            if (!selfDoc.RootElement.TryGetProperty("data", out var selfData)
                || !selfData.TryGetProperty("quota", out var balEl) || balEl.ValueKind != JsonValueKind.Number)
                throw new TokenFetchException("user/self 接口返回异常（缺少 data.quota）");
            usage.BalanceQuota = balEl.GetInt64();
        }

        // 3. 增量 / 建基准
        if (usage.BaselineComplete)
        {
            await UpdateIncrementalAsync(usage, baseUrl, accessToken, monthStart, dayStart, daySwitched, endTs, progress, ct);
        }
        else
        {
            await BuildBaselineAsync(usage, baseUrl, accessToken, monthStart, dayStart, endTs, progress, ct);
        }

        usage.FetchedAt = DateTime.Now;
        usage.LastError = null;
        progress?.Invoke("完成");
    }

    /// <summary>增量：从 LastLogAt - 60s（容差防漏）串行分页到 now，去重累加（月只加 created_at &gt; LastLogAt；
    /// 跨日后的首次更新从今日 0 点重扫，日累计重建全量今日）。</summary>
    private static async Task UpdateIncrementalAsync(TokenUsage usage, string baseUrl, string accessToken,
        long monthStart, long dayStart, bool daySwitched, long endTs, Action<string>? progress, CancellationToken ct)
    {
        var start = usage.LastLogAt > 0
            ? (daySwitched ? Math.Min(usage.LastLogAt - 60, dayStart) : usage.LastLogAt - 60)
            : monthStart;
        long maxCreated = usage.LastLogAt;
        int pages = 0;

        // 先取第 1 页拿 total，确定页数
        var first = await FetchPageSkippingAsync(Http, baseUrl, accessToken, 1, start, endTs, usage);
        if (first.items == null) // 第 1 页两次失败：跳过本轮，游标不变，下轮重试
        {
            usage.Pages = 0;
            return;
        }
        long total = first.total;
        pages = (int)((total + PageSize - 1) / PageSize);
        if (pages == 0)
        {
            usage.Pages = 0;
            return;
        }
        Accumulate(first.items, usage, dedup: true, dayStart, dayRebuild: daySwitched, ref maxCreated);
        for (int p = 2; p <= pages; p++)
        {
            ct.ThrowIfCancellationRequested();
            var page = await FetchPageSkippingAsync(Http, baseUrl, accessToken, p, start, endTs, usage);
            if (page.items != null) Accumulate(page.items, usage, dedup: true, dayStart, dayRebuild: daySwitched, ref maxCreated);
        }
        usage.LastLogAt = maxCreated;
        usage.Pages = pages;
    }

    /// <summary>建基准：并行 6 页扫全月（从本月起点到 now），完成后置 BaselineComplete。</summary>
    private static async Task BuildBaselineAsync(TokenUsage usage, string baseUrl, string accessToken,
        long monthStart, long dayStart, long endTs, Action<string>? progress, CancellationToken ct)
    {
        // 重新建基准：清掉上次失败残留的累计，避免重复累加（Partial 已在 UpdateAsync 每轮重置）
        usage.MonthTokens = 0;
        usage.LastLogAt = 0;

        var first = await FetchPageSkippingAsync(Http, baseUrl, accessToken, 1, monthStart, endTs, usage);
        if (first.items == null) // 第 1 页两次失败：无法确定总量，中止本轮（下轮重试）
            throw new TokenFetchException("第 1 页获取失败，无法确定本月日志总量");
        long total = first.total;
        int pages = (int)((total + PageSize - 1) / PageSize);
        if (pages > 5000)
            throw new TokenFetchException($"本月日志过多（{total} 条），无法统计");
        usage.Pages = pages;
        if (pages == 0)
        {
            usage.BaselineComplete = true;
            return;
        }

        long maxCreated = 0;
        int done = 0;
        Accumulate(first.items, usage, dedup: false, dayStart, dayRebuild: true, ref maxCreated);
        done++;

        for (int basePage = 2; basePage <= pages; basePage += ParallelPages)
        {
            ct.ThrowIfCancellationRequested();
            var batch = new List<Task<(JsonElement[]? items, long total)>>();
            int batchEnd = Math.Min(basePage + ParallelPages, pages + 1);
            for (int p = basePage; p < batchEnd; p++)
            {
                int pageNum = p;
                batch.Add(Task.Run(() => FetchPageSkippingAsync(Http, baseUrl, accessToken, pageNum, monthStart, endTs, usage)));
            }
            var results = await Task.WhenAll(batch);
            foreach (var r in results)
                if (r.items != null) Accumulate(r.items, usage, dedup: false, dayStart, dayRebuild: true, ref maxCreated);
            done += results.Length;
            progress?.Invoke($"统计中 {done}/{pages} 页…");
        }
        usage.LastLogAt = maxCreated;
        usage.BaselineComplete = true;
    }

    /// <summary>取一页并累加；网络/HTTP 失败重试 1 次后仍失败则跳过该页并标 Partial（不中止整月），
    /// 返回 items=null 表示该页被跳过。结构异常（缺 data.items / success=false）抛 TokenFetchException 中止本轮。</summary>
    private static async Task<(JsonElement[]? items, long total)> FetchPageSkippingAsync(
        HttpClient http, string baseUrl, string accessToken, int p, long start, long end, TokenUsage usage)
    {
        var url = $"{baseUrl}/api/log/self?p={p}&page_size={PageSize}&type=2&start_timestamp={start}&end_timestamp={end}";
        JsonDocument doc;
        try
        {
            doc = await GetJsonAsync(http, url, accessToken);
        }
        catch (TokenFetchException)
        {
            await Task.Delay(500);
            try
            {
                doc = await GetJsonAsync(http, url, accessToken);
            }
            catch (TokenFetchException)
            {
                usage.Partial = true; // 跳过本页，下次增量更新自然补齐
                return (null, 0);
            }
        }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new TokenFetchException($"第 {p} 页接口返回异常（缺少 data.items）");
            long total = data.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : 0;
            return (items.EnumerateArray().Select(e => e.Clone()).ToArray(), total);
        }
    }

    private static void Accumulate(JsonElement[] items, TokenUsage usage, bool dedup, long dayStart, bool dayRebuild, ref long maxCreated)
    {
        foreach (var it in items)
        {
            long createdAt = it.TryGetProperty("created_at", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt64() : 0;
            if (createdAt > maxCreated) maxCreated = createdAt;
            long pt = it.TryGetProperty("prompt_tokens", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
            long cpt = it.TryGetProperty("completion_tokens", out var q) && q.ValueKind == JsonValueKind.Number ? q.GetInt64() : 0;
            // 月累计：去重（容差窗口内已统计过的旧条目不重复加）
            bool countMonth = !dedup || createdAt > usage.LastLogAt;
            // 日累计：跨日重扫（dayRebuild）时重建全量今日（含月已计过但日被重置的条目）；
            // 普通增量只加新条目，避免重复计数
            bool countDay = createdAt >= dayStart && (dayRebuild || countMonth);
            if (countMonth) usage.MonthTokens += pt + cpt;
            if (countDay) usage.DayTokens += pt + cpt;
        }
    }

    /// <summary>GET + Bearer 鉴权；错误映射为 TokenFetchException（读响应体 message）。调用方负责 Dispose 返回的文档。</summary>
    private static async Task<JsonDocument> GetJsonAsync(HttpClient http, string url, string token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (Exception e)
        {
            throw new TokenFetchException(e.Message, e);
        }
        using (res)
        {
            var body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                var msg = ExtractMessage(body) ?? $"HTTP {(int)res.StatusCode}";
                var mapped = res.StatusCode == HttpStatusCode.Unauthorized
                    ? "访问令牌无效或已过期，请在设置中重新填写"
                    : res.StatusCode == HttpStatusCode.Forbidden
                        ? "接口被拒绝（站点可能关闭了日志接口）"
                        : msg;
                throw new TokenFetchException(mapped);
            }
            var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("success", out var succ) && succ.ValueKind == JsonValueKind.False)
            {
                var msg = doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString() : null;
                doc.Dispose();
                throw new TokenFetchException(string.IsNullOrEmpty(msg) ? "接口返回失败" : msg!);
            }
            return doc; // 所有权移交调用方
        }
    }

    private static string? ExtractMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                return m.GetString();
        }
        catch { /* 非 JSON 响应体 */ }
        return null;
    }

    /// <summary>token 数格式化：≥1 亿 → "x.xx 亿"；≥1 万 → "x.xx 万"；否则原样。</summary>
    public static string FmtTokens(long n)
    {
        if (n >= 100_000_000) return $"{(n / 1e8):F2} 亿";
        if (n >= 10_000) return $"{(n / 1e4):F2} 万";
        return n.ToString();
    }
}
