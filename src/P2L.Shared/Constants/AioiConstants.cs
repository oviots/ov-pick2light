namespace P2L.Shared.Constants;

/// <summary>
/// Constants for the Aioi Systems L-PICK communication protocol.
/// Reference: L-Pick Controller Command Reference V4.1, Controller User's Manual V1.3
/// </summary>
public static class AioiConstants
{
    // Ethernet packet framing (0x02 / 0x03 must NOT appear in command data)
    public const byte STX = 0x02;
    public const byte ETX = 0x03;

    // Default gateway network settings
    public const string DefaultGatewayIp   = "192.168.1.254";
    public const int    DefaultTcpPort      = 5003;

    // Sequence number range (3-digit ASCII: "000"–"999")
    public const int SeqNoMin = 0;
    public const int SeqNoMax = 999;

    // Data length field (4-digit ASCII: length of the Command portion only)
    public const int MaxCommandLength = 1015;

    // Light module address range (4-digit BCD)
    public const int AddrMin = 1;
    public const int AddrMax = 7999;

    // Block number range (2-digit BCD, "00" = all modules)
    public const int BlockNoAll = 0;
    public const int BlockNoMax = 99;

    // Display data is always 5 ASCII characters (right-aligned, space-padded)
    public const int DisplayDataLength = 5;

    // Communication timeouts (milliseconds)
    public const int EthernetResponseTimeoutMs = 5_000;
    public const int ReconnectDelayMs          = 1_000;
    public const int HeartbeatIntervalMs       = 30_000;

    // Polling cycle target for completion status
    public const int OptimalPollingCycleMs = 100;

    // Number 0 module = error display address
    public const string ErrorDisplayAddress = "0000";
}
