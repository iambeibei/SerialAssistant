using SerialAssistant.Commonds;
using SerialAssistant.Model;
using SerialAssistant.Services;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Ports;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace SerialAssistant.ViewModel;

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private const int MaxDisplayCharacters = 1_000_000;
    private readonly ISerialPortService _serialService;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly ConcurrentQueue<string> _displayQueue = new();
    private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();
    private readonly Task _receiveTask;
    private readonly Task _displayTask;
    private string? _selectedPort;
    private int _selectedBaudRate = 115200;
    private int _selectedDataBits = 8;
    private string _selectedStopBits = "1";
    private Parity _selectedParity = Parity.None;
    private string _receivedText = string.Empty;
    private string _sendText = string.Empty;
    private string _statusMessage = "就绪";
    private bool _isShowHex;
    private bool _isSendHex;
    private bool _isAddCrc;
    private bool _showTimestamp;
    private bool _pauseDisplay;
    private bool _disposed;

    public MainViewModel() : this(new SerialPortService()) { }

    internal MainViewModel(ISerialPortService serialService)
    {
        _serialService = serialService;
        _serialService.StateChanged += OnServiceStateChanged;
        _serialService.StatisticsChanged += OnStatisticsChanged;
        _serialService.CommunicationError += OnCommunicationError;
        RefreshPortsCommand = new RelayCommand(_ => RefreshAvailablePorts(), _ => CanConfigure);
        OpenCloseCommand = new AsyncRelayCommand(ExecuteOpenCloseAsync);
        ClearReceivedTextCommand = new RelayCommand(_ => ClearReceivedText());
        SendCommand = new AsyncRelayCommand(ExecuteSendAsync, _ => IsOpenPort);
        ClearSendTextCommand = new RelayCommand(_ => ClearSendText());
        ExportReceivedTextCommand = new RelayCommand(_ => ExportReceivedText(), _ => !string.IsNullOrWhiteSpace(ReceivedText));
        RefreshAvailablePorts();
        _receiveTask = ConsumeMessagesAsync(_lifetimeCts.Token);
        _displayTask = FlushDisplayAsync(_lifetimeCts.Token);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? ScrollToEndRequested;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public ObservableCollection<string> AvailablePorts { get; } = new();
    public int[] BaudRates { get; } = { 110, 300, 600, 1200, 2400, 4800, 9600, 14400, 19200, 38400, 57600, 115200, 230400, 460800, 921600 };
    public int[] DataBits { get; } = { 5, 6, 7, 8 };
    public string[] StopBitsOptions { get; } = { "1", "1.5", "2" };
    public Parity[] ParityOptions { get; } = { Parity.None, Parity.Odd, Parity.Even, Parity.Mark, Parity.Space };

    public string? SelectedPort { get => _selectedPort; set => SetField(ref _selectedPort, value); }
    public int SelectedBaudRate { get => _selectedBaudRate; set => SetField(ref _selectedBaudRate, value); }
    public int SelectedDataBits { get => _selectedDataBits; set => SetField(ref _selectedDataBits, value); }
    public string SelectedStopBits { get => _selectedStopBits; set => SetField(ref _selectedStopBits, value); }
    public Parity SelectedParity { get => _selectedParity; set => SetField(ref _selectedParity, value); }
    public string ReceivedText
    {
        get => _receivedText;
        set
        {
            if (SetField(ref _receivedText, value))
                (ExportReceivedTextCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }
    public string SendText { get => _sendText; set => SetField(ref _sendText, value); }
    public string StatusMessage { get => _statusMessage; set => SetField(ref _statusMessage, value); }
    public bool ShowTimestamp { get => _showTimestamp; set => SetField(ref _showTimestamp, value); }
    public bool PauseDisplay { get => _pauseDisplay; set => SetField(ref _pauseDisplay, value); }

    public bool IsShowHex
    {
        get => _isShowHex;
        set { if (SetField(ref _isShowHex, value)) _utf8Decoder.Reset(); }
    }

    public bool IsSendHex
    {
        get => _isSendHex;
        set
        {
            if (!SetField(ref _isSendHex, value)) return;
            if (value && !ValidateHexInput(SendText))
            {
                MessageBox.Show("当前内容包含非十六进制字符，已自动清空", "提示");
                SendText = string.Empty;
            }
        }
    }

    public bool IsAddCRC { get => _isAddCrc; set => SetField(ref _isAddCrc, value); }
    public bool IsOpenPort => _serialService.State == SerialConnectionState.Connected;
    public bool CanConfigure => _serialService.State is SerialConnectionState.Disconnected or SerialConnectionState.Faulted;
    public string OpenCloseText => IsOpenPort ? "断开串口" : "打开串口";
    public string ConnectionStatus => _serialService.State switch
    {
        SerialConnectionState.Connected => $"● 已连接 {_serialService.CurrentPortName}",
        SerialConnectionState.Connecting => "● 正在连接",
        SerialConnectionState.Disconnecting => "● 正在断开",
        SerialConnectionState.Faulted => "● 连接异常",
        _ => "○ 未连接"
    };
    public long ReceivedBytesCount => _serialService.ReceivedBytesCount;
    public long SentBytesCount => _serialService.SentBytesCount;

    public ICommand RefreshPortsCommand { get; }
    public AsyncRelayCommand OpenCloseCommand { get; }
    public ICommand ClearReceivedTextCommand { get; }
    public AsyncRelayCommand SendCommand { get; }
    public ICommand ClearSendTextCommand { get; }
    public ICommand ExportReceivedTextCommand { get; }

    private void RefreshAvailablePorts()
    {
        var previous = SelectedPort;
        AvailablePorts.Clear();
        foreach (var port in _serialService.GetPortNames()) AvailablePorts.Add(port);
        SelectedPort = previous is not null && AvailablePorts.Contains(previous) ? previous : AvailablePorts.FirstOrDefault();
    }

    private async Task ExecuteOpenCloseAsync(object? _)
    {
        try
        {
            if (IsOpenPort)
            {
                await _serialService.CloseAsync();
                StatusMessage = "串口已关闭";
                RefreshAvailablePorts();
                return;
            }
            if (string.IsNullOrWhiteSpace(SelectedPort))
            {
                MessageBox.Show("请选择串口", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var settings = new SerialPortSettings(SelectedPort, SelectedBaudRate, SelectedDataBits,
                ParseStopBits(SelectedStopBits), SelectedParity);
            await _serialService.OpenAsync(settings);
            StatusMessage = $"串口 {SelectedPort} 已打开";
        }
        catch (Exception ex)
        {
            StatusMessage = $"串口操作错误：{ex.Message}";
            MessageBox.Show(StatusMessage, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ExecuteSendAsync(object? _)
    {
        try
        {
            var data = ConvertSendText();
            if (data is null) return;
            if (IsAddCRC) data = [.. data, .. Crc16Modbus.Calculate(data)];
            await _serialService.WriteAsync(data);
            StatusMessage = $"已发送 {data.Length} 字节";
        }
        catch (Exception ex)
        {
            StatusMessage = $"发送错误：{ex.Message}";
            MessageBox.Show(StatusMessage, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private byte[]? ConvertSendText()
    {
        if (!IsSendHex) return Encoding.UTF8.GetBytes(SendText);
        if (!ValidateHexInput(SendText))
        {
            MessageBox.Show("十六进制内容只能包含 0-9、A-F 和空格", "输入错误");
            return null;
        }
        var hex = Regex.Replace(SendText, "\\s", string.Empty);
        if (hex.Length == 0 || hex.Length % 2 != 0)
        {
            MessageBox.Show("十六进制数据不能为空且长度必须为偶数", "输入错误");
            return null;
        }
        return Convert.FromHexString(hex);
    }

    private async Task ConsumeMessagesAsync(CancellationToken token)
    {
        try
        {
            await foreach (var message in _serialService.ReadAllAsync(token))//后台一直等待读取
            {
                string text;
                if (IsShowHex)
                {
                    text = string.Join(' ', Convert.ToHexString(message.Data).Chunk(2).Select(x => new string(x))) + Environment.NewLine;
                }
                else
                {
                    var chars = new char[_utf8Decoder.GetCharCount(message.Data, false)];
                    _utf8Decoder.GetChars(message.Data, chars, false);
                    text = new string(chars);
                }
                if (ShowTimestamp) text = $"[{message.Timestamp:HH:mm:ss.fff}] {text}";
                _displayQueue.Enqueue(text);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task FlushDisplayAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(75, token);
                if (PauseDisplay || _displayQueue.IsEmpty) continue;
                var builder = new StringBuilder();
                while (_displayQueue.TryDequeue(out var text)) builder.Append(text);
                if (builder.Length == 0) continue;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var combined = ReceivedText + builder;
                    if (combined.Length > MaxDisplayCharacters) combined = combined[^MaxDisplayCharacters..];
                    ReceivedText = combined;
                    ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
                });
            }
        }
        catch (OperationCanceledException) { }
    }

    private void OnServiceStateChanged(object? sender, EventArgs e) => RunOnUi(() =>
    {
        OnPropertyChanged(nameof(IsOpenPort));
        OnPropertyChanged(nameof(CanConfigure));
        OnPropertyChanged(nameof(OpenCloseText));
        OnPropertyChanged(nameof(ConnectionStatus));
        OpenCloseCommand.RaiseCanExecuteChanged();
        SendCommand.RaiseCanExecuteChanged();
        CommandManager.InvalidateRequerySuggested();
    });

    private void OnStatisticsChanged(object? sender, EventArgs e) => RunOnUi(() =>
    {
        OnPropertyChanged(nameof(ReceivedBytesCount));
        OnPropertyChanged(nameof(SentBytesCount));
    });

    private void OnCommunicationError(object? sender, Exception e) => RunOnUi(() =>
    {
        StatusMessage = $"串口通信异常：{e.Message}";
        RefreshAvailablePorts();
    });

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action(); else dispatcher.BeginInvoke(action);
    }

    private void ClearReceivedText()
    {
        while (_displayQueue.TryDequeue(out _)) { }
        ReceivedText = string.Empty;
        _serialService.ResetReceivedCount();
    }

    private void ClearSendText()
    {
        SendText = string.Empty;
        _serialService.ResetSentCount();
    }

    private void ExportReceivedText()
    {
        try
        {
            var content = ReceivedText;
            if (string.IsNullOrWhiteSpace(content))
            {
                MessageBox.Show("当前没有可导出的接收数据", "提示");
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Markdown 文件 (*.md)|*.md|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                DefaultExt = ".md",
                FileName = $"SerialLog_{DateTime.Now:yyyyMMdd_HHmmss}.md"
            };

            if (dialog.ShowDialog() != true) return;

            var markdown = new StringBuilder();
            markdown.AppendLine($"# 串口接收数据日志");
            markdown.AppendLine();
            markdown.AppendLine($"- **导出时间**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            markdown.AppendLine($"- **端口**: {_serialService.CurrentPortName ?? "未连接"}");
            markdown.AppendLine($"- **显示模式**: {(IsShowHex ? "HEX" : "文本")}");
            markdown.AppendLine($"- **时间戳**: {(ShowTimestamp ? "已启用" : "未启用")}");
            markdown.AppendLine();
            markdown.AppendLine("## 数据内容");
            markdown.AppendLine();
            markdown.AppendLine("```");
            markdown.Append(content);
            if (!content.EndsWith(Environment.NewLine)) markdown.AppendLine();
            markdown.AppendLine("```");

            File.WriteAllText(dialog.FileName, markdown.ToString(), Encoding.UTF8);
            StatusMessage = $"已导出到：{dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"导出失败：{ex.Message}";
            MessageBox.Show(StatusMessage, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static bool ValidateHexInput(string text)
        => string.IsNullOrWhiteSpace(text) || Regex.IsMatch(text, "^[0-9A-Fa-f\\s]*$");
    private static StopBits ParseStopBits(string value) => value switch { "1.5" => StopBits.OnePointFive, "2" => StopBits.Two, _ => StopBits.One };

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetimeCts.Cancel();
        try { await Task.WhenAll(_receiveTask, _displayTask); } catch (OperationCanceledException) { }
        _serialService.StateChanged -= OnServiceStateChanged;
        _serialService.StatisticsChanged -= OnStatisticsChanged;
        _serialService.CommunicationError -= OnCommunicationError;
        await _serialService.DisposeAsync();
        _lifetimeCts.Dispose();
    }
}
