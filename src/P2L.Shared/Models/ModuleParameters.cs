using P2L.Shared.Enums;

namespace P2L.Shared.Models;

/// <summary>
/// The full set of lighting and display parameters that map to an Aioi M-command
/// (mode array) + optional L-command (display data).
///
/// These parameters describe how a module should behave during an active instruction
/// and what it should do after the operator presses CONFIRM.
///
/// Defaults mirror <c>SetModeForColor</c> behaviour: green steady LED, SEG on, no buzzer,
/// all LEDs off after CONFIRM.
/// </summary>
public sealed class ModuleParameters
{
    /// <summary>LED colour to illuminate during an active instruction.</summary>
    public LightColor Color { get; init; } = LightColor.Green;

    /// <summary>How the LED channels behave during an active instruction (On / Flash / HighFlash).</summary>
    public LightState LedMode { get; init; } = LightState.On;

    /// <summary>How the 7-segment display behaves during an active instruction.</summary>
    public LightState SegMode { get; init; } = LightState.On;

    /// <summary>Buzzer behaviour during an active instruction.</summary>
    public LightState BuzMode { get; init; } = LightState.Off;

    /// <summary>LED channel behaviour after the CONFIRM button is pressed.</summary>
    public LightState AfterConfirm { get; init; } = LightState.Off;

    /// <summary>Buzzer behaviour after the CONFIRM button is pressed.</summary>
    public LightState BuzAfterConfirm { get; init; } = LightState.Off;

    /// <summary>Creates a <see cref="ModuleParameters"/> from the relevant fields of a picking instruction.</summary>
    public static ModuleParameters FromInstruction(PickingInstruction inst) => new()
    {
        Color  = inst.Color,
        LedMode = inst.LedMode,
        SegMode = inst.SegMode,
        BuzMode = inst.BuzMode,
    };

    /// <summary>Returns a copy with only the non-null overrides applied.</summary>
    public ModuleParameters With(
        LightColor? color          = null,
        LightState? ledMode        = null,
        LightState? segMode        = null,
        LightState? buzMode        = null,
        LightState? afterConfirm   = null,
        LightState? buzAfterConfirm = null) => new()
    {
        Color           = color           ?? Color,
        LedMode         = ledMode         ?? LedMode,
        SegMode         = segMode         ?? SegMode,
        BuzMode         = buzMode         ?? BuzMode,
        AfterConfirm    = afterConfirm    ?? AfterConfirm,
        BuzAfterConfirm = buzAfterConfirm ?? BuzAfterConfirm,
    };
}
