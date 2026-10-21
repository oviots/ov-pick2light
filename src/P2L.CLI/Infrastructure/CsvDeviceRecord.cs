using P2L.Shared.Enums;
using P2L.Shared.Models;

namespace P2L.CLI.Infrastructure;

/// <summary>
/// One row in a P2L device-configuration CSV file.
/// All parameter fields are nullable strings: null means the field was empty in the
/// source file and should be resolved from the current registry state on import.
/// </summary>
public sealed class CsvDeviceRecord
{
    // ── Identity ────────────────────────────────────────────────────────────
    public string  Address        { get; init; } = "";
    public int     SourceRow      { get; init; }          // 1-based line number in file

    // ── Display ─────────────────────────────────────────────────────────────
    public string? Display        { get; init; }          // null = keep current

    // ── Lighting parameters ─────────────────────────────────────────────────
    public string? Color          { get; init; }
    public string? LedMode        { get; init; }
    public string? SegMode        { get; init; }
    public string? BuzMode        { get; init; }
    public string? AfterConfirm   { get; init; }
    public string? BuzAfterConfirm { get; init; }

    // ── Informational (read-only on import) ─────────────────────────────────
    public string  Status         { get; init; } = "";
    public string? Label          { get; init; }

    // ─── Factory ─────────────────────────────────────────────────────────────

    /// <summary>Creates an export-ready record from a live module state.</summary>
    public static CsvDeviceRecord FromState(LightModuleState state) => new()
    {
        Address         = state.Address,
        Display         = state.DisplayData.Trim(),
        Color           = state.Color.ToString(),
        LedMode         = state.LedMode.ToString(),
        SegMode         = state.SegMode.ToString(),
        BuzMode         = state.BuzMode.ToString(),
        AfterConfirm    = state.AfterConfirm?.ToString(),
        BuzAfterConfirm = state.BuzAfterConfirm?.ToString(),
        Status          = state.Status.ToString(),
        Label           = "",
    };

    // ─── Import conversion ────────────────────────────────────────────────────

    /// <summary>
    /// Parses this record into a <see cref="ModuleParameters"/> and an optional display
    /// quantity, merging empty fields with <paramref name="current"/> registry state.
    /// Returns false and sets <paramref name="error"/> on the first validation failure.
    /// </summary>
    public bool TryToParameters(
        LightModuleState?   current,
        out ModuleParameters parameters,
        out int?            display,
        out string          error)
    {
        display    = null;
        parameters = new ModuleParameters();
        error      = "";

        // ── Display ────────────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(Display))
        {
            if (!int.TryParse(Display.Trim(), out int qty) || qty < 0)
            { error = $"Invalid display value '{Display}' — must be a non-negative integer."; return false; }
            display = qty;
        }

        // ── Parse each string field, return on first bad value ─────────────
        LightColor? color           = Parse(Color,           ParameterParser.ParseLightColor,  "Color");
        LightState? ledMode         = Parse(LedMode,         ParameterParser.ParseLightState,  "LedMode");
        LightState? segMode         = Parse(SegMode,         ParameterParser.ParseLightState,  "SegMode");
        LightState? buzMode         = Parse(BuzMode,         ParameterParser.ParseLightState,  "BuzMode");
        LightState? afterConfirm    = Parse(AfterConfirm,    ParameterParser.ParseLightState,  "AfterConfirm");
        LightState? buzAfterConfirm = Parse(BuzAfterConfirm, ParameterParser.ParseLightState,  "BuzAfterConfirm");

        // Check if any parse failed
        if (color is null && !string.IsNullOrWhiteSpace(Color))
        { error = $"Unknown Color value '{Color}'. Valid: {ParameterParser.ValidColorsHint}"; return false; }

        foreach (var (raw, label, value) in new[] {
            (LedMode,         "LedMode",         ledMode),
            (SegMode,         "SegMode",         segMode),
            (BuzMode,         "BuzMode",         buzMode),
            (AfterConfirm,    "AfterConfirm",    afterConfirm),
            (BuzAfterConfirm, "BuzAfterConfirm", buzAfterConfirm) })
        {
            if (value is null && !string.IsNullOrWhiteSpace(raw))
            { error = $"Unknown {label} value '{raw}'. Valid: {ParameterParser.ValidLedModesHint}"; return false; }
        }

        // ── Merge with current state for any unspecified fields ────────────
        var baseline = current is null
            ? new ModuleParameters { AfterConfirm = LightState.NoChange, BuzAfterConfirm = LightState.NoChange }
            : new ModuleParameters
            {
                Color           = current.Color,
                LedMode         = current.LedMode,
                SegMode         = current.SegMode,
                BuzMode         = current.BuzMode,
                AfterConfirm    = current.AfterConfirm ?? LightState.NoChange,
                BuzAfterConfirm = current.BuzAfterConfirm ?? LightState.NoChange,
            };

        parameters = baseline.With(
            color:           color,
            ledMode:         ledMode,
            segMode:         segMode,
            buzMode:         buzMode,
            afterConfirm:    afterConfirm,
            buzAfterConfirm: buzAfterConfirm);

        return true;
    }

    private static T? Parse<T>(string? raw, Func<string?, T?> parser, string _) where T : struct
        => string.IsNullOrWhiteSpace(raw) ? null : parser(raw);
}

/// <summary>A row-level error found while parsing a CSV file.</summary>
public sealed record CsvParseError(int Row, string Address, string Message);

/// <summary>Summary returned by <see cref="DeviceConfigCsvService.ApplyAsync"/>.</summary>
public sealed class CsvApplyResult
{
    public int                     Applied { get; init; }
    public int                     Failed  { get; init; }
    public IReadOnlyList<string>   Errors  { get; init; } = [];
}
