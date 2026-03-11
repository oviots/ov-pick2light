namespace P2L.Shared.Enums;

/// <summary>
/// Logical color aliases mapping to the three physical LED channels on Aioi light modules.
/// LED0 = typically Red, LED1 = typically Green, LED2 = typically Blue.
/// </summary>
public enum LightColor
{
    Off,
    Red,
    Green,
    Blue,
    Yellow,   // LED0 + LED1
    Cyan,     // LED1 + LED2
    Magenta,  // LED0 + LED2
    White,    // LED0 + LED1 + LED2
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
        return color switch
        {
            LightColor.Red     => (LightState.Off, LightState.Off, activeMode),
            LightColor.Green   => (LightState.Off, activeMode,     LightState.Off),
            LightColor.Blue    => (activeMode,     LightState.Off, LightState.Off),
            LightColor.Yellow  => (LightState.Off, activeMode,     activeMode),
            LightColor.Cyan    => (activeMode,     activeMode,     LightState.Off),
            LightColor.Magenta => (activeMode,     LightState.Off, activeMode),
            LightColor.White   => (activeMode,     activeMode,     activeMode),
            _                  => (LightState.Off, LightState.Off, LightState.Off),
        };
    }
}
