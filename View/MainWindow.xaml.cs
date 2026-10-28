using SerialAssistant.Infrastructure;
using SerialAssistant.ViewModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace SerialAssistant
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainViewModel? _viewModel;
        private bool _shutdownCompleted;
        private bool _shutdownStarted;

        public MainWindow()
        {
            InitializeComponent();

            if (DataContext is MainViewModel viewModel)
            {
                _viewModel = viewModel;
                viewModel.DisplayAppendRequested += OnDisplayAppendRequested;
                viewModel.DisplayResetRequested += OnDisplayResetRequested;
                viewModel.DisplayCleared += OnDisplayCleared;
            }
        }

        private void OnDisplayAppendRequested(string text)
        {
            var stickToEnd = IsScrolledToEnd();
            ReceivedTextBox.AppendText(text);
            if (stickToEnd) ReceivedTextBox.ScrollToEnd();
            EmptyHint.Visibility = Visibility.Collapsed;
        }

        private void OnDisplayResetRequested(string text)
        {
            // 缓冲裁剪后整体重设。这是唯一一次 O(n) 替换，约每 20 万字符发生一次。
            var stickToEnd = IsScrolledToEnd();
            ReceivedTextBox.Text = text;
            if (stickToEnd) ReceivedTextBox.ScrollToEnd();
            EmptyHint.Visibility = Visibility.Collapsed;
        }

        private void OnDisplayCleared()
        {
            ReceivedTextBox.Clear();
            EmptyHint.Visibility = Visibility.Visible;
        }

        /// <summary>用户向上翻看历史时不要再把他拽回底部。</summary>
        private bool IsScrolledToEnd()
            => ReceivedTextBox.ExtentHeight - ReceivedTextBox.ViewportHeight - ReceivedTextBox.VerticalOffset < 1.0;

        /// <summary>
        /// 关闭前先释放串口。这里必须"取消本次关闭 → 异步清理 → 重新排队关闭"，
        /// 且真正的 <see cref="Window.Close"/> 只能投递到消息循环里执行：
        /// WPF 在 Raising Closing 期间把内部 _isClosing 置为 true，此窗口期内调用
        /// Close/Show/设置 Visibility 都会抛 InvalidOperationException。
        /// 而 DisposeAsync 在串口已断开时往往同步完成，await 不切线程，
        /// finally 里的 Close() 就正好落在 Closing 的栈帧内 —— 即退出时的崩溃。
        /// </summary>
        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (_shutdownCompleted) return;   // 第二轮：清理已完成，放行关闭

            e.Cancel = true;                  // 截停本次关闭，等清理完成后再关

            if (_shutdownStarted) return;     // 清理期间的重复点击不再启动第二次清理
            _shutdownStarted = true;

            IsEnabled = false;                // 清理最长 5 秒，期间禁止交互
            _ = ShutdownAsync();
        }

        private async Task ShutdownAsync()
        {
            try
            {
                if (_viewModel is not null)
                {
                    // 硬上限：驱动卡死时也必须能退出，绝不能让窗口永远关不掉
                    await _viewModel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log("Window_Closing", ex);
            }
            finally
            {
                if (_viewModel is not null)
                {
                    _viewModel.DisplayAppendRequested -= OnDisplayAppendRequested;
                    _viewModel.DisplayResetRequested -= OnDisplayResetRequested;
                    _viewModel.DisplayCleared -= OnDisplayCleared;
                }

                _shutdownCompleted = true;

                // 关键：即使上面的 await 是同步完成的，也必须让出当前栈帧再关窗
                try
                {
                    _ = Dispatcher.InvokeAsync(Close, DispatcherPriority.Normal);
                }
                catch (InvalidOperationException)
                {
                    // 调度器已关闭，进程本来就要退出
                }
            }
        }
    }
}
