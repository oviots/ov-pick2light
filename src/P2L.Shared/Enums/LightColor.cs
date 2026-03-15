namespace P2L.Shared.Enums;

/// <summary>
/// Logical color aliases mapping to the three physical LED channels on Aioi light modules.
/// Per spec §5.5.2 (L-PICK Controller Command Reference V4.1) for full-color modules
/// (AW2xxxF, TW2xxxF, DW2xxxF, TW2928, NW294x, NW297x):
///   LED0 = Blue, LED1 = Green, LED2 = Red.
/// </summary>
public enum LightColor
{
    Off,
    Red,
    Green,
    Blue,
    Yellow,   // LED2 + LED1  (Red + Green)
    Cyan,     // LED1 + LED0  (Green + Blue)
    Magenta,  // LED2 + LED0  (Red + Blue)
    White,    // LED2 + LED1 + LED0
}

/// <summary>
/// Maps a <see cref="LightColor"/> to the three-channel (LED2, LED1, LED0) <see cref="LightState"/> tuple.
/// </summary>
public static class LightColorExtensions
{
    /// <summary>
    /// Returns (LED2 state, LED1 state, LED0 state) for the requested color.
    /// </summary>
    public static (LightState Led2, LightState Led1, LightState Led0) ToLedStates(
        this LightColor color,
        LightState activeMode = LightState.On)
    {
        // LED channel assignment per §5.5.2 for full-color modules (AW2xxxF, TW2xxxF, etc.):
        //   LED2 = Red,  LED1 = Green,  LED0 = Blue
        return color switch
        {
            LightColor.Red     => (activeMode,     LightState.Off, LightState.Off),  // LED2=Red
            LightColor.Green   => (LightState.Off, activeMode,     LightState.Off),  // LED1=Green
            LightColor.Blue    => (LightState.Off, LightState.Off, activeMode),      // LED0=Blue
            LightColor.Yellow  => (activeMode,     activeMode,     LightState.Off),  // LED2+LED1 = Red+Green
            LightColor.Cyan    => (LightState.Off, activeMode,     activeMode),      // LED1+LED0 = Green+Blue
            LightColor.Magenta => (activeMode,     LightState.Off, activeMode),      // LED2+LED0 = Red+Blue
            LightColor.White   => (activeMode,     activeMode,     activeMode),      // all
            _                  => (LightState.Off, LightState.Off, LightState.Off),
        };
    }
}
