using System.IO;
using System.Text;

namespace SerialAssistant.Infrastructure;

/// <summary>
/// 轻量崩溃日志写入器。不依赖 Application，可在 AppDomain / TaskScheduler 线程安全调用。
/// </summary>
public static class CrashLogger
{
    private static readonly object Gate = new();

    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SerialAssistant",
        "logs");

    /// <summary>
    /// 追加一条日志。此方法自身绝不抛异常，否则会在崩溃处理路径上造成递归崩溃。
    /// </summary>
    public static void Log(string source, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var file = Path.Combine(LogDirectory, $"crash_{DateTime.Now:yyyyMMdd}.log");
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}] {exception}{Environment.NewLine}";
            lock (Gate)
            {
                File.AppendAllText(file, entry, Encoding.UTF8);
            }
        }
        catch
        {
            // 记日志失败时静默放弃：继续抛异常只会把原始故障掩盖成递归崩溃。
        }
    }
}
