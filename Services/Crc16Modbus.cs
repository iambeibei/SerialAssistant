namespace SerialAssistant.Services;

public static class Crc16Modbus
{
    public static byte[] Calculate(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return [(byte)crc, (byte)(crc >> 8)];
    }
}
