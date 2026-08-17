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
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly System.Threading.Timer _pollTimer;

    private AppSettings _settings;
    private bool _pushing;
    private DateTime? _lastPushTime;
    private bool _lastPushOk;

    public TrayAppContext()
    {
        _settings = Settings.Load();

        // 首次运行/设置变更后同步开机自启注册表
        if (_settings.AutoStart != Autostart.IsEnabled())
            Autostart.Set(_settings.AutoStart);

        _modeRates = new ToolStripMenuItem("汇率图", null, (_, _) => PushRatesAsync(manual: true)) { CheckOnClick = true };
        _modeCalendar = new ToolStripMenuItem("日历", null, (_, _) => SwitchModeAsync(1, "日历")) { CheckOnClick = true };
        _modeClock = new ToolStripMenuItem("时钟", null, (_, _) => SwitchModeAsync(2, "时钟")) { CheckOnClick = true };
        _modeRates.Checked = true;
        foreach (var item in new[] { _modeRates, _modeCalendar, _modeClock })
            item.CheckedChanged += (_, _) => SyncModeChecks();

        _modeMenu = new ToolStripMenuItem("显示模式", null, _modeRates, _modeCalendar, _modeClock);
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
        _menu.Items.Add("立即推送汇率", null, (_, _) => PushRatesAsync(manual: true));
        _menu.Items.Add("更新汇率数据", null, (_, _) => UpdateRatesAsync());
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
    private void OnPoll(object? state)
    {
        if (_pushing) return;
        if (!_settings.ScheduledPushEnabled) return;
        if (string.IsNullOrEmpty(_settings.DeviceAddress)) return;

        var today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (_settings.LastPushDate == today) return;
        if (TimeOnly.TryParse(_settings.PushTime, out var t) && DateTime.Now.TimeOfDay >= t.ToTimeSpan())
        {
            Log.Info("定时推送触发");
            PushRatesAsync(manual: false);
        }
        UpdateStatus();
    }

    // ── 汇率推送 ──────────────────────────────────
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
                data = await RatesFetcher.FetchAsync(_settings);
            }
            catch (RatesFetchException e)
            {
                ShowBalloon("汇率获取失败", e.Message + "，今日已跳过", ToolTipIcon.Warning);
                Log.Warn($"汇率获取失败: {e.Message}");
                return;
            }

            // 无论是否来自缓存，保存最新缓存供离线兜底（含历史，保 YTD/MTD/折线）
            _settings.Cache = new RateCache { Date = data.Date, Rates = data.Today, History = data.History };
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

    private void SyncModeChecks()
    {
        // 单选语义：只有一个勾选
        var items = new[] { _modeRates, _modeCalendar, _modeClock };
        if (items.Count(i => i.Checked) > 1)
        {
            // 保留最后点击的（CheckedChanged 已置位），取消其它
            foreach (var item in items.Where(i => i.Checked).Skip(1))
            {
                item.Checked = false;
            }
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
            _settings.Cache = new RateCache { Date = data.Date, Rates = data.Today, History = data.History };
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
        using var form = new SettingsForm(_settings, () => PushRatesAsync(manual: true), p => UpdateRatesAsync(p));
        form.ShowDialog();
        _settings = Settings.Load(); // 重新读取（窗体已保存）
        _autoStartItem.Checked = _settings.AutoStart;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var last = _lastPushTime?.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "从未";
        var result = _lastPushTime == null ? "" : _lastPushOk ? "成功" : "失败";
        var device = string.IsNullOrEmpty(_settings.DeviceName) ? "未配置设备" : _settings.DeviceName;
        _tray.Text = $"EPD 墨水屏 · {device} · 上次推送 {last} {result}".Trim();
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
