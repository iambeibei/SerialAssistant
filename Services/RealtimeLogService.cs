using SerialAssistant.Infrastructure;
using SerialAssistant.Model;
using System.IO;
using System.Text;
using System.Threading.Channels;

namespace SerialAssistant.Services;

/// <summary>
/// 单次会话的实时日志写入器。每次开启日志创建一个新实例，停止后不可复用，
/// 从而避免 Channel 复用、重启竞态与半关闭状态机。
/// 入队是非阻塞的，文件 I/O 全部在后台任务中执行。
/// </summary>
public sealed class RealtimeLogService : IAsyncDisposable
{
    /// <summary>队列容量。写盘偶尔抖动能缓冲数千条消息，满了则丢弃并计数，绝不阻塞接收线程。</summary>
    private const int QueueCapacity = 8192;

    /// <summary>单批最大字符数，防止一次性拼接超大文本。</summary>
    private const int MaxBatchChars = 256 * 1024;

    private static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>距上次真正刷盘超过该间隔，或累计写入超过 <see cref="FsyncBytes"/>，就执行一次 Flush(true)。</summary>
    private static readonly TimeSpan FsyncInterval = TimeSpan.FromSeconds(2);

    private const long FsyncBytes = 1024 * 1024;

    private readonly Channel<LogEntry> _queue;
    private readonly object _stopSync = new();

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

    /// <summary>幂等停止：关闭队列、排空、刷盘、释放句柄。异常只记录不抛出，避免阻塞关窗。</summary>
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

    private async Task PumpAsync()
    {
        try
        {
            var reader = _queue.Reader;
            var batch = new StringBuilder(MaxBatchChars);

            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Length < MaxBatchChars && reader.TryRead(out var entry))
                {
                    AppendEntry(batch, entry);
                }

                if (batch.Length > 0)
                {
                    await _writer!.WriteAsync(batch.ToString()).ConfigureAwait(false);
                    // 先交给系统缓冲，保证 200ms 内对其它读取者可见；真正刷盘按间隔执行
                    await _writer.FlushAsync().ConfigureAwait(false);
                    _charsSinceFsync += batch.Length;
                }

                if (_charsSinceFsync > 0 &&
                    (_charsSinceFsync >= FsyncBytes / 2 || DateTime.UtcNow - _lastFsyncUtc >= FsyncInterval))
                {
                    // 后台线程上的同步刷盘：FlushAsync 不支持 flushToDisk 语义
                    _stream!.Flush(true);
                    _charsSinceFsync = 0;
                    _lastFsyncUtc = DateTime.UtcNow;
                }

                await Task.Delay(BatchInterval).ConfigureAwait(false);
            }

            // 正常停止：队列已排空，最后做一次完整落盘
            await _writer!.FlushAsync().ConfigureAwait(false);
            _stream!.Flush(true);
        }
        catch (Exception ex)
        {
            HandleFault(ex);
        }
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

    private static void AppendEntry(StringBuilder builder, LogEntry entry)
    {
        builder.Append('[')
            .Append(entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(entry.Direction == LogDirection.Rx ? "] [RX] " : "] [TX] ")
            .Append(entry.Payload);

        // 流式文本不保证以换行结尾，补一个保证下一条日志不会粘在同一行
        if (entry.Payload[^1] != '\n') builder.AppendLine();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private readonly record struct LogEntry(LogDirection Direction, DateTimeOffset Timestamp, string Payload);
}
