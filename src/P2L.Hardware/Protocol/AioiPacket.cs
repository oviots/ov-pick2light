using System.Text;
using P2L.Shared.Constants;

namespace P2L.Hardware.Protocol;

/// <summary>
/// Represents a parsed or constructed Aioi Ethernet communication packet.
///
/// Ethernet frame structure (all fields ASCII except STX/ETX):
///   STX(1) | SeqNo(3) | DataLen(4) | Command(1–1015) | ETX(1)
///
/// Reference: L-Pick Controller Command Reference V4.1, §Ethernet Communication
/// </summary>
public sealed class AioiPacket
{
    public int    SequenceNo  { get; init; }
    public string Command     { get; init; } = string.Empty;

    /// <summary>
    /// Serializes this packet into the raw bytes to transmit over TCP.
    /// DataLen = length of Command string in ASCII chars.
    /// </summary>
    public byte[] ToBytes()
    {
        var cmdBytes = Encoding.ASCII.GetBytes(Command);
        var frame    = new byte[1 + 3 + 4 + cmdBytes.Length + 1];
        int pos      = 0;

        frame[pos++] = AioiConstants.STX;

        // SeqNo: 3-digit zero-padded ASCII
        var seqStr = (SequenceNo % 1000).ToString("D3");
        Encoding.ASCII.GetBytes(seqStr, 0, 3, frame, pos);
        pos += 3;

        // DataLen: 4-digit zero-padded ASCII (= command length)
        var lenStr = cmdBytes.Length.ToString("D4");
        Encoding.ASCII.GetBytes(lenStr, 0, 4, frame, pos);
        pos += 4;

        // Command body
        cmdBytes.CopyTo(frame, pos);
        pos += cmdBytes.Length;

        frame[pos] = AioiConstants.ETX;
        return frame;
    }

    /// <summary>
    /// Attempts to parse a single packet from a buffer.
    /// Returns null if the buffer does not yet contain a complete packet.
    /// <paramref name="consumed"/> is set to the number of bytes consumed on success.
    /// </summary>
    public static AioiPacket? TryParse(ReadOnlySpan<byte> buffer, out int consumed)
    {
        consumed = 0;

        int stxIndex = -1;
        for (int i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] == AioiConstants.STX) { stxIndex = i; break; }
        }
        if (stxIndex < 0) return null;

        // Minimum: STX(1) + SeqNo(3) + DataLen(4) + Cmd(>=1) + ETX(1) = 10 bytes
        var frame = buffer[stxIndex..];
        if (frame.Length < 10) return null;

        // Parse SeqNo
        if (!int.TryParse(Encoding.ASCII.GetString(frame[1..4]), out int seqNo))
            return null;

        // Parse DataLen
        if (!int.TryParse(Encoding.ASCII.GetString(frame[4..8]), out int dataLen))
            return null;

        int totalExpected = 1 + 3 + 4 + dataLen + 1;
        if (frame.Length < totalExpected) return null;

        if (frame[totalExpected - 1] != AioiConstants.ETX) return null;

        var command = Encoding.ASCII.GetString(frame[8..(8 + dataLen)]);
        consumed = stxIndex + totalExpected;

        return new AioiPacket { SequenceNo = seqNo, Command = command };
    }
}
