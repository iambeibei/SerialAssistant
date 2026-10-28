using SerialAssistant.Infrastructure;
using System.Windows;
using System.Windows.Threading;

namespace SerialAssistant;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>连续 UI 异常超过该次数后直接退出，避免弹窗刷屏。</summary>
    private const int MaxHandledDispatcherFaults = 3;

    private int _dispatcherFaultCount;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 三层兜底：UI 线程、非 UI 线程、未观察的 Task。
        // 缺任何一层，串口回调线程上的异常都会让进程静默消失且不留痕迹。
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLogger.Log("Dispatcher", e.Exception);
        e.Handled = true;

        if (Interlocked.Increment(ref _dispatcherFaultCount) > MaxHandledDispatcherFaults)
        {
            Shutdown(1);
            return;
        }

        MessageBox.Show(
            $"发生未处理异常：{e.Exception.Message}\n\n详细日志：{CrashLogger.LogDirectory}",
            "错误",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception
            ?? new Exception(e.ExceptionObject?.ToString() ?? "未知异常");
        CrashLogger.Log("AppDomain", exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        CrashLogger.Log("TaskScheduler", e.Exception);
        e.SetObserved();
    }
}
