namespace P2L.Shared.Enums;

/// <summary>
/// LED / SEG / BUZ state encoding per the Aioi mode array specification.
/// Binary representation: 0001=OFF, 0010=ON, 0011=Flash, 0100=HighFlash, 1111=NoChange.
/// Encoded as single hex ASCII char in command strings.
/// </summary>
public enum LightState : byte
{
    Off       = 0x1,   // '1'
    On        = 0x2,   // '2'
    Flash     = 0x3,   // '3'
    HighFlash = 0x4,   // '4'
    NoChange  = 0xF,   // 'F'
}

public static class LightStateExtensions
{
    /// <summary>Returns the single hex ASCII character used in Aioi command strings.</summary>
    public static char ToCommandChar(this LightState state) => state switch
    {
        LightState.Off       => '1',
        LightState.On        => '2',
        LightState.Flash     => '3',
        LightState.HighFlash => '4',
        LightState.NoChange  => 'F',
        _                    => '1',
    };
}
