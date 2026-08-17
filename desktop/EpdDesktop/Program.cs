using System.Runtime.InteropServices;

namespace EpdDesktop;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    private const int AttachParentProcess = -1;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.Exit(RunSelfTest());
            return;
        }
        if (args.Any(a => a.Equals("--scan", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.Exit(RunCli("scan", null));
            return;
        }
        int probeIdx = Array.FindIndex(args, a => a.Equals("--probe", StringComparison.OrdinalIgnoreCase));
        if (probeIdx >= 0 && probeIdx + 1 < args.Length)
        {
            Environment.Exit(RunCli("probe", args[probeIdx + 1]));
            return;
        }
        int pushIdx = Array.FindIndex(args, a => a.Equals("--push", StringComparison.OrdinalIgnoreCase));
        if (pushIdx >= 0 && pushIdx + 1 < args.Length)
        {
            Environment.Exit(RunCli("push", args[pushIdx + 1]));
            return;
        }
        if (args.Any(a => a.Equals("--fetch", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.Exit(RunCli("fetch", null));
            return;
        }

        using var mutex = new Mutex(true, "EpdDesktop_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new SecondInstanceContext());
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext());
    }

    private static int RunCli(string mode, string? arg)
    {
        bool console = AttachConsole(AttachParentProcess);
        try
        {
            if (console)
            {
                // 不 Dispose：进程即将退出，FreeConsole 负责清理
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            }
            var result = mode switch
            {
                "scan" => ScanProbe.ScanAsync().GetAwaiter().GetResult(),
                "probe" => ScanProbe.ProbeAsync(ParseAddress(arg!)).GetAwaiter().GetResult(),
                "push" => ScanProbe.PushAsync(ParseAddress(arg!)).GetAwaiter().GetResult(),
                "fetch" => ScanProbe.FetchAsync().GetAwaiter().GetResult(),
                _ => "未知模式",
            };
            var report = Path.Combine(Path.GetTempPath(), $"epd-{mode}.txt");
            File.WriteAllText(report, result);
            Console.WriteLine(result);
            Console.WriteLine($"报告: {report}");
            if (!console)
                MessageBox.Show(result, "EpdDesktop 蓝牙诊断", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        finally
        {
            if (console) FreeConsole();
        }
    }

    /// <summary>解析 12 位十六进制（可带 0x 前缀）或十进制地址。</summary>
    private static ulong ParseAddress(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.Length == 12 && s.All(Uri.IsHexDigit))
            return Convert.ToUInt64(s, 16);
        return ulong.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int RunSelfTest()
    {
        bool console = AttachConsole(AttachParentProcess);
        try
        {
            if (console)
            {
                // 不 Dispose：进程即将退出，FreeConsole 负责清理
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            }
            var summary = SelfTest.Run();
            // 无论是否有控制台都落盘，便于无头验证
            var report = Path.Combine(Path.GetTempPath(), "epd-selftest.txt");
            File.WriteAllText(report, summary);
            Console.WriteLine();
            Console.WriteLine($"自检报告: {report}");
            if (!console)
            {
                MessageBox.Show(summary, "EpdDesktop 自检", MessageBoxButtons.OK,
                    summary.StartsWith("自检通过") ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            }
            return summary.StartsWith("自检通过") ? 0 : 1;
        }
        finally
        {
            if (console) FreeConsole();
        }
    }
}

/// <summary>第二实例：托盘气泡提示已在运行，4 秒后退出。</summary>
internal sealed class SecondInstanceContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 4000 };

    public SecondInstanceContext()
    {
        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            BalloonTipTitle = "EpdDesktop",
            BalloonTipText = "程序已在运行（请查看系统托盘图标）",
        };
        _icon.ShowBalloonTip(3000);
        _timer.Tick += (_, _) => ExitThread();
        _timer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
