using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SerialAssistant.Infrastructure;
using SerialAssistant.Model;
using SerialAssistant.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Threading;

namespace SerialAssistant.ViewModel;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    /// <summary>显示缓冲上限，超过即触发一次裁剪。</summary>
    private const int MaxDisplayChars = 1_000_000;

    /// <summary>裁剪目标长度。一次裁掉 20 万字符，避免每次追加都退化成 O(n)。</summary>
    private const int TrimTargetChars = 800_000;

    /// <summary>单次刷新最多拼装的字符数，防止一次 AppendText 巨型文本卡住 UI。</summary>
    private const int MaxCharsPerFlush = 64 * 1024;

    private const int DisplayQueueCapacity = 2048;

    private readonly ISerialPortService _serialService;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Channel<string> _displayQueue;
    private readonly StringBuilder _displayBuffer = new(MaxDisplayChars + 4096);
    private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();
    private readonly DispatcherTimer _uiTimer;
    private readonly Task _receiveTask;
    private readonly Task _displayTask;

    // 由 UI 线程写入、后台消费线程读取，用 volatile 保证可见性。
    // Decoder 本身非线程安全，因此只在消费线程内使用，切换模式时通过 _resetDecoderPending 传递。
    private volatile bool _paused;
    private volatile bool _hexDisplay;
    private volatile bool _timestampEnabled;
    private volatile bool _resetDecoderPending;

    private long _pausedDroppedBytes;
    private int _displayGeneration;
    private bool _canExport;
    private long _lastReceivedBytes;
    private long _lastSentBytes;
    private long _lastDroppedBytes;
    private int _disposed;

    // 实时日志：每次开启创建新实例，停止后置空。接收/发送线程通过 Volatile 读取。
    private RealtimeLogService? _logService;
    private readonly Func<string, string?> _logPathPicker;
    private string? _lastLogPath;

    [ObservableProperty] private bool _isLogSaving;
    [ObservableProperty] private string _logFilePath = string.Empty;

    [ObservableProperty] private string? _selectedPort;
    [ObservableProperty] private int _selectedBaudRate = 115200;
    [ObservableProperty] private int _selectedDataBits = 8;
    [ObservableProperty] private string _selectedStopBits = "1";
    [ObservableProperty] private Parity _selectedParity = Parity.None;
    [ObservableProperty] private string _sendText = string.Empty;
    [ObservableProperty] private string _statusMessage = "就绪";
    [ObservableProperty] private bool _isShowHex;
    [ObservableProperty] private bool _isSendHex;
    [ObservableProperty] private bool _isAddCRC;
    [ObservableProperty] private bool _showTimestamp;
    [ObservableProperty] private bool _pauseDisplay;

    public MainViewModel() : this(new SerialPortService()) { }

    internal MainViewModel(ISerialPortService serialService, Func<string, string?>? logPathPicker = null)
    {
        _serialService = serialService;
        _logPathPicker = logPathPicker ?? DefaultLogPathPicker;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        _displayQueue = Channel.CreateBounded<string>(new BoundedChannelOptions(DisplayQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        _serialService.StateChanged += OnServiceStateChanged;
        _serialService.CommunicationError += OnCommunicationError;

        _uiTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _uiTimer.Tick += OnUiTimerTick;

        RefreshAvailablePorts();

        _receiveTask = ConsumeMessagesAsync(_lifetimeCts.Token);
        _displayTask = FlushDisplayAsync(_lifetimeCts.Token);

        _uiTimer.Start();
    }

    /// <summary>请求向接收区追加文本。始终在 UI 线程触发。</summary>
    public event Action<string>? DisplayAppendRequested;

    /// <summary>请求用新文本整体替换接收区（缓冲裁剪后才会发生）。始终在 UI 线程触发。</summary>
    public event Action<string>? DisplayResetRequested;

    /// <summary>请求清空接收区。始终在 UI 线程触发。</summary>
    public event Action? DisplayCleared;

    public ObservableCollection<string> AvailablePorts { get; } = new();
    public int[] BaudRates { get; } = [110, 300, 600, 1200, 2400, 4800, 9600, 14400, 19200, 38400, 57600, 115200, 230400, 460800, 921600];
    public int[] DataBits { get; } = [5, 6, 7, 8];
    public string[] StopBitsOptions { get; } = ["1", "1.5", "2"];
    public Parity[] ParityOptions { get; } = [Parity.None, Parity.Odd, Parity.Even, Parity.Mark, Parity.Space];

    public bool IsOpenPort => _serialService.State == SerialConnectionState.Connected;
    public bool CanConfigure => _serialService.State is SerialConnectionState.Disconnected or SerialConnectionState.Faulted;

    /// <summary>供视图按状态着色（状态胶囊的指示点）。</summary>
    public SerialConnectionState ConnectionState => _serialService.State;

    public string OpenCloseText => IsOpenPort ? "断开串口" : "打开串口";

    public string ConnectionStatus => _serialService.State switch
    {
        SerialConnectionState.Connected => $"已连接 {_serialService.CurrentPortName}",
        SerialConnectionState.Connecting => "正在连接",
        SerialConnectionState.Disconnecting => "正在断开",
        SerialConnectionState.Faulted => "连接异常",
        _ => "未连接"
    };

    public long ReceivedBytesCount => _serialService.ReceivedBytesCount;
    public long SentBytesCount => _serialService.SentBytesCount;

    /// <summary>导出用快照，与接收区显示内容保持一致。</summary>
    public string GetReceivedTextSnapshot() => _displayBuffer.ToString();

    private bool CanToggleConnection => _serialService.State
        is SerialConnectionState.Disconnected or SerialConnectionState.Connected or SerialConnectionState.Faulted;

    private bool CanSend => IsOpenPort;
    private bool CanExport => _canExport;

    public string LogButtonText => IsLogSaving ? "停止日志" : "日志保存";

    public string LogButtonToolTip => IsLogSaving
        ? $"正在写入：{LogFilePath}\n点击停止并保存"
        : "开启后持续写入接收与发送数据";

    /// <summary>开启时弹出路径设置，再次点击停止并落盘。AsyncRelayCommand 默认禁止并发，快速重复点击不会重入。</summary>
    [RelayCommand]
    private async Task ToggleLogSavingAsync()
    {
        if (IsLogSaving)
        {
            await StopLogSavingAsync();
            return;
        }

        await StartLogSavingAsync();
    }

    private async Task StartLogSavingAsync()
    {
        var initialPath = string.IsNullOrWhiteSpace(_lastLogPath) ? GetDefaultLogPath() : _lastLogPath;
        var path = _logPathPicker(initialPath);
        if (string.IsNullOrWhiteSpace(path)) return;   // 用户取消

        var service = new RealtimeLogService();
        try
        {
            var sessionInfo = $"端口：{_serialService.CurrentPortName ?? "未连接"}；" +
                              $"显示模式：{(IsShowHex ? "HEX" : "文本")}；时间戳：{(ShowTimestamp ? "开" : "关")}";
            await service.StartAsync(path, sessionInfo);
        }
        catch (Exception ex)
        {
            CrashLogger.Log("RealtimeLog.Start", ex);
            await service.DisposeAsync();
            StatusMessage = $"日志开启失败：{ex.Message}";
            MessageBox.Show(StatusMessage, "日志保存", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        service.Faulted += OnLogFaulted;
        _lastLogPath = path;
        LogFilePath = path;
        Volatile.Write(ref _logService, service);

        IsLogSaving = true;
        OnPropertyChanged(nameof(LogButtonText));
        OnPropertyChanged(nameof(LogButtonToolTip));
        StatusMessage = $"日志保存中：{path}";
    }

    private async Task StopLogSavingAsync()
    {
        var service = Volatile.Read(ref _logService);
        Volatile.Write(ref _logService, null);

        IsLogSaving = false;
        OnPropertyChanged(nameof(LogButtonText));
        OnPropertyChanged(nameof(LogButtonToolTip));

        if (service is null) return;

        service.Faulted -= OnLogFaulted;
        // 先复位状态再等待落盘，接收线程此后不会再入队；服务内部幂等，故障路径重复停止也安全
        await service.StopAsync();

        var dropped = service.DroppedEntries;
        StatusMessage = dropped > 0
            ? $"日志已保存：{LogFilePath}（写入队列溢出，丢弃 {dropped} 条）"
            : $"日志已保存：{LogFilePath}";
    }

    private void OnLogFaulted(object? sender, Exception e) => RunOnUi(async () =>
    {
        if (!IsLogSaving) return;   // 手动停止过程中的迟到故障不再打扰用户

        await StopLogSavingAsync();
        StatusMessage = $"日志写入失败，已自动停止：{e.Message}";
        MessageBox.Show(StatusMessage, "日志保存", MessageBoxButton.OK, MessageBoxImage.Error);
    });

    private static string GetDefaultLogPath()
    {
        var directory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            directory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return Path.Combine(directory, $"SerialLog_{DateTime.Now:yyyyMMdd_HHmmss}.log");
    }

    private static string? DefaultLogPathPicker(string initialPath)
    {
        var dialog = new LogPathDialog(initialPath);
        if (Application.Current?.MainWindow is { } owner) dialog.Owner = owner;
        return dialog.ShowDialog() == true ? dialog.FilePath : null;
    }

    [RelayCommand(CanExecute = nameof(CanConfigure))]
    private void RefreshPorts() => RefreshAvailablePorts();

    [RelayCommand(CanExecute = nameof(CanToggleConnection), FlowExceptionsToTaskScheduler = true)]
    private async Task OpenCloseAsync()
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

    [RelayCommand(CanExecute = nameof(CanSend), FlowExceptionsToTaskScheduler = true)]
    private async Task SendAsync()
    {
        try
        {
            var data = ConvertSendText();
            if (data is null) return;

            if (data.Length == 0)
            {
                StatusMessage = "发送内容为空";
                return;
            }

            if (IsAddCRC) data = [.. data, .. Crc16Modbus.Calculate(data)];
            await _serialService.WriteAsync(data);
            // 串口真正写入成功才记日志，data 已包含可选的 CRC
            Volatile.Read(ref _logService)?.TryEnqueue(LogDirection.Tx, DateTimeOffset.Now,
                IsSendHex ? FormatHex(data) : Encoding.UTF8.GetString(data));
            StatusMessage = $"已发送 {data.Length} 字节";
        }
        catch (Exception ex)
        {
            StatusMessage = $"发送错误：{ex.Message}";
            MessageBox.Show(StatusMessage, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void ClearReceivedText()
    {
        Interlocked.Increment(ref _displayGeneration);
        while (_displayQueue.Reader.TryRead(out _)) { }

        _displayBuffer.Clear();
        _canExport = false;
        ExportReceivedTextCommand.NotifyCanExecuteChanged();
        DisplayCleared?.Invoke();

        _serialService.ResetReceivedCount();
        Interlocked.Exchange(ref _pausedDroppedBytes, 0);
        _lastReceivedBytes = 0;
        _lastDroppedBytes = 0;
        OnPropertyChanged(nameof(ReceivedBytesCount));
    }

    [RelayCommand]
    private void ClearSendText() => SendText = string.Empty;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void ExportReceivedText()
    {
        try
        {
            var content = GetReceivedTextSnapshot();
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
            markdown.AppendLine("# 串口接收数据日志");
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
            if (!content.EndsWith(Environment.NewLine, StringComparison.Ordinal)) markdown.AppendLine();
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

    partial void OnIsShowHexChanged(bool value)
    {
        _hexDisplay = value;
        // 不在 UI 线程碰 Decoder，交给消费线程在下一帧自行 Reset
        _resetDecoderPending = true;
    }

    partial void OnIsSendHexChanged(bool value)
    {
        if (!value || ValidateHexInput(SendText)) return;
        SendText = string.Empty;
        StatusMessage = "当前内容包含非十六进制字符，已自动清空";
    }

    partial void OnShowTimestampChanged(bool value) => _timestampEnabled = value;

    partial void OnPauseDisplayChanged(bool value)
    {
        _paused = value;
        if (value) return;

        Interlocked.Exchange(ref _pausedDroppedBytes, 0);
        // 恢复后不能继续挂着"已丢弃 N 字节"的旧提示
        if (StatusMessage.StartsWith("暂停显示中", StringComparison.Ordinal)) StatusMessage = "已恢复显示";
    }

    private void RefreshAvailablePorts()
    {
        var previous = SelectedPort;
        AvailablePorts.Clear();
        foreach (var port in _serialService.GetPortNames()) AvailablePorts.Add(port);
        SelectedPort = previous is not null && AvailablePorts.Contains(previous) ? previous : AvailablePorts.FirstOrDefault();
    }

    private byte[]? ConvertSendText()
    {
        if (!IsSendHex) return Encoding.UTF8.GetBytes(SendText);

        var hex = new StringBuilder(SendText.Length);
        foreach (var ch in SendText)
        {
            if (char.IsWhiteSpace(ch)) continue;
            if (!Uri.IsHexDigit(ch))
            {
                MessageBox.Show("十六进制内容只能包含 0-9、A-F 和空格", "输入错误");
                return null;
            }
            hex.Append(ch);
        }

        if (hex.Length == 0 || hex.Length % 2 != 0)
        {
            MessageBox.Show("十六进制数据不能为空且长度必须为偶数", "输入错误");
            return null;
        }

        return Convert.FromHexString(hex.ToString());
    }

    private async Task ConsumeMessagesAsync(CancellationToken token)
    {
        var lastChar = '\0';

        while (!token.IsCancellationRequested)
        {
            try
            {
                await foreach (var message in _serialService.ReadAllAsync(token).ConfigureAwait(false))
                {
                    var log = Volatile.Read(ref _logService);

                    // 暂停且未记录日志时保留零解码开销：直接丢弃，绝不入显示队列，
                    // 否则恢复瞬间要一次性拼接数十 MB 文本。
                    if (_paused && log is null)
                    {
                        Interlocked.Add(ref _pausedDroppedBytes, message.Data.Length);
                        continue;
                    }

                    string text;
                    if (_hexDisplay)
                    {
                        text = FormatHex(message.Data) + Environment.NewLine;
                    }
                    else
                    {
                        if (_resetDecoderPending)
                        {
                            _resetDecoderPending = false;
                            _utf8Decoder.Reset();
                        }

                        var chars = new char[_utf8Decoder.GetCharCount(message.Data, false)];
                        _utf8Decoder.GetChars(message.Data, chars, false);
                        text = new string(chars);
                    }

                    // 日志独立于「暂停显示」。写入的是不含显示时间戳的格式化内容，
                    // 避免与日志行自身的毫秒时间戳重复。
                    log?.TryEnqueue(LogDirection.Rx, message.Timestamp, text);

                    // 暂停只影响界面刷新，日志仍持续记录
                    if (_paused)
                    {
                        Interlocked.Add(ref _pausedDroppedBytes, message.Data.Length);
                        continue;
                    }

                    if (_timestampEnabled)
                    {
                        var stamp = $"[{message.Timestamp:HH:mm:ss.fff}] ";
                        // 上一包没有换行时先补一个，否则多包时间戳会挤在同一行
                        text = !_hexDisplay && lastChar != '\0' && lastChar != '\n'
                            ? Environment.NewLine + stamp + text
                            : stamp + text;
                    }

                    if (text.Length > 0) lastChar = text[^1];

                    if (!_displayQueue.Writer.TryWrite(text))
                    {
                        Interlocked.Add(ref _pausedDroppedBytes, text.Length);
                    }
                }

                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // 任何异常都不允许让接收管线静默死亡
                ReportPipelineError(ex);
                try
                {
                    await Task.Delay(500, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task FlushDisplayAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(75, token).ConfigureAwait(false);

                var builder = new StringBuilder();
                while (builder.Length < MaxCharsPerFlush && _displayQueue.Reader.TryRead(out var chunk))
                {
                    builder.Append(chunk);
                }

                if (builder.Length == 0) continue;

                var text = builder.ToString();
                var generation = Volatile.Read(ref _displayGeneration);

                await _dispatcher.InvokeAsync(() =>
                {
                    // 期间用户点了"清空"，这批旧数据不能又冒出来
                    if (Volatile.Read(ref _displayGeneration) != generation) return;
                    AppendToDisplay(text);
                }, DispatcherPriority.Background);
            }
        }
        catch (TaskCanceledException) { }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ReportPipelineError(ex);
        }
    }

    /// <summary>仅在 UI 线程调用。</summary>
    private void AppendToDisplay(string text)
    {
        _displayBuffer.Append(text);

        if (_displayBuffer.Length > MaxDisplayChars)
        {
            var tail = _displayBuffer.ToString(_displayBuffer.Length - TrimTargetChars, TrimTargetChars);
            _displayBuffer.Clear();
            _displayBuffer.Append(tail);
            DisplayResetRequested?.Invoke(tail);
        }
        else
        {
            DisplayAppendRequested?.Invoke(text);
        }

        if (!_canExport)
        {
            _canExport = true;
            ExportReceivedTextCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnUiTimerTick(object? sender, EventArgs e)
    {
        var received = _serialService.ReceivedBytesCount;
        if (received != _lastReceivedBytes)
        {
            _lastReceivedBytes = received;
            OnPropertyChanged(nameof(ReceivedBytesCount));
        }

        var sent = _serialService.SentBytesCount;
        if (sent != _lastSentBytes)
        {
            _lastSentBytes = sent;
            OnPropertyChanged(nameof(SentBytesCount));
        }

        var dropped = _serialService.DroppedBytesCount + Interlocked.Read(ref _pausedDroppedBytes);
        if (dropped == _lastDroppedBytes) return;

        _lastDroppedBytes = dropped;
        if (dropped <= 0) return;

        StatusMessage = _paused
            ? $"暂停显示中，已丢弃 {dropped} 字节"
            : $"接收队列溢出，已丢弃 {dropped} 字节";
    }

    private void OnServiceStateChanged(object? sender, EventArgs e) => RunOnUi(() =>
    {
        OnPropertyChanged(nameof(IsOpenPort));
        OnPropertyChanged(nameof(CanConfigure));
        OnPropertyChanged(nameof(ConnectionState));
        OnPropertyChanged(nameof(OpenCloseText));
        OnPropertyChanged(nameof(ConnectionStatus));
        OpenCloseCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
        RefreshPortsCommand.NotifyCanExecuteChanged();
    });

    private void OnCommunicationError(object? sender, Exception e) => RunOnUi(() =>
    {
        StatusMessage = $"串口通信异常：{e.Message}";
        RefreshAvailablePorts();
    });

    private void RunOnUi(Action action)
    {
        if (_dispatcher.HasShutdownStarted) return;
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        try
        {
            _dispatcher.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // 调度器已关闭，忽略
        }
    }

    private void ReportPipelineError(Exception exception)
    {
        CrashLogger.Log("ReceivePipeline", exception);
        RunOnUi(() => StatusMessage = $"接收管线异常：{exception.Message}");
    }

    private static string FormatHex(byte[] data)
    {
        if (data.Length == 0) return string.Empty;

        var builder = new StringBuilder(data.Length * 3);
        for (var i = 0; i < data.Length; i++)
        {
            if (i > 0) builder.Append(' ');
            builder.Append(data[i].ToString("X2"));
        }
        return builder.ToString();
    }

    private static bool ValidateHexInput(string text)
    {
        foreach (var ch in text)
        {
            if (!char.IsWhiteSpace(ch) && !Uri.IsHexDigit(ch)) return false;
        }
        return true;
    }

    private static StopBits ParseStopBits(string value) => value switch
    {
        "1.5" => StopBits.OnePointFive,
        "2" => StopBits.Two,
        _ => StopBits.One
    };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _uiTimer.Stop();
        _uiTimer.Tick -= OnUiTimerTick;

        try { _lifetimeCts.Cancel(); }
        catch (ObjectDisposedException) { }

        try
        {
            await Task.WhenAll(_receiveTask, _displayTask).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 后台任务异常不能阻止后续的退订与释放，否则串口句柄会泄漏
            CrashLogger.Log("ViewModel.Dispose", ex);
        }

        // 接收/显示任务已结束，日志队列不会再有新数据，此刻停止可保证完整落盘。
        // 关窗整体有 5 秒硬上限，这里再给日志 3 秒，避免慢盘拖住退出。
        var log = Volatile.Read(ref _logService);
        Volatile.Write(ref _logService, null);
        if (log is not null)
        {
            log.Faulted -= OnLogFaulted;
            try
            {
                await log.StopAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                CrashLogger.Log("ViewModel.DisposeLog", ex);
            }
        }

        _serialService.StateChanged -= OnServiceStateChanged;
        _serialService.CommunicationError -= OnCommunicationError;

        try
        {
            await _serialService.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CrashLogger.Log("ViewModel.DisposeService", ex);
        }

        _lifetimeCts.Dispose();
    }
}
