using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace SerialAssistant
{
    /// <summary>日志保存路径设置弹窗。仅做格式与静态校验，真实可写性由日志服务在开启时验证。</summary>
    public partial class LogPathDialog : Window
    {
        public LogPathDialog(string initialPath)
        {
            InitializeComponent();
            PathTextBox.Text = initialPath;
            Loaded += (_, _) =>
            {
                PathTextBox.Focus();
                PathTextBox.CaretIndex = PathTextBox.Text.Length;
            };
        }

        /// <summary>校验通过后的完整路径，仅在 DialogResult 为 true 时有效。</summary>
        public string FilePath { get; private set; } = string.Empty;

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "选择日志保存位置",
                Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                DefaultExt = ".log",
                AddExtension = true,
                // 追加写入是预期行为，不需要系统的覆盖确认
                OverwritePrompt = false,
                FileName = $"SerialLog_{DateTime.Now:yyyyMMdd_HHmmss}.log"
            };

            if (TryResolvePath(PathTextBox.Text, out var current, out _))
            {
                dialog.InitialDirectory = Path.GetDirectoryName(current);
                dialog.FileName = Path.GetFileName(current);
            }

            if (dialog.ShowDialog(this) == true)
            {
                PathTextBox.Text = dialog.FileName;
                PathTextBox.CaretIndex = PathTextBox.Text.Length;
            }
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            if (!TryResolvePath(PathTextBox.Text, out var path, out var error))
            {
                ErrorText.Text = error;
                ErrorText.Visibility = Visibility.Visible;
                PathTextBox.Focus();
                return;
            }

            FilePath = path;
            DialogResult = true;
        }

        private void PathTextBox_TextChanged(object sender, TextChangedEventArgs e)
            => ErrorText.Visibility = Visibility.Collapsed;

        /// <summary>
        /// 解析并校验用户输入：去引号、补全完整路径、拦截非法字符与目录、补默认扩展名。
        /// 目录不存在时直接拦截，不擅自创建目录。
        /// </summary>
        internal static bool TryResolvePath(string? input, out string fullPath, out string error)
        {
            fullPath = string.Empty;
            error = string.Empty;

            var text = input?.Trim() ?? string.Empty;
            if (text.Length >= 2 && text.StartsWith('"') && text.EndsWith('"'))
                text = text[1..^1].Trim();

            if (string.IsNullOrWhiteSpace(text))
            {
                error = "请输入日志文件的保存路径";
                return false;
            }

            try
            {
                fullPath = Path.GetFullPath(text);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                error = $"路径格式无效：{ex.Message}";
                return false;
            }

            if (Directory.Exists(fullPath) || Path.EndsInDirectorySeparator(text))
            {
                error = "该路径指向文件夹，请指定日志文件名";
                return false;
            }

            var fileName = Path.GetFileName(fullPath);
            if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                error = "文件名无效或包含 \\ / : * ? \" < > | 等非法字符";
                return false;
            }

            if (string.IsNullOrEmpty(Path.GetExtension(fullPath))) fullPath += ".log";

            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                error = $"目录不存在：{directory}";
                return false;
            }

            if (File.Exists(fullPath) && new FileInfo(fullPath).IsReadOnly)
            {
                error = "该文件为只读，无法追加写入";
                return false;
            }

            return true;
        }
    }
}
