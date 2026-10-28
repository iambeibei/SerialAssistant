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
        private bool _isClosing;

        public MainWindow()
        {
            InitializeComponent();
            if (DataContext is MainViewModel viewModel)
                viewModel.ScrollToEndRequested += OnScrollToEndRequested;
        }

        private void OnScrollToEndRequested(object? sender, EventArgs e)
            => ReceivedTextBox.ScrollToEnd();

        private async void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (_isClosing) return;
            e.Cancel = true;
            _isClosing = true;
            if (DataContext is MainViewModel viewModel)
                await viewModel.DisposeAsync();
            Close();
        }
    }
}
