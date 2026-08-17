using System.Diagnostics;
using Microsoft.Win32;

namespace EpdDesktop;

/// <summary>开机自启：HKCU\Software\Microsoft\Windows\CurrentVersion\Run 注册表项。</summary>
public static class Autostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "EpdDesktop";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string v && v.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Set(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key == null) return;
            if (enable)
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exe)) return;
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception e)
        {
            Log.Warn($"设置开机自启失败: {e.Message}");
        }
    }
}
