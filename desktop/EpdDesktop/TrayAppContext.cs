using System.Globalization;

namespace EpdDesktop;

/// <summary>
/// 托盘主程序：NotifyIcon + 菜单 + 每日定时推送调度器。
/// 调度：30s 轮询（对睡眠唤醒鲁棒）；到点补推；失败重试 3 次（间隔 60s）。
/// </summary>
public sealed class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _modeMenu;
    private readonly ToolStripMenuItem _modeRates;
    private readonly ToolStripMenuItem _modeCalendar;
    private readonly ToolStripMenuItem _modeClock;
    private readonly ToolStripMenuItem _modeToken;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly ToolStripMenuItem _tokenMenuItem;
    private readonly System.Threading.Timer _pollTimer;
    private readonly SynchronizationContext? _uiContext; // 主线程 UI 上下文（OnPoll 在 timer 线程回调）

    private AppSettings _settings;
    private bool _pushing;
    private DateTime? _lastPushTime;
    private bool _lastPushOk;

    private bool _tokenBusy;
    private DateTime? _lastTokenFailAt; // 失败退避：15 分钟内不自动重试（避免刷屏）

    public TrayAppContext()
    {
        _settings = Settings.Load();

        // 首次运行/设置变更后同步开机自启注册表
        if (_settings.AutoStart != Autostart.IsEnabled())
            Autostart.Set(_settings.AutoStart);

        _modeRates = new ToolStripMenuItem("汇率图", null, (_, _) => PushRatesAsync(manual: true));
        _modeCalendar = new ToolStripMenuItem("日历", null, (_, _) => SwitchModeAsync(1, "日历"));
        _modeClock = new ToolStripMenuItem("时钟", null, (_, _) => SwitchModeAsync(2, "时钟"));
        _modeToken = new ToolStripMenuItem("Token 用量", null, (_, _) => SwitchToTokenAsync());
        // 启动时按上次记录的显示模式初始化勾选（CheckOnClick 已移除：勾选只由 SetDisplayMode 在推送/切换成功后设置）
        var initialMode = _settings.DisplayMode switch
        {
            "token" => _modeToken,
            "calendar" => _modeCalendar,
            "clock" => _modeClock,
            _ => _modeRates,
        };
        initialMode.Checked = true;

        _modeMenu = new ToolStripMenuItem("显示模式", null, _modeRates, _modeCalendar, _modeClock, _modeToken);
        _autoStartItem = new ToolStripMenuItem("开机自启", null, (sender, _) =>
        {
            if (sender is ToolStripMenuItem item)
            {
                _settings.AutoStart = item.Checked;
                Autostart.Set(_settings.AutoStart);
                Settings.Save(_settings);
            }
        })
        { CheckOnClick = true, Checked = _settings.AutoStart };

        _menu = new ContextMenuStrip();
        // OnPoll 由 System.Threading.Timer 在线程池回调，Token 更新涉及 UI 控件 →
        // 显式保证主线程装有 WindowsFormsSynchronizationContext，供 Post 回主线程。
        if (SynchronizationContext.Current == null)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _uiContext = SynchronizationContext.Current;
        _menu.Items.Add("立即推送汇率", null, (_, _) => PushRatesAsync(manual: true));
        _menu.Items.Add("更新汇率数据", null, (_, _) => UpdateRatesAsync());
        _tokenMenuItem = new ToolStripMenuItem("Token 用量: 未配置", null, (_, _) => ShowTokenUsage());
        _menu.Items.Add(_tokenMenuItem);
        _menu.Items.Add(_modeMenu);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("设置…", null, (_, _) => ShowSettings());
        _menu.Items.Add(_autoStartItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => Exit());

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = _menu,
            Text = "EPD 墨水屏助手",
        };
        _tray.DoubleClick += (_, _) => ShowSettings();

        UpdateStatus();
        UpdateTokenMenuItem();
        // 首次运行引导：未配置设备时自动打开设置窗体选择墨水屏
        if (string.IsNullOrEmpty(_settings.DeviceAddress))
        {
            ShowBalloon("欢迎使用 EpdDesktop", "请先在设置中选择墨水屏设备（首次使用）");
            ShowSettings();
        }
        else
        {
            ShowBalloon("EpdDesktop 已启动", "已最小化到系统托盘，右键图标操作（推送/模式切换/设置）");
        }
        _pollTimer = new System.Threading.Timer(OnPoll, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    // ── 调度 ──────────────────────────────────────
    // 时间轴：推送时间 PushTime，汇率数据在 PushTime - 30 分钟自动抓取好，
    // 推送时刻直接用备好的数据（不受推送瞬间网络影响）。
    private void OnPoll(object? state)
    {
        // Token 用量更新：与汇率调度完全独立（不互斥，也不受推送相关守卫影响）
        MaybeUpdateToken();

        if (_pushing) return;
        if (!_settings.ScheduledPushEnabled) return;
        if (string.IsNullOrEmpty(_settings.DeviceAddress)) return;
        if (!TimeOnly.TryParse(_settings.PushTime, out var pushTime)) return;

        var today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var now = DateTime.Now.TimeOfDay;

        // 1. 推送点：到点且今天未推 → 推送（数据已由提前抓取备好，未备好则现场抓）
        if (_settings.LastPushDate != today && now >= pushTime.ToTimeSpan())
        {
            Log.Info("定时推送触发");
            PushRatesAsync(manual: false);
        }
        // 2. 抓取点：推送前 30 分钟，今天尚未抓取 → 自动更新汇率数据
        else if (_settings.LastFetchDate != today && now >= pushTime.AddMinutes(-30).ToTimeSpan())
        {
            Log.Info($"定时抓取触发（推送前 30 分钟，推送时间 {_settings.PushTime}）");
            UpdateRatesAsync();
        }
        UpdateStatus();
    }

    // ── Token 用量更新（newapi 中转站，独立于汇率调度） ──
    private void MaybeUpdateToken()
    {
        if (!_settings.TokenEnabled || string.IsNullOrEmpty(_settings.TokenAccessToken)) return;
        var usage = _settings.TokenUsage;
        bool due = usage == null || usage.FetchedAt == default
                   || (DateTime.Now - usage.FetchedAt).TotalHours >= _settings.TokenUpdateHours;
        if (!due) return;
        // 免打扰时段：跳过执行、计时继续，恢复后因 due 立即补拉
        if (InQuietHours(_settings.TokenQuietStart, _settings.TokenQuietEnd, DateTime.Now.TimeOfDay)) return;
        // 失败退避：15 分钟内不自动重试（避免刷屏）
        if (_lastTokenFailAt is { } f && (DateTime.Now - f).TotalMinutes < 15) return;
        if (_uiContext != null && _uiContext != SynchronizationContext.Current)
            _uiContext.Post(_ => FetchTokenUsageAsync(manual: false), null);
        else
            FetchTokenUsageAsync(manual: false);
    }

    private async void FetchTokenUsageAsync(bool manual)
    {
        if (_tokenBusy)
        {
            if (manual) ShowBalloon("Token 用量", "正在更新中，请稍候", ToolTipIcon.Info);
            return;
        }
        if (string.IsNullOrEmpty(_settings.TokenAccessToken))
        {
            if (manual)
                ShowBalloon("Token 用量", "未配置访问令牌，请在设置中填写", ToolTipIcon.Warning);
            return;
        }
        bool ok = await FetchTokenCoreAsync();
        var usage = _settings.TokenUsage;
        if (ok)
        {
            Log.Info($"Token 用量更新: 今日 {TokenUsageFetcher.FmtTokens(usage!.DayTokens)} · " +
                     $"本月 {TokenUsageFetcher.FmtTokens(usage.MonthTokens)}（{(usage.Partial ? "部分" : "完整")}）");
            if (manual)
                ShowBalloon("Token 已更新",
                    $"今日 {TokenUsageFetcher.FmtTokens(usage.DayTokens)} · " +
                    $"本月 {TokenUsageFetcher.FmtTokens(usage.MonthTokens)}");
            // 当前显示的是 Token 面板 → 数据更新后自动重推（以持久化模式为准，跨重启保持）
            if (_settings.DisplayMode == "token") await PushTokenPanelAsync(manualBalloon: false);
        }
        else if (manual || usage == null || usage.FetchedAt == default
                 || (DateTime.Now - usage.FetchedAt).TotalHours > 24)
        {
            ShowBalloon("Token 更新失败", usage?.LastError ?? "未知错误", ToolTipIcon.Warning);
        }
    }

    /// <summary>执行一次采集并落盘；无 UI 副作用，返回是否成功（供自动/切换流程复用）。</summary>
    private async Task<bool> FetchTokenCoreAsync()
    {
        if (_tokenBusy) return false;
        if (string.IsNullOrEmpty(_settings.TokenAccessToken)) return false;
        _tokenBusy = true;
        UpdateTokenMenuItem();
        try
        {
            var usage = _settings.TokenUsage ?? new TokenUsage();
            _settings.TokenUsage = usage; // 统计过程中窗体/菜单也能看到对象（如 Pages）
            await TokenUsageFetcher.UpdateAsync(usage, _settings.TokenApiBase, _settings.TokenAccessToken,
                progress: m => Log.Info($"Token 更新: {m}"));
            _settings.TokenUsage = usage;
            Settings.Save(_settings);
            return true;
        }
        catch (TokenFetchException e)
        {
            _lastTokenFailAt = DateTime.Now;
            _settings.TokenUsage ??= new TokenUsage();
            _settings.TokenUsage.LastError = e.Message; // 仅内存，不随失败落盘
            Log.Warn($"Token 用量更新失败: {e.Message}");
            return false;
        }
        catch (Exception e)
        {
            _lastTokenFailAt = DateTime.Now;
            _settings.TokenUsage ??= new TokenUsage();
            _settings.TokenUsage.LastError = e.Message;
            Log.Error($"Token 用量更新异常: {e}");
            return false;
        }
        finally
        {
            _tokenBusy = false;
            UpdateTokenMenuItem();
        }
    }

    /// <summary>切换到 Token 面板：数据过期/缺失先采集，再推送到墨水屏（PICTURE 模式）。</summary>
    private async void SwitchToTokenAsync()
    {
        if (!_settings.TokenEnabled || string.IsNullOrEmpty(_settings.TokenAccessToken))
        {
            ShowBalloon("Token 用量", "未启用或未配置访问令牌（设置 → Token 用量）", ToolTipIcon.Warning);
            return;
        }
        var usage = _settings.TokenUsage;
        if (usage == null || usage.FetchedAt == default
            || (DateTime.Now - usage.FetchedAt).TotalHours >= _settings.TokenUpdateHours)
        {
            // 数据过期/缺失：先采集（手动切换不受免打扰限制）
            await FetchTokenCoreAsync();
        }
        if (_settings.TokenUsage is not { } u || u.FetchedAt == default)
        {
            ShowBalloon("Token 面板", "数据获取失败，无法推送", ToolTipIcon.Warning);
            return;
        }
        await PushTokenPanelAsync(manualBalloon: true);
    }

    /// <summary>渲染并推送 Token 面板（三色屏带红平面）；成功时把显示模式勾选同步到 Token。</summary>
    private async Task PushTokenPanelAsync(bool manualBalloon)
    {
        if (_pushing)
        {
            if (manualBalloon) ShowBalloon("忙", "推送任务进行中，请稍候");
            return;
        }
        if (string.IsNullOrEmpty(_settings.DeviceAddress))
        {
            if (manualBalloon) ShowBalloon("未配置设备", "请先在「设置」中选择墨水屏设备");
            return;
        }
        var usage = _settings.TokenUsage;
        if (usage == null || usage.FetchedAt == default)
        {
            if (manualBalloon) ShowBalloon("Token 面板", "尚无 Token 数据，请先更新", ToolTipIcon.Warning);
            return;
        }
        _pushing = true;
        try
        {
            var addr = ulong.Parse(_settings.DeviceAddress, CultureInfo.InvariantCulture);
            var result = await EpdPusher.PushTokenAsync(addr, usage, _settings);
            _lastPushOk = true;
            _lastPushTime = DateTime.Now;
            SetDisplayMode(_modeToken, "token"); // 设备当前显示 Token 面板
            Log.Info($"Token 面板推送成功: 今日 {TokenUsageFetcher.FmtTokens(usage.DayTokens)} · " +
                     $"本月 {TokenUsageFetcher.FmtTokens(usage.MonthTokens)} " +
                     $"({result.Width}x{result.Height} {(result.ThreeColor ? "三色" : "黑白")} model=0x{result.ModelId:X2})");
            if (manualBalloon)
                ShowBalloon("Token 面板已推送",
                    $"今日 {TokenUsageFetcher.FmtTokens(usage.DayTokens)} · 本月 {TokenUsageFetcher.FmtTokens(usage.MonthTokens)}");
        }
        catch (Exception e)
        {
            Log.Warn($"Token 面板推送失败: {e.Message}");
            if (manualBalloon) ShowBalloon("Token 面板推送失败", e.Message, ToolTipIcon.Warning);
        }
        finally
        {
            _pushing = false;
            UpdateStatus();
        }
    }

    private void UpdateTokenMenuItem()
    {
        var usage = _settings.TokenUsage;
        if (_tokenBusy && usage is { BaselineComplete: false })
            _tokenMenuItem.Text = "Token 用量: 统计中…";
        else if (!_settings.TokenEnabled || string.IsNullOrEmpty(_settings.TokenAccessToken)
                 || usage == null || usage.FetchedAt == default)
            _tokenMenuItem.Text = "Token 用量: 未配置";
        else
            _tokenMenuItem.Text = $"Token 用量: 今日 {TokenUsageFetcher.FmtTokens(usage.DayTokens)} · " +
                                  $"本月 {TokenUsageFetcher.FmtTokens(usage.MonthTokens)}";
    }

    /// <summary>显示模式切换成功：勾选唯一项、记录到设置并落盘（跨线程安全，推送可能在 timer/线程池 continuation 完成）。
    /// 自动重推等逻辑以 _settings.DisplayMode 为准，不依赖易失的菜单勾选。</summary>
    private void SetDisplayMode(ToolStripMenuItem item, string name)
    {
        _settings.DisplayMode = name;
        Settings.Save(_settings);
        if (_uiContext != null && _uiContext != SynchronizationContext.Current)
            _uiContext.Post(_ =>
            {
                foreach (var m in new[] { _modeRates, _modeCalendar, _modeClock, _modeToken })
                    m.Checked = m == item;
            }, null);
        else
        {
            foreach (var m in new[] { _modeRates, _modeCalendar, _modeClock, _modeToken })
                m.Checked = m == item;
        }
    }

    /// <summary>免打扰判定：start &lt;= end → 区间内；跨午夜 → 分两段。解析失败返回 false（不豁免）。</summary>
    private static bool InQuietHours(string start, string end, TimeSpan now)
    {
        if (!TimeOnly.TryParse(start, out var s) || !TimeOnly.TryParse(end, out var e)) return false;
        return s <= e ? now >= s.ToTimeSpan() && now < e.ToTimeSpan()
                      : now >= s.ToTimeSpan() || now < e.ToTimeSpan();
    }

    private void ShowTokenUsage()
    {
        using var form = new TokenUsageForm(_settings, () => FetchTokenUsageAsync(manual: true), ShowSettings);
        form.ShowDialog();
    }
    private async void PushRatesAsync(bool manual)
    {
        if (_pushing)
        {
            ShowBalloon("推送进行中", "已有推送任务在运行，请稍候");
            return;
        }
        _pushing = true;
        try
        {
            if (string.IsNullOrEmpty(_settings.DeviceAddress))
            {
                ShowBalloon("未配置设备", "请先在「设置」中选择墨水屏设备");
                return;
            }

            RatesData data;
            try
            {
                // 定时推送：今天已提前抓取（推送前 30 分钟）→ 直接用缓存数据渲染，
                // 推送动作不依赖网络；手动推送：现场抓取最新。
                if (!manual && _settings.LastFetchDate == DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    && _settings.Cache is { Rates.Count: > 0 })
                {
                    data = new RatesData
                    {
                        Date = _settings.Cache.Date,
                        FetchedAt = _settings.Cache.FetchedAt,
                        Today = _settings.Cache.Rates,
                        History = _settings.Cache.History,
                        FromCache = true,
                        CacheDate = _settings.Cache.Date,
                    };
                    RatesFetcher.ComputeChanges(data);
                }
                else
                {
                    data = await RatesFetcher.FetchAsync(_settings);
                }
            }
            catch (RatesFetchException e)
            {
                ShowBalloon("汇率获取失败", e.Message + "，今日已跳过", ToolTipIcon.Warning);
                Log.Warn($"汇率获取失败: {e.Message}");
                return;
            }

            // 无论是否来自缓存，保存最新缓存供离线兜底（含历史，保 YTD/MTD/折线）
            _settings.Cache = new RateCache { Date = data.Date, FetchedAt = DateTime.Now, Rates = data.Today, History = data.History };
            _settings.LastFetchDate = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            Settings.Save(_settings);
            if (data.FromCache)
                ShowBalloon("汇率使用缓存", $"联网获取失败，使用 {data.Date} 的缓存数据推送", ToolTipIcon.Warning);

            var addr = ulong.Parse(_settings.DeviceAddress, CultureInfo.InvariantCulture);

            Exception? lastErr = null;
            for (int attempt = 1; attempt <= 4; attempt++)
            {
                try
                {
                    var result = await EpdPusher.PushRatesAsync(addr, data, _settings);
                    // 设备型号自动校准设置（供设置窗体显示真实面板）
                    if (result.ModelId != 0xFF &&
                        (_settings.PanelWidth != result.Width || _settings.PanelHeight != result.Height))
                    {
                        _settings.PanelWidth = result.Width;
                        _settings.PanelHeight = result.Height;
                        Settings.Save(_settings);
                    }
                    _settings.LastPushDate = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    Settings.Save(_settings);
                    _lastPushOk = true;
                    _lastPushTime = DateTime.Now;
                    SetDisplayMode(_modeRates, "rates"); // 设备当前显示汇率图
                    Log.Info($"推送成功: {_settings.DeviceName} ({data.Date}{(data.FromCache ? ", 缓存" : "")}) " +
                             $"model=0x{result.ModelId:X2} {result.Width}x{result.Height} {(result.ThreeColor ? "三色" : "黑白")} MTU={result.Mtu} RLE={result.Rle}");
                    ShowBalloon("推送成功", $"汇率已推送至 {_settings.DeviceName ?? "墨水屏"}（{data.Date}）");
                    return;
                }
                catch (Exception e)
                {
                    lastErr = e;
                    Log.Warn($"推送失败 (尝试 {attempt}/4): {e.Message}");
                    if (attempt < 4)
                    {
                        ShowBalloon("推送失败，正在重试", $"{e.Message}");
                        await Task.Delay(TimeSpan.FromSeconds(60));
                    }
                }
            }

            _lastPushOk = false;
            _lastPushTime = DateTime.Now;
            var hint = "请确认墨水屏已上电且靠近电脑";
            if (lastErr is TimeoutException)
                hint += "；若设备已休眠，请按唤醒引脚或切换到日历模式";
            ShowBalloon("推送失败", $"{lastErr?.Message}。{hint}", ToolTipIcon.Error);
        }
        catch (Exception e)
        {
            Log.Error($"推送流程异常: {e}");
            ShowBalloon("推送失败", $"异常: {e.Message}", ToolTipIcon.Error);
        }
        finally
        {
            _pushing = false;
            UpdateStatus();
        }
    }

    // ── 显示模式切换 ──────────────────────────────
    private async void SwitchModeAsync(byte mode, string modeName)
    {
        if (_pushing)
        {
            ShowBalloon("忙", "推送任务进行中，请稍候");
            return;
        }
        _pushing = true;
        try
        {
            if (string.IsNullOrEmpty(_settings.DeviceAddress))
            {
                ShowBalloon("未配置设备", "请先在「设置」中选择墨水屏设备");
                return;
            }
            var addr = ulong.Parse(_settings.DeviceAddress, CultureInfo.InvariantCulture);
            using var ble = new BleClient();
            await ble.ConnectAsync(addr);
            var ver = await ble.ReadVersionAsync();
            if (ver < BleClient.MinAppVersion)
                throw new InvalidOperationException(
                    $"固件版本过旧（0x{ver:X2}），需要 v1.6（0x16）以上，请先升级固件");
            await ble.SetTimeAsync(mode);
            Log.Info($"切换显示模式: {modeName} ({_settings.DeviceName})");
            SetDisplayMode(mode == 2 ? _modeClock : _modeCalendar, mode == 2 ? "clock" : "calendar");
            ShowBalloon($"已切换至{modeName}", "墨水屏正在刷新（约 20 秒），请勿断电");
            // 与推送同理：SET_TIME 触发立即重绘，写响应先于刷新完成返回；
            // 立即断开会触发固件 sleep 中断刷新 → 保持连接覆盖完整刷新时间。
            await Task.Delay(TimeSpan.FromSeconds(25));
        }
        catch (Exception e)
        {
            Log.Warn($"切换模式失败: {e.Message}");
            ShowBalloon("切换失败", e.Message, ToolTipIcon.Error);
        }
        finally
        {
            _pushing = false;
            UpdateStatus();
        }
    }

    // ── 汇率数据更新（只抓取不推送） ──────────────
    private async void UpdateRatesAsync(Action<string>? progress = null)
    {
        if (_pushing)
        {
            ShowBalloon("忙", "推送任务进行中，请稍候");
            progress?.Invoke("忙：推送任务进行中，请稍候");
            return;
        }
        _pushing = true;
        try
        {
            progress?.Invoke("正在更新汇率数据…");
            var data = await RatesFetcher.FetchAsync(_settings, progress);
            _settings.Cache = new RateCache { Date = data.Date, FetchedAt = DateTime.Now, Rates = data.Today, History = data.History };
            _settings.LastFetchDate = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            Settings.Save(_settings);
            int ok = RatesFetcher.Currencies.Count(c => data.Today.ContainsKey(c));
            Log.Info($"汇率数据更新: {data.Date} {ok}/{RatesFetcher.Currencies.Length} 币种{(data.FromCache ? "（缓存）" : "")}");
            var status = RatesFetcher.FormatStatus(data);
            progress?.Invoke(status);
            ShowBalloon("汇率已更新", $"日期 {data.Date}，{ok}/{RatesFetcher.Currencies.Length} 币种{(data.FromCache ? "，来自缓存" : "")}");
        }
        catch (Exception e)
        {
            Log.Warn($"汇率更新失败: {e.Message}");
            progress?.Invoke($"更新失败: {e.Message}");
            ShowBalloon("汇率更新失败", e.Message, ToolTipIcon.Warning);
        }
        finally
        {
            _pushing = false;
        }
    }

    // ── 设置窗体 ──────────────────────────────────
    private void ShowSettings()
    {
        using var form = new SettingsForm(_settings, () => PushRatesAsync(manual: true),
            p => UpdateRatesAsync(p), () => FetchTokenUsageAsync(manual: true), ShowTokenUsage);
        form.ShowDialog();
        _settings = Settings.Load(); // 重新读取（窗体已保存）
        _autoStartItem.Checked = _settings.AutoStart;
        UpdateTokenMenuItem();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var last = _lastPushTime?.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "从未";
        var result = _lastPushTime == null ? "" : _lastPushOk ? "成功" : "失败";
        var device = string.IsNullOrEmpty(_settings.DeviceName) ? "未配置设备" : _settings.DeviceName;
        var pushInfo = _settings.ScheduledPushEnabled ? $" · 每日 {_settings.PushTime} 推送" : "";
        _tray.Text = $"EPD 墨水屏 · {device} · 上次推送 {last} {result}{pushInfo}".Trim();
    }

    private void ShowBalloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        try
        {
            _tray.ShowBalloonTip(3000, title, text, icon);
        }
        catch
        {
            // 托盘不可用时忽略
        }
    }

    private void Exit()
    {
        _pollTimer.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pollTimer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _menu.Dispose();
        }
        base.Dispose(disposing);
    }
}
