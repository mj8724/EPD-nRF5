using System.Text;

namespace EpdDesktop;

/// <summary>Token 用量窗体：显示本月 token/金额/余额与状态，可立即更新、跳转设置。</summary>
public sealed class TokenUsageForm : Form
{
    private readonly AppSettings _settings;
    private readonly Action _refresh;
    private readonly Action _openSettings;
    private readonly Label _info;
    private readonly Button _refreshButton;
    private readonly Button _settingsButton;
    private readonly Button _closeButton;
    private readonly System.Windows.Forms.Timer _displayTimer;
    private bool _fetching; // 立即更新点击后为 true，看到 FetchedAt/LastError 变化或超时后复位
    private DateTime _fetchStart;
    private DateTime _fetchStartFetchedAt;
    private string? _fetchStartError;

    public TokenUsageForm(AppSettings settings, Action refresh, Action openSettings)
    {
        _settings = settings;
        _refresh = refresh;
        _openSettings = openSettings;

        Text = "Token 用量 — EPD 墨水屏助手";
        Width = 440;
        Height = 330;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        _info = new Label
        {
            Dock = DockStyle.Top,
            Height = 190,
            Font = new Font("Consolas", 9f),
            Padding = new Padding(12, 10, 12, 4),
        };
        _refreshButton = new Button { Text = "立即更新", Left = 14, Top = 240, Width = 100 };
        _settingsButton = new Button { Text = "打开设置", Left = 122, Top = 240, Width = 100 };
        _closeButton = new Button { Text = "关闭", Left = 230, Top = 240, Width = 100, DialogResult = DialogResult.Cancel };

        Controls.Add(_info);
        Controls.Add(_refreshButton);
        Controls.Add(_settingsButton);
        Controls.Add(_closeButton);

        _refreshButton.Click += (_, _) =>
        {
            _fetching = true;
            _fetchStart = DateTime.Now;
            _fetchStartFetchedAt = _settings.TokenUsage?.FetchedAt ?? default;
            _fetchStartError = _settings.TokenUsage?.LastError;
            _refresh(); // 异步更新，结果经托盘气泡呈现；本窗体定时刷新显示
            RefreshDisplay();
        };
        _settingsButton.Click += (_, _) =>
        {
            _fetching = false;
            Close();
            _openSettings();
        };
        _displayTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _displayTimer.Tick += (_, _) => RefreshDisplay();
        _displayTimer.Start();
        FormClosed += (_, _) => _displayTimer.Dispose();

        RefreshDisplay();
    }

    private void RefreshDisplay()
    {
        if (IsDisposed) return;
        var usage = _settings.TokenUsage;
        var enabled = _settings.TokenEnabled;
        var hasToken = !string.IsNullOrEmpty(_settings.TokenAccessToken);
        var sb = new StringBuilder();
        sb.AppendLine($"Token 用量（{_settings.TokenApiBase}）");
        if (usage == null || usage.FetchedAt == default)
        {
            sb.AppendLine("今日使用: —");
            sb.AppendLine("本月使用: —");
            sb.AppendLine("今年使用: —");
            sb.AppendLine("全站使用: —");
        }
        else
        {
            sb.AppendLine($"今日使用: {TokenUsageFetcher.FmtTokens(usage.DayTokens)} token");
            sb.AppendLine($"本月使用: {TokenUsageFetcher.FmtTokens(usage.MonthTokens)} token");
            sb.AppendLine(usage.YearBaselineComplete
                ? $"今年使用: {TokenUsageFetcher.FmtTokens(usage.YearTokens)} token"
                : $"今年使用: 统计中（{usage.Pages} 页）…");
            sb.AppendLine(usage.SiteBaselineComplete
                ? $"全站使用: {TokenUsageFetcher.FmtTokens(usage.SiteTokens)} token"
                : $"全站使用: 统计中（{usage.Pages} 页）…");
        }
        sb.AppendLine(usage == null || usage.LastLogAt == 0
            ? "最近请求: —"
            : $"最近请求: {DateTimeOffset.FromUnixTimeSeconds(usage.LastLogAt).ToLocalTime():MM-dd HH:mm}");
        sb.AppendLine(usage == null || usage.FetchedAt == default
            ? "上次更新: 从未"
            : $"上次更新: {usage.FetchedAt:MM-dd HH:mm}");
        sb.AppendLine(usage == null || usage.FetchedAt == default
            ? "下次更新: 立即"
            : $"下次更新: {usage.FetchedAt.AddHours(_settings.TokenUpdateHours):MM-dd HH:mm}（免打扰时段顺延）");
        sb.AppendLine($"更新间隔: 每 {_settings.TokenUpdateHours} 小时 · 免打扰 {_settings.TokenQuietStart}-{_settings.TokenQuietEnd}");

        string status;
        if (!enabled)
            status = "未启用自动更新（可在设置中开启）";
        else if (!hasToken)
            status = "未配置访问令牌（请在设置中填写）";
        else if (_fetching && usage is { BaselineComplete: false })
            status = $"统计中（{usage.Pages} 页）…";
        else if (_fetching)
            status = "更新中…";
        else if (usage == null || usage.FetchedAt == default)
            status = "尚未更新";
        else if (!string.IsNullOrEmpty(usage.LastError))
            status = $"上次失败: {usage.LastError}";
        else
            status = usage.Partial ? "部分（有页面失败，下次更新补齐）" : "完整";
        sb.AppendLine($"状态: {status}");
        _info.Text = sb.ToString();

        // 立即更新结束（成功 → FetchedAt 变化；失败 → LastError 变化；未配置 → 立即复位；超时兜底）
        if (_fetching && (usage == null || !enabled || !hasToken
            || usage.FetchedAt != _fetchStartFetchedAt
            || !string.Equals(usage.LastError, _fetchStartError)
            || (DateTime.Now - _fetchStart).TotalMinutes > 20))
            _fetching = false;
    }
}
