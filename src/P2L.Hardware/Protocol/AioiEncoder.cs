using System.Text;
using P2L.Shared.Enums;
using P2L.Shared.Models;

namespace P2L.Hardware.Protocol;

/// <summary>
/// Builds Aioi command strings (the payload inside a packet's Command field).
///
/// All methods return pure ASCII strings — no STX/ETX framing.
/// Wrap the result in <see cref="AioiPacket"/> for transmission.
///
/// Reference: L-Pick Controller Command Reference V4.1
/// </summary>
public static class AioiEncoder
{
    // ─── Initialisation ───────────────────────────────────────────────────

    /// <summary>Z — Full initialization. Send once at startup.</summary>
    public static string Initialize() => "Z";

    /// <summary>Z1 — Hardware reset of all modules (~1.5 s delay required after send).</summary>
    public static string HardwareReset() => "Z1";

    // ─── Mode configuration ────────────────────────────────────────────────

    /// <summary>
    /// M — Mode array: defines LED/SEG/BUZ behavior for all subsequent PP1/L instructions.
    ///
    /// Uses the Aioi mode array format: M m1[3 bytes] m2[3 bytes] m4[1 byte]
    ///   m1 = display status during instruction
    ///   m2 = display status after CONFIRM press
    ///   m4 = CONFIRM/Fn key configuration (0x40 = both enabled)
    ///
    /// Binary byte encoding per Aioi spec §4.4.2:
    ///   LED2 byte:      0x30 | state_nibble  (high nibble is fixed 0x3 prefix)
    ///   LED1+LED0 byte: (LED1_nibble &lt;&lt; 4) | LED0_nibble
    ///   SEG+BUZ byte:   (SEG_nibble  &lt;&lt; 4) | BUZ_nibble
    /// State nibbles: Off=1, On=2, Flash=3, HighFlash=4, NoChange=0xF
    ///
    /// IMPORTANT: These bytes are binary (not ASCII). AioiPacket.ToBytes() must
    /// use Latin1 encoding to preserve byte values above 0x7F.
    /// </summary>
    public static string SetModeArray(
        (LightState Led2, LightState Led1, LightState Led0, LightState Seg, LightState Buz) normal,
        (LightState Led2, LightState Led1, LightState Led0, LightState Seg, LightState Buz) afterConfirm)
    {
        // LED2 byte: high nibble is fixed 0x3 prefix, low nibble is the state
        static char Led2B(LightState s) => (char)(0x30 | (byte)s);
        // Packed-nibble byte: high nibble = first state, low nibble = second state
        static char Pack(LightState hi, LightState lo) => (char)(((byte)hi << 4) | (byte)lo);

        return new StringBuilder("M")
            .Append("m1")
            .Append(Led2B(normal.Led2))
            .Append(Pack(normal.Led1,        normal.Led0))
            .Append(Pack(normal.Seg,         normal.Buz))
            .Append("m2")
            .Append(Led2B(afterConfirm.Led2))
            .Append(Pack(afterConfirm.Led1,  afterConfirm.Led0))
            .Append(Pack(afterConfirm.Seg,   afterConfirm.Buz))
            .Append("m4\x40")   // CONFIRM + Fn key: enabled; Fn-decrement: disabled
            .ToString();
    }

    /// <summary>
    /// Convenience overload: sets LED states from a <see cref="LightColor"/> and active mode,
    /// turning off all LEDs after CONFIRM, and no buzzer.
    /// </summary>
    public static string SetModeForColor(LightColor color, LightState ledMode = LightState.On)
    {
        var (led2, led1, led0) = color.ToLedStates(ledMode);
        // afterConfirm uses NoChange (0xF nibbles) instead of Off (0x1 nibbles) to avoid
        // producing 0x11 (XON) / 0x13 (XOFF) bytes that some gateway firmware strips from TCP.
        return SetModeArray(
            normal:       (led2,              led1,              led0,              LightState.On,       LightState.Off),
            afterConfirm: (LightState.NoChange, LightState.NoChange, LightState.NoChange, LightState.NoChange, LightState.NoChange));
    }

    /// <summary>
    /// Builds a full M-command from a <see cref="ModuleParameters"/> instance,
    /// giving control over SEG mode, buzzer, and after-CONFIRM behaviour.
    /// </summary>
    public static string SetModeForParameters(ModuleParameters p)
    {
        var (led2, led1, led0) = p.Color.ToLedStates(p.LedMode);
        // Use NoChange if caller passes Off for afterConfirm to avoid XON (0x11) bytes.
        var ac = p.AfterConfirm == LightState.Off ? LightState.NoChange : p.AfterConfirm;
        return SetModeArray(
            normal:       (led2, led1, led0, p.SegMode,  p.BuzMode),
            afterConfirm: (ac,   ac,   ac,   ac,         p.BuzAfterConfirm == LightState.Off ? LightState.NoChange : p.BuzAfterConfirm));
    }

    // ─── Per-address mode commands (alternative to M mode array) ──────────

    /// <summary>
    /// Am1 — Sets the display status (LED/SEG/BUZ during instruction) for a specific address.
    ///
    /// Format: Am1[Address(4)][LED2_byte][LED1+LED0_byte][SEG+BUZ_byte]
    /// Binary byte encoding (same as M mode array m1 section):
    ///   LED2 byte:      0x30 | state_nibble
    ///   LED1+LED0 byte: (LED1_nibble &lt;&lt; 4) | LED0_nibble
    ///   SEG+BUZ byte:   (SEG_nibble  &lt;&lt; 4) | BUZ_nibble
    ///
    /// Example from Lightstep PG §4.7.1: Am10001$33$11$23
    ///   → LED2=Flash, LED1=Off, LED0=Off, SEG=On, BUZ=Flash
    /// </summary>
    public static string SetModeAm1(string address,
                                     LightState led2, LightState led1, LightState led0,
                                     LightState seg,  LightState buz)
    {
        static char Led2B(LightState s) => (char)(0x30 | (byte)s);
        static char Pack(LightState hi, LightState lo) => (char)(((byte)hi << 4) | (byte)lo);
        return $"Am1{address.PadLeft(4, '0')}{Led2B(led2)}{Pack(led1, led0)}{Pack(seg, buz)}";
    }

    /// <summary>
    /// Am2 — Sets the display status after CONFIRM press for a specific address.
    /// Same binary byte format as Am1.
    /// </summary>
    public static string SetModeAm2(string address,
                                     LightState led2, LightState led1, LightState led0,
                                     LightState seg,  LightState buz)
    {
        static char Led2B(LightState s)                => (char)(0x30 | (byte)s);
        static char Pack(LightState hi, LightState lo) => (char)(((byte)hi << 4) | (byte)lo);
        return $"Am2{address.PadLeft(4, '0')}{Led2B(led2)}{Pack(led1, led0)}{Pack(seg, buz)}";
    }

    // ─── Block operation instructions ─────────────────────────────────────

    /// <summary>
    /// P1 — Block instruction. Format: P1[BlockNo(2)][Addr(4)][Display(5)]...
    /// Flash mode is set via Am1 nibbles before P1, not via a separate FP1 command
    /// (FP1 is NACK'd by L-PICK Ethernet gateways; Am1 controls On/Flash/HighFlash).
    /// Use BlockInstructOverwrite when you need to overwrite an in-progress instruction.
    /// </summary>
    public static string BlockInstruct(string blockNo, IEnumerable<PickingInstruction> items,
                                       LightState ledMode = LightState.On)
        => BuildBlockCommand("P1", blockNo, items);

    /// <summary>
    /// PP5 — Rewritable block instruction with no Fn display and no mode array.
    /// Format: PP50500[BlockNo(2)][Addr(4)][Display(5)]...
    /// Omitting the mode array preserves each address's preceding Am1 color/modes.
    /// Plain P5 requires five operation-mode bytes and cannot use the P1 layout.
    /// </summary>
    public static string BlockInstructOverwrite(string blockNo, IEnumerable<PickingInstruction> items,
                                                LightState ledMode = LightState.On)
        => BuildBlockCommand("PP50500", blockNo, items);

    private static string BuildBlockCommand(string cmd, string blockNo, IEnumerable<PickingInstruction> items)
    {
        var sb = new StringBuilder(cmd);
        sb.Append(blockNo.PadLeft(2, '0'));
        foreach (var item in items)
        {
            sb.Append(item.Address.PadLeft(4, '0'));
            sb.Append(item.FormattedDisplay);
        }
        return sb.ToString();
    }

    // ─── Individual light instruction ──────────────────────────────────────

    /// <summary>
    /// L — Single-address operation instruction.
    ///
    /// Format: L[Addr(4)][Display(5)]
    /// The M command must define the active mode beforehand.
    /// </summary>
    public static string SingleInstruct(string address, string displayData)
    {
        if (displayData.Length > 5) displayData = displayData[^5..];
        return $"L{address.PadLeft(4, '0')}{displayData.PadLeft(5, '0')}";
    }

    // ─── Termination ───────────────────────────────────────────────────────

    /// <summary>
    /// D — Terminate a single module (turns off light, removes instruction).
    /// Address format: 4-digit BCD, e.g. "0001".
    /// </summary>
    public static string Terminate(string address)
        => $"D{address.PadLeft(4, '0')}";

    /// <summary>
    /// D — Terminate all modules in a block (and extinguishes signal light).
    /// BlockNo format: 2-digit BCD, e.g. "00" for all.
    /// </summary>
    public static string TerminateBlock(string blockNo)
        => $"D{blockNo.PadLeft(2, '0')}";

    // ─── CONFIRM acknowledgement ───────────────────────────────────────────

    /// <summary>
    /// O — Acknowledge a "t" interrupt from the controller.
    /// Must be sent immediately upon receiving any "t" packet (highest priority).
    /// </summary>
    public static string Ack() => "O";

    // ─── Completion polling (Board/RS-232C only) ───────────────────────────

    /// <summary>
    /// H — Poll block completion status.
    /// For Ethernet the controller pushes "t" events; H is not needed.
    /// </summary>
    public static string PollBlock(string blockNo)
        => $"H{blockNo.PadLeft(2, '0')}";

    // ─── Signal light / one-point I/O ──────────────────────────────────────

    /// <summary>
    /// T — Turn signal light ON or OFF.
    /// Signal light addresses: 8001–8099.
    /// </summary>
    public static string SignalLight(string address, bool on)
        => $"T{address.PadLeft(4, '0')}{(on ? '1' : '0')}";

    // ─── Maintenance ──────────────────────────────────────────────────────

    /// <summary>
    /// Az — Module status inquiry. Returns module status byte.
    /// Response: single byte per Aioi module status encoding.
    /// </summary>
    public static string ModuleStatusInquiry(string address)
        => $"Az{address.PadLeft(4, '0')}";

    /// <summary>
    /// @ — Controller operation specification / firmware version query.
    /// </summary>
    public static string ControllerSpec() => "@";
}
