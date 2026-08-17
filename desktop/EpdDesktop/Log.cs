namespace EpdDesktop;

/// <summary>滚动日志：%APPDATA%\EpdDesktop\epd-desktop.log，超过 512KB 滚动为 .old。</summary>
public static class Log
{
    private static readonly object Lock = new();

    public static string FilePath { get; } = Path.Combine(Settings.Dir, "epd-desktop.log");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    private static void Write(string level, string msg)
    {
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 512 * 1024)
                {
                    try { File.Copy(FilePath, FilePath + ".old", overwrite: true); } catch { /* 忽略 */ }
                    try { File.Delete(FilePath); } catch { /* 忽略 */ }
                }
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}{Environment.NewLine}");
            }
            catch
            {
                // 日志失败不阻断主流程
            }
        }
    }
}
