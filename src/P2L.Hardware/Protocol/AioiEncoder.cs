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
    /// Format: M[m1][m2][m3][m4]
    ///   m1 = normal operation states   (LED2 LED1 LED0 SEG)
    ///   m2 = after-CONFIRM states      (LED2 LED1 LED0 SEG)
    ///   m3 = after-Fn-key states       (LED2 LED1 LED0 SEG)
    ///   m4 = BUZ states               (normal afterConfirm afterFn –)
    ///
    /// Each nibble: 1=Off 2=On 3=Flash 4=HighFlash F=NoChange
    /// </summary>
    public static string SetModeArray(
        (LightState Led2, LightState Led1, LightState Led0, LightState Seg) normal,
        (LightState Led2, LightState Led1, LightState Led0, LightState Seg) afterConfirm,
        (LightState Led2, LightState Led1, LightState Led0, LightState Seg) afterFn,
        (LightState Buz, LightState BuzAfterConfirm, LightState BuzAfterFn) buz)
    {
        static char N(LightState s) => s.ToCommandChar();

        return new StringBuilder("M")
            .Append(N(normal.Led2)).Append(N(normal.Led1)).Append(N(normal.Led0)).Append(N(normal.Seg))
            .Append(N(afterConfirm.Led2)).Append(N(afterConfirm.Led1)).Append(N(afterConfirm.Led0)).Append(N(afterConfirm.Seg))
            .Append(N(afterFn.Led2)).Append(N(afterFn.Led1)).Append(N(afterFn.Led0)).Append(N(afterFn.Seg))
            .Append(N(buz.Buz)).Append(N(buz.BuzAfterConfirm)).Append(N(buz.BuzAfterFn)).Append('1')
            .ToString();
    }

    /// <summary>
    /// Convenience overload: sets LED states from a <see cref="LightColor"/> and active mode,
    /// turning off all LEDs after CONFIRM, and no buzzer.
    /// </summary>
    public static string SetModeForColor(LightColor color, LightState ledMode = LightState.On)
    {
        var (led2, led1, led0) = color.ToLedStates(ledMode);
        return SetModeArray(
            normal:       (led2, led1, led0, LightState.On),
            afterConfirm: (LightState.Off, LightState.Off, LightState.Off, LightState.Off),
            afterFn:      (LightState.NoChange, LightState.NoChange, LightState.NoChange, LightState.NoChange),
            buz:          (LightState.Off, LightState.Off, LightState.Off));
    }

    // ─── Block operation instructions ─────────────────────────────────────

    /// <summary>
    /// PP1 — Block operation instruction (recommended; not overwritable).
    ///
    /// Format: PP1[BlockNo(2)][Item...] where each Item = [Addr(4)][Display(5)]
    /// The M command must be sent before PP1 to configure LED/SEG/BUZ modes.
    ///
    /// Use PP5 when you need to overwrite an in-progress instruction.
    /// </summary>
    public static string BlockInstruct(string blockNo, IEnumerable<PickingInstruction> items)
        => BuildBlockCommand("PP1", blockNo, items);

    /// <summary>
    /// PP5 — Rewritable block operation instruction (overwrites in-progress instruction).
    /// </summary>
    public static string BlockInstructOverwrite(string blockNo, IEnumerable<PickingInstruction> items)
        => BuildBlockCommand("PP5", blockNo, items);

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
        return $"L{address.PadLeft(4, '0')}{displayData.PadLeft(5)}";
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
