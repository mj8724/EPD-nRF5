using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace EpdDesktop;

/// <summary>
/// 首次运行/重选设备：扫描 NRF_EPD 广播（名称前缀或服务 UUID），选中即保存地址。
/// 设备不要求配对，后续按地址直连。
/// </summary>
public sealed class DevicePickerForm : Form
{
    private const int ScanSeconds = 8;

    private readonly ListBox _list = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 40, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _okButton = new() { Text = "确定", Enabled = false, DialogResult = DialogResult.OK };
    private readonly Button _cancelButton = new() { Text = "取消", DialogResult = DialogResult.Cancel };
    private readonly System.Windows.Forms.Timer _scanTimer;

    private readonly BluetoothLEAdvertisementWatcher _watcher = new() { ScanningMode = BluetoothLEScanningMode.Active };
    private readonly Dictionary<ulong, (string name, short rssi)> _devices = new();

    public string? SelectedAddress { get; private set; }
    public string? SelectedName { get; private set; }

    public DevicePickerForm()
    {
        Text = "选择墨水屏设备";
        Width = 480;
        Height = 360;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1 };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        content.Controls.Add(_list);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10, 6, 10, 10),
        };
        buttons.Controls.Add(_cancelButton);
        buttons.Controls.Add(_okButton);

        var header = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Padding = new Padding(10, 6, 0, 0),
            Text = "正在扫描墨水屏设备（约 8 秒）…",
        };

        Controls.Add(panel);
        panel.Controls.Add(content, 0, 0);
        Controls.Add(buttons);
        Controls.Add(header);
        Controls.Add(_status);

        _okButton.Click += (_, _) => CommitSelection();
        _list.DoubleClick += (_, _) => { if (_list.SelectedItem != null) { CommitSelection(); DialogResult = DialogResult.OK; Close(); } };

        _watcher.Received += OnReceived;
        _watcher.Stopped += OnStopped;

        _scanTimer = new System.Windows.Forms.Timer { Interval = ScanSeconds * 1000 };
        _scanTimer.Tick += (_, _) => FinishScan();
        _scanTimer.Start();
        _watcher.Start();
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        if (IsDisposed) return;
        var adv = args.Advertisement;
        bool matches = adv.LocalName.StartsWith("NRF_EPD", StringComparison.Ordinal)
                       || adv.ServiceUuids.Contains(BleClient.SvcUuid);
        if (!matches) return;

        BeginInvoke(() =>
        {
            if (!_devices.TryGetValue(args.BluetoothAddress, out var cur) || !string.IsNullOrEmpty(adv.LocalName))
            {
                _devices[args.BluetoothAddress] = (adv.LocalName, args.RawSignalStrengthInDBm);
                RefreshList();
            }
            else if (cur.rssi != args.RawSignalStrengthInDBm)
            {
                _devices[args.BluetoothAddress] = (cur.name, args.RawSignalStrengthInDBm);
                RefreshList();
            }
        });
    }

    private void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (args.Error != BluetoothError.Success && !IsDisposed)
        {
            BeginInvoke(() => _status.Text = $"扫描异常: {args.Error}（请确认蓝牙已开启）");
        }
    }

    private void FinishScan()
    {
        _scanTimer.Stop();
        try { _watcher.Stop(); } catch { /* 忽略 */ }
        if (IsDisposed) return;
        _status.Text = _devices.Count == 0
            ? "未发现 NRF_EPD 设备，请确认墨水屏已上电并靠近电脑后重试。"
            : $"扫描完成，发现 {_devices.Count} 个设备（双击或选中后点确定）。";
    }

    private void RefreshList()
    {
        _list.Items.Clear();
        foreach (var (addr, (name, rssi)) in _devices.OrderByDescending(d => d.Value.rssi))
        {
            var label = string.IsNullOrEmpty(name) ? "NRF_EPD_????（未广播名称）" : name;
            _list.Items.Add($"{label}    {addr:X12}    RSSI: {rssi} dBm");
        }
        _okButton.Enabled = _list.Items.Count > 0;
    }

    private void CommitSelection()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _devices.Count) return;
        var entry = _devices.OrderByDescending(d => d.Value.rssi).ElementAt(_list.SelectedIndex);
        SelectedAddress = entry.Key.ToString();
        SelectedName = string.IsNullOrEmpty(entry.Value.name) ? $"0x{entry.Key:X12}" : entry.Value.name;
    }

    protected override void Dispose(bool disposing)
    {
        _scanTimer?.Dispose();
        try { _watcher.Stop(); } catch { /* 忽略 */ }
        _watcher.Received -= OnReceived;
        _watcher.Stopped -= OnStopped;
        base.Dispose(disposing);
    }
}
