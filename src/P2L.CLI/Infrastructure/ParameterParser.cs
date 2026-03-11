using P2L.Shared.Enums;

namespace P2L.CLI.Infrastructure;

/// <summary>
/// Parses user-supplied strings (CLI options, CSV fields) into domain enum values.
/// Returns null for unrecognised input so callers can decide how to handle it.
/// </summary>
public static class ParameterParser
{
    public static LightColor? ParseLightColor(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "off"     => LightColor.Off,
        "red"     => LightColor.Red,
        "green"   => LightColor.Green,
        "blue"    => LightColor.Blue,
        "yellow"  => LightColor.Yellow,
        "cyan"    => LightColor.Cyan,
        "magenta" => LightColor.Magenta,
        "white"   => LightColor.White,
        _         => null,
    };

    public static LightState? ParseLightState(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "off"                    => LightState.Off,
        "on"                     => LightState.On,
        "flash"                  => LightState.Flash,
        "highflash" or "hiflash" => LightState.HighFlash,
        "nochange" or "nc"       => LightState.NoChange,
        _                        => null,
    };

    public static string ValidColorsHint    => "Off Red Green Blue Yellow Cyan Magenta White";
    public static string ValidLedModesHint  => "Off On Flash HighFlash";
    public static string ValidSegModesHint  => "Off On Flash";
    public static string ValidBuzModesHint  => "Off On Flash";
    public static string ValidAfterHint     => "Off On Flash NoChange";
}
