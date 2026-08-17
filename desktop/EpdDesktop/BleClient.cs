using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using Buffer = System.Buffer;

namespace EpdDesktop;

/// <summary>
/// BLE 协议层，逐字节移植 html/js/main.js（连接/通知解析/写入管线/分块/RLE 选择）。
/// 设备端不要求配对（固件 SEC_OPEN、拒绝 pairing），按蓝牙地址直连即可。
/// </summary>
public sealed class BleClient : IDisposable
{
    // 已核实的 GUID（Web Bluetooth 同款字符串，勿做字节换算）
    public static readonly Guid SvcUuid = new("62750001-d828-918d-fb46-b6c11c675aec"); // service 0x0001
    public static readonly Guid CharUuid = new("62750002-d828-918d-fb46-b6c11c675aec"); // write+notify 0x0002
    public static readonly Guid VerUuid = new("62750003-d828-918d-fb46-b6c11c675aec");  // read-only 0x0003

    public const byte CmdInit = 0x01;
    public const byte CmdRefresh = 0x05;
    public const byte CmdSetTime = 0x20;
    public const byte CmdWriteImg = 0x30;

    /// <summary>固件版本下限（v1.6）；低于此值不支持 WRITE_IMAGE 协议。</summary>
    public const byte MinAppVersion = 0x16;

    /// <summary>流控：50 次无响应写后插入 1 次带响应写（对应 interleavedcount=50）。</summary>
    private const int InterleavedCount = 50;

    private static readonly TimeSpan OpTimeout = TimeSpan.FromSeconds(15);

    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _char;
    private GattCharacteristic? _verChar;
    private GattSession? _session;

    private int _mtu = 20; // 收到 mtu= 通知前使用安全值（MTU 23 也成立）
    private bool _rleSupport;
    private TaskCompletionSource<bool>? _mtuTcs;

    public string DeviceName { get; private set; } = "";
    public byte AppVersion { get; private set; }
    public byte ModelId { get; private set; } = 0xFF; // 0xFF = 未知（config 通知未收到）
    public int Mtu => _mtu;
    public bool RleSupport => _rleSupport;
    public int ChunkSize => _mtu - 2;

    /// <summary>按蓝牙地址连接：发现服务/特征、开启通知（固件随即回发 config + mtu= 文本）。</summary>
    public async Task ConnectAsync(ulong address)
    {
        DisposeSession();

        _device = await AwaitTimeout(BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(), "连接设备超时");
        DeviceName = string.IsNullOrEmpty(_device.Name) ? $"0x{address:X12}" : _device.Name;

        _session = await AwaitTimeout(GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId).AsTask(), "创建 GATT 会话超时");
        // 此投影无 OpenAsync：连接由后续 GATT 操作建立，MaintainConnection 保持链路
        _session.MaintainConnection = true;

        var svcResult = await AwaitTimeout(_device.GetGattServicesForUuidAsync(SvcUuid).AsTask(), "发现服务超时");
        if (svcResult.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0)
            throw new IOException($"未找到 EPD 服务: {svcResult.Status}");
        _service = svcResult.Services[0];

        var charResult = await AwaitTimeout(_service.GetCharacteristicsForUuidAsync(CharUuid).AsTask(), "发现数据特征超时");
        if (charResult.Status != GattCommunicationStatus.Success || charResult.Characteristics.Count == 0)
            throw new IOException($"未找到数据特征: {charResult.Status}");
        _char = charResult.Characteristics[0];

        var verResult = await AwaitTimeout(_service.GetCharacteristicsForUuidAsync(VerUuid).AsTask(), "发现版本特征超时");
        _verChar = verResult.Status == GattCommunicationStatus.Success
            ? verResult.Characteristics.FirstOrDefault()
            : null;

        _char.ValueChanged += OnValueChanged;
        var cccd = await AwaitTimeout(
            _char.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(), "开启通知超时");
        if (cccd != GattCommunicationStatus.Success)
            throw new IOException($"开启通知失败: {cccd}");
    }

    /// <summary>读取固件版本（特征 0x0003 第 1 字节）。</summary>
    public async Task<byte> ReadVersionAsync()
    {
        if (_verChar == null) throw new IOException("未找到版本特征");
        var result = await AwaitTimeout(_verChar.ReadValueAsync().AsTask(), "读取版本超时");
        var bytes = BufferToBytes(result.Value);
        if (bytes.Length == 0) throw new IOException("版本读取为空");
        AppVersion = bytes[0];
        return AppVersion;
    }

    /// <summary>发送 INIT 并等待 mtu= 通知（最长 5s，超时用默认 chunkSize=18）。</summary>
    public async Task SendInitAsync()
    {
        _mtuTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await WriteCmdAsync(CmdInit, null, withResponse: true);
        await Task.WhenAny(_mtuTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
    }

    /// <summary>同步时间并切换显示模式（SET_TIME：BE32 unix + TZ 小时 + mode）。</summary>
    public async Task SetTimeAsync(byte mode)
    {
        var ts = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        sbyte tz = (sbyte)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow).TotalHours; // 中国 = +8
        var payload = new byte[6];
        payload[0] = (byte)(ts >> 24);
        payload[1] = (byte)(ts >> 16);
        payload[2] = (byte)(ts >> 8);
        payload[3] = (byte)ts;
        payload[4] = (byte)tz;
        payload[5] = mode;
        await WriteCmdAsync(CmdSetTime, payload, withResponse: true);
    }

    /// <summary>
    /// 发送整幅平面图（WRITE_IMAGE 分块）。
    /// flags 双语义（与 web main.js writeImage 逐位一致）：
    ///   v1.6+（收到 rle=1 标记）: bit0=红平面, bit1=begin, bit2=RLE → (red?1:0)|(i==0?2:0)|(rle?4:0)
    ///   legacy（无 rle 标记，如本设备固件 0x20）: MSB 半字节=0 → begin, LSB 半字节=0xF → black
    ///     → bw: 0x0F/0xFF, red: 0x00/0xF0（写错 flags 会把黑平面数据写进红平面 RAM2，黑平面空白）
    /// RLE 仅在固件支持且压缩后更小时启用；50 次无响应写 + 1 次带响应写。
    /// </summary>
    public async Task SendImageAsync(byte[] plane, bool isRed)
    {
        if (_char == null) throw new InvalidOperationException("未连接");
        int chunkSize = ChunkSize;

        var rleChunks = _rleSupport ? Rle.CompressChunks(plane, chunkSize) : null;
        int rleLength = rleChunks?.Sum(c => c.Length) ?? plane.Length;
        bool useRle = _rleSupport && rleLength < plane.Length;
        var chunks = useRle ? rleChunks! : SplitPlain(plane, chunkSize);

        int noReplyCount = InterleavedCount;
        for (int i = 0; i < chunks.Count; i++)
        {
            byte flags = _rleSupport
                ? (byte)((isRed ? 0x01 : 0x00) | (i == 0 ? 0x02 : 0x00) | (useRle ? 0x04 : 0x00))
                : (byte)((isRed ? 0x00 : 0x0F) | (i == 0 ? 0x00 : 0xF0));
            var payload = new byte[chunks[i].Length + 2];
            payload[0] = CmdWriteImg;
            payload[1] = flags;
            Buffer.BlockCopy(chunks[i], 0, payload, 2, chunks[i].Length);

            bool withResponse = noReplyCount == 0;
            await WriteAsync(payload, withResponse);
            noReplyCount = withResponse ? InterleavedCount : noReplyCount - 1;
        }
    }

    /// <summary>REFRESH：显示面板 RAM 内容并置为 PICTURE 模式。</summary>
    public async Task SendRefreshAsync() => await WriteCmdAsync(CmdRefresh, null, withResponse: true);

    /// <summary>通用命令写：[opcode, ...payload]。</summary>
    public async Task WriteCmdAsync(byte opcode, byte[]? payload, bool withResponse)
    {
        var buf = new byte[1 + (payload?.Length ?? 0)];
        buf[0] = opcode;
        if (payload is { Length: > 0 }) Buffer.BlockCopy(payload, 0, buf, 1, payload.Length);
        await WriteAsync(buf, withResponse);
    }

    private async Task WriteAsync(byte[] payload, bool withResponse)
    {
        if (_char == null) throw new InvalidOperationException("未连接");
        using var writer = new DataWriter();
        writer.WriteBytes(payload);
        var status = withResponse
            ? await _char.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse)
            : await _char.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
        if (status != GattCommunicationStatus.Success)
            throw new IOException($"写入失败: {status}");
    }

    /// <summary>通知解析：文本 mtu=/t= 与 13 字节原始 config（data[7] = model_id，仅日志）。</summary>
    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            var bytes = BufferToBytes(args.CharacteristicValue);
            if (bytes.Length == 0) return;
            var text = Encoding.ASCII.GetString(bytes);
            Log.Info($"通知 [{bytes.Length}B]: '{Sanitize(text)}'");
            if (text.StartsWith("mtu=", StringComparison.Ordinal))
            {
                if (int.TryParse(text.AsSpan(4), out var mtu) && mtu > 0) _mtu = mtu;
                _rleSupport = text.Contains("rle=1", StringComparison.Ordinal);
                _mtuTcs?.TrySetResult(true);
                Log.Info($"MTU={_mtu}, RLE={_rleSupport}");
            }
            else if (text.StartsWith("t=", StringComparison.Ordinal))
            {
                // 远端时间，忽略
            }
            else
            {
                ModelId = bytes.Length > 7 ? bytes[7] : (byte)0xFF;
                Log.Info($"收到设备配置: model_id=0x{ModelId:X2}, len={bytes.Length}");
            }
        }
        catch (Exception e)
        {
            Log.Warn($"通知解析失败: {e.Message}");
        }
    }

    private static List<byte[]> SplitPlain(byte[] data, int chunkSize)
    {
        var list = new List<byte[]>((data.Length + chunkSize - 1) / chunkSize);
        for (int off = 0; off < data.Length; off += chunkSize)
            list.Add(data[off..Math.Min(off + chunkSize, data.Length)]);
        return list;
    }

    private static string Sanitize(string s) =>
        new(s.Select(c => c >= 0x20 && c < 0x7F ? c : '·').ToArray());

    private static byte[] BufferToBytes(IBuffer buffer)
    {
        if (buffer == null || buffer.Length == 0) return Array.Empty<byte>();
        var bytes = new byte[buffer.Length];
        DataReader.FromBuffer(buffer).ReadBytes(bytes);
        return bytes;
    }

    private static async Task<T> AwaitTimeout<T>(Task<T> task, string what)
    {
        var done = await Task.WhenAny(task, Task.Delay(OpTimeout));
        if (done != task) throw new TimeoutException($"{what}（设备可能未开机、不在广播或已休眠）");
        return await task;
    }

    private void DisposeSession()
    {
        if (_char != null) _char.ValueChanged -= OnValueChanged;
        _session?.Dispose();
        _session = null;
        _service?.Dispose();
        _service = null;
        _device?.Dispose();
        _device = null;
        _char = null;
        _verChar = null;
        _mtu = 20;
        _rleSupport = false;
        ModelId = 0xFF;
    }

    public void Dispose() => DisposeSession();
}
