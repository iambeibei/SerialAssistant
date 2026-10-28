using SerialAssistant.Model;
using System.IO;
using System.IO.Ports;
using System.Threading.Channels;

namespace SerialAssistant.Services;

public sealed class SerialPortService : ISerialPortService
{
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Channel<SerialReceiveMessage> _receiveQueue = Channel.CreateUnbounded<SerialReceiveMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private SerialPort? _serialPort;
    private CancellationTokenSource? _monitorCts;
    private long _receivedBytesCount;
    private long _sentBytesCount;
    private int _faultHandling;
    private bool _disposed;

    public SerialConnectionState State { get; private set; } = SerialConnectionState.Disconnected;
    public string? CurrentPortName { get; private set; }
    public long ReceivedBytesCount => Interlocked.Read(ref _receivedBytesCount);
    public long SentBytesCount => Interlocked.Read(ref _sentBytesCount);

    public event EventHandler? StateChanged;
    public event EventHandler? StatisticsChanged;
    public event EventHandler<Exception>? CommunicationError;

    public string[] GetPortNames() => SerialPort.GetPortNames().OrderBy(x => x).ToArray();

    public async Task OpenAsync(SerialPortSettings settings, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (State is SerialConnectionState.Connected or SerialConnectionState.Connecting)
                return;

            SetState(SerialConnectionState.Connecting);
            var serial = new SerialPort(settings.PortName, settings.BaudRate, settings.Parity,
                settings.DataBits, settings.StopBits)
            {
                Handshake = settings.Handshake,
                ReadTimeout = 500,
                WriteTimeout = 1000
            };

            try
            {
                serial.DataReceived += OnDataReceived;
                serial.ErrorReceived += OnErrorReceived;
                serial.Open();
                _serialPort = serial;
                CurrentPortName = settings.PortName;
                Interlocked.Exchange(ref _faultHandling, 0);
                StartPortMonitor(settings.PortName);
                SetState(SerialConnectionState.Connected);
            }
            catch
            {
                serial.DataReceived -= OnDataReceived;
                serial.ErrorReceived -= OnErrorReceived;
                serial.Dispose();
                CurrentPortName = null;
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
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await CloseCoreAsync(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task CloseCoreAsync(bool faulted)
    {
        if (_serialPort is null && State == SerialConnectionState.Disconnected)
            return;

        SetState(faulted ? SerialConnectionState.Faulted : SerialConnectionState.Disconnecting);
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = null;

        var serial = Interlocked.Exchange(ref _serialPort, null);
        CurrentPortName = null;
        if (serial is not null)
        {
            serial.DataReceived -= OnDataReceived;
            serial.ErrorReceived -= OnErrorReceived;

            var cleanup = Task.Run(() =>
            {
                try
                {
                    if (serial.IsOpen) serial.Close();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    CommunicationError?.Invoke(this, ex);
                }
                finally
                {
                    try { serial.Dispose(); } catch { }
                }
            });

            // 部分 USB 串口驱动在设备被直接拔出后 Close 会长时间阻塞，不能让 UI 一起阻塞。
            await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromSeconds(1.5)));
        }

        SetState(SerialConnectionState.Disconnected);
    }

    public async Task WriteAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        if (data.Length == 0) return;
        var serial = _serialPort;
        if (State != SerialConnectionState.Connected || serial is null)
            throw new InvalidOperationException("串口未连接");

        try
        {
            await serial.BaseStream.WriteAsync(data, cancellationToken);
            Interlocked.Add(ref _sentBytesCount, data.Length);
            StatisticsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (IsCommunicationException(ex))
        {
            BeginFaultHandling(ex);
            throw;
        }
    }

    public IAsyncEnumerable<SerialReceiveMessage> ReadAllAsync(CancellationToken cancellationToken = default)
        => _receiveQueue.Reader.ReadAllAsync(cancellationToken);

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        var serial = _serialPort;
        if (serial is null || State != SerialConnectionState.Connected) return;

        try
        {
            var count = serial.BytesToRead;
            if (count <= 0) return;
            var buffer = new byte[count];
            var read = serial.Read(buffer, 0, count);
            if (read <= 0) return;
            if (read != buffer.Length) Array.Resize(ref buffer, read);

            Interlocked.Add(ref _receivedBytesCount, read);
            StatisticsChanged?.Invoke(this, EventArgs.Empty);
            _receiveQueue.Writer.TryWrite(new SerialReceiveMessage(buffer, DateTimeOffset.Now,
                CurrentPortName ?? string.Empty));
        }
        catch (Exception ex) when (IsCommunicationException(ex))
        {
            BeginFaultHandling(ex);
        }
    }

    private void OnErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        => BeginFaultHandling(new IOException($"串口驱动报告错误：{e.EventType}"));

    private void BeginFaultHandling(Exception exception)
    {
        if (Interlocked.Exchange(ref _faultHandling, 1) != 0) return;
        CommunicationError?.Invoke(this, exception);
        _ = Task.Run(async () =>
        {
            await _lifecycleGate.WaitAsync();
            try { await CloseCoreAsync(true); }
            finally { _lifecycleGate.Release(); }
        });
    }

    private void StartPortMonitor(string portName)
    {
        _monitorCts?.Cancel();
        _monitorCts = new CancellationTokenSource();
        var token = _monitorCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(1000, token);
                    if (!GetPortNames().Contains(portName, StringComparer.OrdinalIgnoreCase))
                    {
                        BeginFaultHandling(new IOException($"设备 {portName} 已断开"));
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { CommunicationError?.Invoke(this, ex); }
        }, token);
    }

    private static bool IsCommunicationException(Exception ex)
        => ex is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException;

    private void SetState(SerialConnectionState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResetReceivedCount()
    {
        Interlocked.Exchange(ref _receivedBytesCount, 0);
        StatisticsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResetSentCount()
    {
        Interlocked.Exchange(ref _sentBytesCount, 0);
        StatisticsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await CloseAsync();
        _disposed = true;
        _receiveQueue.Writer.TryComplete();
        _lifecycleGate.Dispose();
    }
}
