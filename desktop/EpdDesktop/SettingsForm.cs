namespace EpdDesktop;

/// <summary>设置窗体：推送时间、面板尺寸、三色屏、定时推送、开机自启、设备选择、立即测试推送。</summary>
public sealed class SettingsForm : Form
{
    private static readonly (string label, int w, int h)[] PanelPresets =
    {
        ("4.2寸 (400x300)", 400, 300),
        ("7.5寸 (800x480)", 800, 480),
        ("7.5寸低分 (640x384)", 640, 384),
        ("5.83寸 (600x448)", 600, 448),
        ("2.9寸 (128x296)", 128, 296),
    };

    private readonly AppSettings _settings;
    private readonly Action _pushTest;
    private readonly Action<Action<string>?> _updateRatesAction;

    private readonly DateTimePicker _pushTime = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true };
    private readonly ComboBox _panelSize = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly CheckBox _threeColor = new() { Text = "三色屏（推送空红色平面）" };
    private readonly CheckBox _scheduled = new() { Text = "每天定时推送汇率" };
    private readonly CheckBox _autoStart = new() { Text = "开机自动启动" };
    private readonly Label _deviceLabel = new() { AutoSize = true, MaximumSize = new Size(280, 40) };
    private readonly Button _pickDevice = new() { Text = "选择设备…" };
    private readonly Button _testPush = new() { Text = "立即测试推送" };
    private readonly Button _updateRates = new() { Text = "更新汇率数据" };
    private readonly Label _rateStatus = new()
    {
        AutoSize = false,
        Dock = DockStyle.Fill,
        Font = new Font("Consolas", 9f),
        Padding = new Padding(6),
    };
    private readonly Button _okButton = new() { Text = "确定", DialogResult = DialogResult.OK };
    private readonly Button _cancelButton = new() { Text = "取消", DialogResult = DialogResult.Cancel };

    public SettingsForm(AppSettings settings, Action pushTest, Action<Action<string>?> updateRates)
    {
        _settings = settings;
        _pushTest = pushTest;
        _updateRatesAction = updateRates;

        Text = "设置 — EPD 墨水屏助手";
        Width = 460;
        Height = 580;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        // ── 推送 ──
        var pushGroup = new GroupBox { Text = "推送", Width = 420, Height = 100, Left = 14, Top = 12 };
        _pushTime.Left = 90; _pushTime.Top = 26; _pushTime.Width = 90;
        var timeLabel = new Label { Text = "推送时间:", Left = 14, Top = 29, AutoSize = true };
        _scheduled.Left = 200; _scheduled.Top = 28; _scheduled.Checked = _settings.ScheduledPushEnabled;
        _testPush.Left = 90; _testPush.Top = 60;
        _updateRates.Left = 205; _updateRates.Top = 60;
        pushGroup.Controls.Add(timeLabel);
        pushGroup.Controls.Add(_pushTime);
        pushGroup.Controls.Add(_scheduled);
        pushGroup.Controls.Add(_testPush);
        pushGroup.Controls.Add(_updateRates);

        // ── 显示 ──
        var dispGroup = new GroupBox { Text = "显示", Width = 420, Height = 96, Left = 14, Top = 122 };
        var sizeLabel = new Label { Text = "面板尺寸:", Left = 14, Top = 29, AutoSize = true };
        _panelSize.Left = 90; _panelSize.Top = 25;
        foreach (var (label, w, h) in PanelPresets)
            _panelSize.Items.Add(new PanelSizeItem(label, w, h));
        _panelSize.SelectedItem = _panelSize.Items.Cast<PanelSizeItem>()
            .FirstOrDefault(p => p.W == _settings.PanelWidth && p.H == _settings.PanelHeight)
            ?? _panelSize.Items[0];
        var tipLabel = new Label
        {
            Text = "必须与墨水屏实际分辨率一致，否则图像会错乱。",
            Left = 14, Top = 55, AutoSize = true,
            ForeColor = SystemColors.GrayText,
        };
        _threeColor.Left = 90; _threeColor.Top = 66; _threeColor.Checked = _settings.ThreeColor;
        dispGroup.Controls.Add(sizeLabel);
        dispGroup.Controls.Add(_panelSize);
        dispGroup.Controls.Add(_threeColor);
        dispGroup.Controls.Add(tipLabel);

        // ── 设备与自启 ──
        var devGroup = new GroupBox { Text = "设备", Width = 420, Height = 96, Left = 14, Top = 228 };
        _deviceLabel.Text = string.IsNullOrEmpty(_settings.DeviceName)
            ? "未配置（首次使用请选择设备）"
            : $"{_settings.DeviceName}（{_settings.DeviceAddress}）";
        _deviceLabel.Left = 14; _deviceLabel.Top = 24;
        _pickDevice.Left = 14; _pickDevice.Top = 56;
        _autoStart.Left = 130; _autoStart.Top = 58; _autoStart.Checked = _settings.AutoStart;
        devGroup.Controls.Add(_deviceLabel);
        devGroup.Controls.Add(_pickDevice);
        devGroup.Controls.Add(_autoStart);

        // ── 汇率数据状态 ──
        var statusGroup = new GroupBox { Text = "汇率数据状态", Width = 420, Height = 168, Left = 14, Top = 334 };
        statusGroup.Controls.Add(_rateStatus);
        _rateStatus.Dock = DockStyle.Fill;
        _rateStatus.Text = BuildCacheStatus();

        // ── 按钮 ──
        _okButton.Left = 250; _okButton.Top = 512;
        _cancelButton.Left = 340; _cancelButton.Top = 512;

        Controls.Add(pushGroup);
        Controls.Add(dispGroup);
        Controls.Add(devGroup);
        Controls.Add(statusGroup);
        Controls.Add(_okButton);
        Controls.Add(_cancelButton);

        // 初始值
        if (TimeOnly.TryParse(_settings.PushTime, out var t))
            _pushTime.Value = DateTime.Today.Add(t.ToTimeSpan());

        _pickDevice.Click += (_, _) =>
        {
            using var picker = new DevicePickerForm();
            if (picker.ShowDialog(this) == DialogResult.OK)
            {
                _settings.DeviceAddress = picker.SelectedAddress;
                _settings.DeviceName = picker.SelectedName;
                _deviceLabel.Text = $"{picker.SelectedName}（{picker.SelectedAddress}）";
            }
        };
        _testPush.Click += (_, _) =>
        {
            ApplyToSettings();
            _pushTest(); // 异步推送，结果以托盘气泡呈现
        };
        _updateRates.Click += (_, _) =>
        {
            ApplyToSettings();
            _rateStatus.Text = "正在更新汇率数据…";
            _updateRatesAction(SetRateStatus); // 异步抓取，进度与结果实时写入状态区
        };
        _okButton.Click += (_, _) => ApplyToSettings();
    }

    /// <summary>跨线程安全地更新状态区（进度回调在后台线程）。</summary>
    private void SetRateStatus(string text)
    {
        if (IsDisposed) return;
        try { BeginInvoke(() => _rateStatus.Text = text); } catch { /* 窗体关闭中 */ }
    }

    /// <summary>打开窗体时从缓存生成最近一次更新状态。</summary>
    private string BuildCacheStatus()
    {
        var cache = _settings.Cache;
        if (cache is not { Rates.Count: > 0 })
            return "尚未更新汇率数据\n点击「更新汇率数据」开始抓取（8 币种 + 一年历史）";
        var data = new RatesData { Date = cache.Date, Today = cache.Rates, History = cache.History };
        RatesFetcher.ComputeChanges(data);
        return RatesFetcher.FormatStatus(data);
    }

    private void ApplyToSettings()
    {
        _settings.PushTime = _pushTime.Value.ToString("HH:mm");
        _settings.ScheduledPushEnabled = _scheduled.Checked;
        _settings.ThreeColor = _threeColor.Checked;
        _settings.AutoStart = _autoStart.Checked;
        if (_panelSize.SelectedItem is PanelSizeItem p)
        {
            _settings.PanelWidth = p.W;
            _settings.PanelHeight = p.H;
        }
        Autostart.Set(_settings.AutoStart);
        Settings.Save(_settings);
    }

    private sealed record PanelSizeItem(string Label, int W, int H)
    {
        public override string ToString() => Label;
    }
}
