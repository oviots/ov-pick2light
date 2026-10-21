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
        // Latin1 preserves all byte values 0–255 (required for binary mode-array bytes).
        var cmdBytes = Encoding.Latin1.GetBytes(Command);
        if (SequenceNo is < 0 or > AioiConstants.SeqNoMax)
            throw new ArgumentOutOfRangeException(nameof(SequenceNo));
        if (cmdBytes.Length is < 1 or > AioiConstants.MaxCommandLength)
            throw new ArgumentOutOfRangeException(nameof(Command), "Aioi commands must contain 1–1015 bytes.");
        if (cmdBytes.Contains(AioiConstants.STX) || cmdBytes.Contains(AioiConstants.ETX))
            throw new ArgumentException("Aioi command data cannot contain framing bytes.", nameof(Command));
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
    /// Returns null if no complete valid packet is available. In that case,
    /// <paramref name="consumed"/> reports invalid bytes that can be discarded;
    /// an incomplete valid frame is retained for the next read.
    /// </summary>
    public static AioiPacket? TryParse(ReadOnlySpan<byte> buffer, out int consumed)
    {
        consumed = 0;

        int offset = 0;
        while (offset < buffer.Length)
        {
            int next = buffer[offset..].IndexOf(AioiConstants.STX);
            if (next < 0) { consumed = buffer.Length; return null; }
            int stxIndex = offset + next;
            var frame = buffer[stxIndex..];
            consumed = stxIndex;
            if (frame.Length < 8) return null;

            bool digits = true;
            for (int i = 1; i < 8; i++)
                digits &= frame[i] is >= (byte)'0' and <= (byte)'9';
            int dataLen = digits ? int.Parse(Encoding.ASCII.GetString(frame[4..8])) : 0;
            if (!digits || dataLen is < 1 or > AioiConstants.MaxCommandLength)
            {
                offset = stxIndex + 1;
                continue;
            }

            int totalExpected = 9 + dataLen;
            // STX cannot occur inside command data; it begins a fresh frame.
            int nestedStx = frame[1..Math.Min(frame.Length, totalExpected)].IndexOf(AioiConstants.STX);
            if (nestedStx >= 0)
            {
                offset = stxIndex + 1 + nestedStx;
                continue;
            }
            if (frame.Length < totalExpected) return null;
            if (frame[totalExpected - 1] != AioiConstants.ETX)
            {
                offset = stxIndex + 1;
                continue;
            }

            int seqNo = int.Parse(Encoding.ASCII.GetString(frame[1..4]));
            var command = Encoding.Latin1.GetString(frame[8..(8 + dataLen)]);
            consumed = stxIndex + totalExpected;
            return new AioiPacket { SequenceNo = seqNo, Command = command };
        }
        consumed = buffer.Length;
        return null;
    }
}
