using SerialAssistant.Model;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Threading.Channels;

namespace SerialAssistant.Services;

public sealed class SerialPortService : ISerialPortService
{
    /// <summary>接收队列容量。单条消息约为一次 BytesToRead 批量读取，512 条足以覆盖消费端 10 秒以上的停顿。</summary>
    private const int ReceiveQueueCapacity = 512;

    /// <summary>关闭串口的软超时：超过后保持"正在断开"并等后台补完，绝不谎报已断开。</summary>
    private static readonly TimeSpan CloseSoftTimeout = TimeSpan.FromSeconds(1.5);

    /// <summary>关闭串口的硬超时：超过后降级为故障态，避免 UI 永远停在"正在断开"。</summary>
    private static readonly TimeSpan CloseHardTimeout = TimeSpan.FromSeconds(6);

    private static readonly TimeSpan WriteHardTimeout = TimeSpan.FromSeconds(3);

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private readonly Channel<SerialReceiveMessage> _receiveQueue =
        Channel.CreateBounded<SerialReceiveMessage>(new BoundedChannelOptions(ReceiveQueueCapacity)
        {
            SingleReader = true,
            // DataReceived 可能由多个 IO 线程并发触发
            SingleWriter = false,
            // 配合 TryWrite：队列满时返回 false，可以精确统计丢弃量
            FullMode = BoundedChannelFullMode.Wait
        });

    private SerialPort? _serialPort;
    private CancellationTokenSource? _monitorCts;
    private volatile string? _currentPortName;
    private int _state = (int)SerialConnectionState.Disconnected;
    private int _disposed;
    private int _faultHandling;
    private long _receivedBytesCount;
    private long _sentBytesCount;
    private long _droppedBytesCount;

    public SerialConnectionState State => (SerialConnectionState)Volatile.Read(ref _state);
    public string? CurrentPortName => _currentPortName;
    public long ReceivedBytesCount => Interlocked.Read(ref _receivedBytesCount);
    public long SentBytesCount => Interlocked.Read(ref _sentBytesCount);
    public long DroppedBytesCount => Interlocked.Read(ref _droppedBytesCount);

    public event EventHandler? StateChanged;
    public event EventHandler<Exception>? CommunicationError;

    public string[] GetPortNames() => SerialPort.GetPortNames()
        .Distinct(StringComparer.OrdinalIgnoreCase)
        // 按数字后缀排序，否则 COM10 会排在 COM2 前面
        .OrderBy(name => name.Length > 3 && int.TryParse(name.AsSpan(3), out var index) ? index : int.MaxValue)
        .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public async Task OpenAsync(SerialPortSettings settings, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // ConfigureAwait(false) 是关键：无竞争时 WaitAsync 会同步完成，
        // 若不切换线程，后面的 serial.Open() 会继续在 UI 线程上跑，导致界面假死。
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is SerialConnectionState.Connected or SerialConnectionState.Connecting) return;
            if (State is SerialConnectionState.Disconnecting)
                throw new InvalidOperationException("串口正在断开，请稍后重试");

            SetState(SerialConnectionState.Connecting);

            SerialPort? serial = null;
            try
            {
                // 构造也在 try 内：参数非法时抛 ArgumentException 必须能被兜住，
                // 否则状态机会永久停在 Connecting，导致界面全部禁用且无法恢复。
                serial = new SerialPort(settings.PortName, settings.BaudRate, settings.Parity,
                    settings.DataBits, settings.StopBits)
                {
                    Handshake = settings.Handshake,
                    ReadTimeout = 500,
                    WriteTimeout = 1000
                };

                serial.DataReceived += OnDataReceived;
                serial.ErrorReceived += OnErrorReceived;

                // USB 转串口驱动初始化常见 100~800ms，不能占用调用线程
                await Task.Run(() => serial.Open(), cancellationToken).ConfigureAwait(false);

                Volatile.Write(ref _serialPort, serial);
                _currentPortName = settings.PortName;
                Interlocked.Exchange(ref _faultHandling, 0);
                StartPortMonitor(settings.PortName);
                SetState(SerialConnectionState.Connected);
            }
            catch (OperationCanceledException)
            {
                ReleasePort(serial);
                Volatile.Write(ref _serialPort, null);
                _currentPortName = null;
                SetState(SerialConnectionState.Disconnected);
                throw;
            }
            catch (Exception)
            {
                ReleasePort(serial);
                Volatile.Write(ref _serialPort, null);
                _currentPortName = null;
                SetState(SerialConnectionState.Faulted);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CloseCoreAsync(false).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task CloseCoreAsync(bool faulted)
    {
        var serial = Interlocked.Exchange(ref _serialPort, null);
        var monitorCts = Interlocked.Exchange(ref _monitorCts, null);

        if (serial is null && monitorCts is null && State is SerialConnectionState.Disconnected) return;

        var closingState = faulted ? SerialConnectionState.Faulted : SerialConnectionState.Disconnecting;
        SetState(closingState);
        _currentPortName = null;

        if (monitorCts is not null)
        {
            // 只 Cancel 不 Dispose：由监控任务自己在其 finally 里释放，
            // 否则任务下一轮 Task.Delay 会撞上 ObjectDisposedException 并被误报为通信异常。
            try { monitorCts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        if (serial is null)
        {
            TryFinishClose(closingState);
            return;
        }

        serial.DataReceived -= OnDataReceived;
        serial.ErrorReceived -= OnErrorReceived;

        var cleanup = Task.Run(() =>
        {
            try
            {
                if (serial.IsOpen) serial.Close();
            }
            catch (Exception ex) when (IsCommunicationException(ex))
            {
                RaiseCommunicationError(ex);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Close] 关闭串口时发生未预期异常: {ex}");
            }
            finally
            {
                try { serial.Dispose(); } catch { }
            }
        });

        // 部分 USB 串口驱动在设备被直接拔出后 Close 会长时间阻塞，不能让 UI 一起等。
        var finished = await Task.WhenAny(cleanup, Task.Delay(CloseSoftTimeout)).ConfigureAwait(false);
        if (ReferenceEquals(finished, cleanup))
        {
            TryFinishClose(closingState);
            return;
        }

        // 软超时：句柄可能仍被驱动占用。保持"正在断开"，等后台真正关完再补切，
        // 绝不在这里提前宣布 Disconnected（否则用户立刻重开必然报端口被占用）。
        _ = cleanup.ContinueWith(
            task =>
            {
                if (task.IsFaulted) Debug.WriteLine($"[Close-late] {task.Exception}");
                TryFinishClose(closingState);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        // 硬超时兜底：不能让 UI 永远停在"正在断开"。
        _ = Task.Delay(CloseHardTimeout).ContinueWith(
            _ =>
            {
                if (closingState is not SerialConnectionState.Disconnecting) return;
                if (State is not SerialConnectionState.Disconnecting) return;

                SetState(SerialConnectionState.Faulted);
                RaiseCommunicationError(new TimeoutException(
                    "串口关闭超时，端口可能仍被系统占用。请稍后重试，或拔插设备后重启程序。"));
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public async Task WriteAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0) throw new ArgumentException("发送数据不能为空", nameof(data));

        var serial = Volatile.Read(ref _serialPort);
        if (serial is null || State != SerialConnectionState.Connected)
            throw new InvalidOperationException("串口未连接");

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 拿到写锁后重新校验：等待期间可能已被故障处理关闭
            if (!ReferenceEquals(serial, Volatile.Read(ref _serialPort)) || State != SerialConnectionState.Connected)
                throw new InvalidOperationException("串口已断开");

            // 用 SerialPort.Write 而非 BaseStream.WriteAsync：前者受 WriteTimeout 约束，
            // 驱动缓冲满时最多阻塞 1 秒抛 TimeoutException，不会让发送按钮永久变灰。
            var writeTask = Task.Run(() => serial.Write(data, 0, data.Length), CancellationToken.None);
            try
            {
                await writeTask.WaitAsync(WriteHardTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException ex) when (!writeTask.IsCompleted)
            {
                // 连 WriteTimeout 都没生效 = 驱动/硬件挂死，不再干等
                BeginFaultHandling(ex);
                throw new IOException("串口写入超时，已断开连接", ex);
            }

            Interlocked.Add(ref _sentBytesCount, data.Length);
        }
        catch (TimeoutException ex)
        {
            BeginFaultHandling(ex);
            throw new IOException("串口写入超时", ex);
        }
        catch (Exception ex) when (IsCommunicationException(ex))
        {
            BeginFaultHandling(ex);
            throw;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public IAsyncEnumerable<SerialReceiveMessage> ReadAllAsync(CancellationToken cancellationToken = default)
        => _receiveQueue.Reader.ReadAllAsync(cancellationToken);

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (sender is not SerialPort serial) return;

        // 退订后事件仍可能在途。用引用比对把"自己关端口引发的 IOException"挡掉，
        // 避免用户主动断开时反而弹出"串口通信异常"。
        if (!ReferenceEquals(serial, Volatile.Read(ref _serialPort))) return;

        try
        {
            var count = serial.BytesToRead;
            if (count <= 0) return;

            var buffer = new byte[count];
            var read = serial.Read(buffer, 0, count);
            if (read <= 0) return;
            if (read != buffer.Length) Array.Resize(ref buffer, read);

            Interlocked.Add(ref _receivedBytesCount, read);

            var message = new SerialReceiveMessage(buffer, DateTimeOffset.Now, _currentPortName ?? string.Empty);
            if (!_receiveQueue.Writer.TryWrite(message))
            {
                // 消费端跟不上，丢弃并记账，绝不无界堆积
                Interlocked.Add(ref _droppedBytesCount, read);
            }
        }
        catch (TimeoutException)
        {
            // ReadTimeout 到期只是本次没读到数据，不是故障
        }
        catch (Exception ex) when (IsCommunicationException(ex))
        {
            if (ReferenceEquals(serial, Volatile.Read(ref _serialPort))) BeginFaultHandling(ex);
        }
        catch (Exception ex)
        {
            // 最后一道防线：异常一旦逃出 DataReceived 线程就是进程崩溃
            Debug.WriteLine($"[DataReceived] 未预期异常: {ex}");
        }
    }

    private void OnErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        => BeginFaultHandling(new IOException($"串口驱动报告错误：{e.EventType}"));

    private void BeginFaultHandling(Exception exception)
    {
        if (Interlocked.Exchange(ref _faultHandling, 1) != 0) return;
        RaiseCommunicationError(exception);

        _ = Task.Run(async () =>
        {
            try
            {
                await _lifecycleGate.WaitAsync().ConfigureAwait(false);
                try { await CloseCoreAsync(true).ConfigureAwait(false); }
                finally { _lifecycleGate.Release(); }
            }
            catch (ObjectDisposedException)
            {
                // 服务已释放，无事可做
            }
            catch (Exception ex)
            {
                // 必须有观察者，否则异常只会在 GC 时静默丢失
                Debug.WriteLine($"[FaultHandling] {ex}");
            }
        });
    }

    private void StartPortMonitor(string portName)
    {
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _monitorCts, cts);
        try { previous?.Cancel(); }
        catch (ObjectDisposedException) { }

        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(1000, token).ConfigureAwait(false);
                    if (!GetPortNames().Contains(portName, StringComparer.OrdinalIgnoreCase))
                    {
                        BeginFaultHandling(new IOException($"设备 {portName} 已断开"));
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                // 监控自身异常不应冒充通信故障
                Debug.WriteLine($"[PortMonitor] {ex}");
            }
            finally
            {
                cts.Dispose();
            }
        });
    }

    private static bool IsCommunicationException(Exception ex)
        => ex is IOException or TimeoutException or UnauthorizedAccessException
            or InvalidOperationException or ObjectDisposedException;

    private void SetState(SerialConnectionState state)
    {
        var previous = (SerialConnectionState)Interlocked.Exchange(ref _state, (int)state);
        if (previous == state) return;
        RaiseStateChanged();
    }

    /// <summary>仅当当前仍停留在 <paramref name="closingState"/> 时才切成已断开，避免迟到的回调覆盖新状态。</summary>
    private bool TryFinishClose(SerialConnectionState closingState)
    {
        var previous = (SerialConnectionState)Interlocked.CompareExchange(
            ref _state, (int)SerialConnectionState.Disconnected, (int)closingState);

        if (previous != closingState) return false;
        RaiseStateChanged();
        return true;
    }

    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Debug.WriteLine($"[StateChanged] 订阅者异常: {ex}"); }
    }

    private void RaiseCommunicationError(Exception exception)
    {
        try { CommunicationError?.Invoke(this, exception); }
        catch (Exception ex) { Debug.WriteLine($"[CommunicationError] 订阅者异常: {ex}"); }
    }

    private void ReleasePort(SerialPort? serial)
    {
        if (serial is null) return;

        serial.DataReceived -= OnDataReceived;
        serial.ErrorReceived -= OnErrorReceived;

        try { if (serial.IsOpen) serial.Close(); }
        catch (Exception ex) { Debug.WriteLine($"[Open] 关闭失败端口: {ex}"); }

        try { serial.Dispose(); }
        catch (Exception ex) { Debug.WriteLine($"[Open] 释放失败端口: {ex}"); }
    }

    public void ResetReceivedCount()
    {
        Interlocked.Exchange(ref _receivedBytesCount, 0);
        Interlocked.Exchange(ref _droppedBytesCount, 0);
    }

    public void ResetSentCount() => Interlocked.Exchange(ref _sentBytesCount, 0);

    public async ValueTask DisposeAsync()
    {
        // 先原子置位再干活：并发调用与中途异常都不会导致半释放
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try
        {
            await CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Dispose] 关闭串口失败: {ex}");
        }

        _receiveQueue.Writer.TryComplete();

        // _lifecycleGate / _writeGate 故意不 Dispose：
        // 释放时可能仍有 fire-and-forget 任务在 WaitAsync，Dispose 会让它们抛 ObjectDisposedException。
        // 未创建 WaitHandle 的 SemaphoreSlim 不持有非托管资源，交给 GC 是安全的。
    }
}
