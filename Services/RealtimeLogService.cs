using SerialAssistant.Infrastructure;
using SerialAssistant.Model;
using System.IO;
using System.Text;
using System.Threading.Channels;

namespace SerialAssistant.Services;

/// <summary>
/// 单次会话的实时日志写入器。每次开启日志创建一个新实例，停止后不可复用，
/// 从而避免 Channel 复用、重启竞态与半关闭状态机。
/// 入队是非阻塞的，文件 I/O 与行聚合全部在后台任务中执行。
/// </summary>
public sealed class RealtimeLogService : IAsyncDisposable
{
    /// <summary>队列容量。写盘偶尔抖动能缓冲数千条消息，满了则丢弃并计数，绝不阻塞接收线程。</summary>
    private const int QueueCapacity = 8192;

    /// <summary>单行接收内容上限。无换行的二进制流不能把内存与日志行无限撑大。</summary>
    private const int MaxRxLineChars = 4096;

    /// <summary>单次写出的字符上限。</summary>
    private const int MaxOutputChars = 64 * 1024;

    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>输出节流：至少间隔这么久才真正写一次文件，避免每 100ms 都产生一次 I/O。</summary>
    private static readonly TimeSpan OutputInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>接收行空闲多久后强制结束。串口一包通常远小于该值，可把多包拼回设备的一行。</summary>
    private static readonly TimeSpan RxIdleFlush = TimeSpan.FromMilliseconds(300);

    /// <summary>距上次真正刷盘超过该间隔，或累计写入超过 <see cref="FsyncBytes"/>，就执行一次 Flush(true)。</summary>
    private static readonly TimeSpan FsyncInterval = TimeSpan.FromSeconds(2);

    private const long FsyncBytes = 1024 * 1024;

    private readonly Channel<LogEntry> _queue;
    private readonly object _stopSync = new();

    // 行聚合与输出缓冲：仅后台写入任务访问，无需加锁
    private readonly StringBuilder _rxLine = new(MaxRxLineChars);
    private readonly StringBuilder _output = new(MaxOutputChars);

    private FileStream? _stream;
    private StreamWriter? _writer;
    private Task? _pumpTask;
    private Task? _stopTask;

    private int _enabled;
    private int _stopRequested;
    private int _faultRaised;
    private long _droppedEntries;

    // 仅后台写入任务访问
    private long _charsSinceFsync;
    private DateTime _lastFsyncUtc;
    private DateTime _lastOutputUtc;
    private DateTimeOffset _rxLineStart;
    private DateTime _rxLineLastAppendUtc;

    public RealtimeLogService()
    {
        _queue = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            // 接收线程与发送命令可能并发入队
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public bool IsEnabled => Volatile.Read(ref _enabled) == 1;

    public long DroppedEntries => Interlocked.Read(ref _droppedEntries);

    /// <summary>运行期写入故障。触发后服务已停止接收新数据，由订阅者负责停止并复位界面。</summary>
    public event EventHandler<Exception>? Faulted;

    /// <summary>
    /// 打开文件并写入会话头。路径非法、目录不存在或无写权限时抛出异常，
    /// 此时服务保持未开启状态，调用方可安全丢弃本实例。
    /// </summary>
    public async Task StartAsync(string filePath, string? sessionInfo, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopRequested) != 0, this);
        if (IsEnabled || _pumpTask is not null) throw new InvalidOperationException("日志会话已经启动");

        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            throw new DirectoryNotFoundException($"目录不存在：{directory}");

        var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);
        try
        {
            var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = false
            };

            var header = new StringBuilder();
            if (stream.Length > 0) header.AppendLine();
            header.AppendLine("# SerialAssistant 实时日志");
            header.AppendLine($"# 开始时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            if (!string.IsNullOrWhiteSpace(sessionInfo)) header.AppendLine($"# {sessionInfo}");
            header.AppendLine();

            await writer.WriteAsync(header.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

            _stream = stream;
            _writer = writer;
            _charsSinceFsync = 0;
            _lastFsyncUtc = DateTime.UtcNow;
            _lastOutputUtc = DateTime.UtcNow;
            _rxLineLastAppendUtc = DateTime.UtcNow;

            Volatile.Write(ref _enabled, 1);
            _pumpTask = Task.Run(PumpAsync);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>非阻塞入队。未开启、已停止或队列满时直接返回，满则累计丢弃计数。</summary>
    public void TryEnqueue(LogDirection direction, DateTimeOffset timestamp, string payload)
    {
        if (!IsEnabled || string.IsNullOrEmpty(payload)) return;

        try
        {
            if (!_queue.Writer.TryWrite(new LogEntry(direction, timestamp, payload)))
                Interlocked.Increment(ref _droppedEntries);
        }
        catch (ChannelClosedException)
        {
            // 与停止并发：本条视为未记录，无需上报
        }
    }

    /// <summary>幂等停止：关闭队列、排空、刷出未完成行、落盘、释放句柄。异常只记录不抛出，避免阻塞关窗。</summary>
    public Task StopAsync()
    {
        lock (_stopSync)
        {
            return _stopTask ??= StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        Volatile.Write(ref _stopRequested, 1);
        Volatile.Write(ref _enabled, 0);
        _queue.Writer.TryComplete();

        try
        {
            if (_pumpTask is not null) await _pumpTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CrashLogger.Log("RealtimeLog.StopPump", ex);
        }

        try
        {
            if (_writer is not null) await _writer.DisposeAsync().ConfigureAwait(false);
            if (_stream is not null) await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CrashLogger.Log("RealtimeLog.DisposeStream", ex);
        }
        finally
        {
            _writer = null;
            _stream = null;
        }
    }

    /// <summary>
    /// 固定节拍消费队列。用节拍而非 WaitToReadAsync，是因为“空闲 300ms 断行”本身就需要定时器；
    /// 同一计时器顺带承担批量写出与落盘检查，避免高频数据下反复创建延时任务。
    /// </summary>
    private async Task PumpAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TickInterval);
            var reader = _queue.Reader;

            while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var entry)) ProcessEntry(entry);

                // 没有换行但已空闲：把残行当作设备的一行输出
                if (_rxLine.Length > 0 && DateTime.UtcNow - _rxLineLastAppendUtc >= RxIdleFlush)
                    FlushRxLine(allowEmpty: false);

                // 完成检测必须同时满足“已标记完成”与“队列确实为空”，否则会丢掉尾部条目
                if (reader.Completion.IsCompleted && !reader.TryPeek(out _))
                {
                    FlushRxLine(allowEmpty: false);
                    await WriteOutputAsync(flushToDisk: true).ConfigureAwait(false);
                    return;
                }

                if (ShouldWriteOutput())
                    await WriteOutputAsync(flushToDisk: false).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            HandleFault(ex);
        }
    }

    private void ProcessEntry(LogEntry entry)
    {
        if (entry.Direction == LogDirection.Rx)
        {
            AppendRxPayload(entry.Timestamp, entry.Payload);
            return;
        }

        // 发送记录不能插进一条尚未结束的接收行中间
        FlushRxLine(allowEmpty: false);
        AppendTxPayload(entry.Timestamp, entry.Payload);
    }

    /// <summary>
    /// 接收侧按设备换行聚合：遇到 \n 才输出一条日志，跨包碎片自动拼接。
    /// 单行超过 <see cref="MaxRxLineChars"/> 时强制断开，保证内存有界。
    /// </summary>
    private void AppendRxPayload(DateTimeOffset timestamp, string payload)
    {
        if (payload.Length == 0) return;

        if (_rxLine.Length == 0) _rxLineStart = timestamp;

        var position = 0;
        for (var i = 0; i < payload.Length; i++)
        {
            if (payload[i] != '\n') continue;

            _rxLine.Append(payload, position, i - position);
            // 空行也要保留，才能还原设备原始输出节奏
            FlushRxLine(allowEmpty: true);

            position = i + 1;
            _rxLineStart = timestamp;
        }

        var remaining = payload.Length - position;
        while (remaining > 0)
        {
            var take = Math.Min(MaxRxLineChars - _rxLine.Length, remaining);
            _rxLine.Append(payload, position, take);
            position += take;
            remaining -= take;

            if (_rxLine.Length >= MaxRxLineChars) FlushRxLine(allowEmpty: false);
        }

        _rxLineLastAppendUtc = DateTime.UtcNow;
    }

    /// <summary>发送侧按一次发送为单位：多行内容逐行带前缀，不跨发送聚合。</summary>
    private void AppendTxPayload(DateTimeOffset timestamp, string payload)
    {
        if (payload.Length == 0) return;

        var start = 0;
        for (var i = 0; i < payload.Length; i++)
        {
            if (payload[i] != '\n') continue;

            WriteTxLine(timestamp, payload.AsSpan(start, i - start));
            start = i + 1;
        }

        if (start < payload.Length) WriteTxLine(timestamp, payload.AsSpan(start));
    }

    private void WriteTxLine(DateTimeOffset timestamp, ReadOnlySpan<char> line)
    {
        if (!line.IsEmpty && line[^1] == '\r') line = line[..^1];
        AppendLogLine(LogDirection.Tx, timestamp, line.ToString());
    }

    /// <summary>输出当前接收行。allowEmpty 用于保留设备的空行；空闲/超长/停止时不需要凭空造行。</summary>
    private void FlushRxLine(bool allowEmpty)
    {
        if (_rxLine.Length == 0 && !allowEmpty) return;

        // 只裁掉一个 \r，避免把 \r\n 写成 \r\r\n
        if (_rxLine.Length > 0 && _rxLine[_rxLine.Length - 1] == '\r') _rxLine.Length--;

        AppendLogLine(LogDirection.Rx, _rxLineStart, _rxLine.ToString());
        _rxLine.Clear();
    }

    private void AppendLogLine(LogDirection direction, DateTimeOffset timestamp, string line)
    {
        _output.Append('[')
            .Append(timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(direction == LogDirection.Rx ? "] [RX] " : "] [TX] ")
            .Append(line)
            .AppendLine();
    }

    private bool ShouldWriteOutput() => _output.Length >= MaxOutputChars ||
        (_output.Length > 0 && DateTime.UtcNow - _lastOutputUtc >= OutputInterval);

    private async Task WriteOutputAsync(bool flushToDisk)
    {
        if (_output.Length > 0)
        {
            var text = _output.ToString();
            _output.Clear();

            await _writer!.WriteAsync(text.AsMemory()).ConfigureAwait(false);
            // 先交给系统缓冲，保证数百毫秒内对其它读取者可见；真正刷盘按间隔执行
            await _writer.FlushAsync().ConfigureAwait(false);

            _charsSinceFsync += text.Length;
            _lastOutputUtc = DateTime.UtcNow;
        }

        if (!flushToDisk &&
            _charsSinceFsync < FsyncBytes / 2 &&
            DateTime.UtcNow - _lastFsyncUtc < FsyncInterval)
        {
            return;
        }

        // 后台线程上的同步刷盘：FlushAsync 不支持 flushToDisk 语义
        _stream!.Flush(true);
        _charsSinceFsync = 0;
        _lastFsyncUtc = DateTime.UtcNow;
    }

    private void HandleFault(Exception exception)
    {
        Volatile.Write(ref _enabled, 0);
        CrashLogger.Log("RealtimeLog.Write", exception);

        if (Interlocked.Exchange(ref _faultRaised, 1) != 0) return;
        try
        {
            Faulted?.Invoke(this, exception);
        }
        catch (Exception ex)
        {
            CrashLogger.Log("RealtimeLog.FaultedHandler", ex);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private readonly record struct LogEntry(LogDirection Direction, DateTimeOffset Timestamp, string Payload);
}
