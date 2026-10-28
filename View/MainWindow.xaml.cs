using SerialAssistant.Infrastructure;
using SerialAssistant.ViewModel;
using System.ComponentModel;
using System.Windows;

namespace SerialAssistant
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainViewModel? _viewModel;
        private bool _shutdownCompleted;

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

        private async void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (_shutdownCompleted) return;

            e.Cancel = true;
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
                Close();
            }
        }
    }
}
