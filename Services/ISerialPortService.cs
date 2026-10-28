using SerialAssistant.Model;

namespace SerialAssistant.Services;

public interface ISerialPortService : IAsyncDisposable
{
    SerialConnectionState State { get; }
    string? CurrentPortName { get; }
    long ReceivedBytesCount { get; }
    long SentBytesCount { get; }
    event EventHandler? StateChanged;
    event EventHandler? StatisticsChanged;
    event EventHandler<Exception>? CommunicationError;

    string[] GetPortNames();
    Task OpenAsync(SerialPortSettings settings, CancellationToken cancellationToken = default);
    Task CloseAsync(CancellationToken cancellationToken = default);
    Task WriteAsync(byte[] data, CancellationToken cancellationToken = default);
    IAsyncEnumerable<SerialReceiveMessage> ReadAllAsync(CancellationToken cancellationToken = default);
    void ResetReceivedCount();
    void ResetSentCount();
}
