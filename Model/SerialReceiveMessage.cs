namespace SerialAssistant.Model;

public sealed record SerialReceiveMessage(
    byte[] Data,
    DateTimeOffset Timestamp,
    string PortName);
