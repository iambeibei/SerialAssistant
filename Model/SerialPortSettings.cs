using System.IO.Ports;

namespace SerialAssistant.Model;

public sealed record SerialPortSettings(
    string PortName,
    int BaudRate,
    int DataBits,
    StopBits StopBits,
    Parity Parity,
    Handshake Handshake = Handshake.None);
