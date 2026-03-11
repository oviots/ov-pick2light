using System.Text;
using Microsoft.Extensions.Logging;
using P2L.Core;
using P2L.Shared.Models;

namespace P2L.CLI.Infrastructure;

/// <summary>
/// Exports device state to CSV and imports/applies CSV parameter changes back to hardware.
///
/// CSV format (RFC 4180):
///   • Comment lines start with '#' and are ignored on import.
///   • First non-comment line is the header row (column order is flexible).
///   • Empty field value means "keep current registry value" on import.
///   • The Status column is informational and ignored on import.
/// </summary>
public sealed class DeviceConfigCsvService
{
    private readonly PickingEngine       _engine;
    private readonly LightModuleRegistry _registry;
    private readonly ILogger             _log;

    private static readonly string[] ColumnHeaders =
        ["Address", "Display", "Color", "LedMode", "SegMode", "BuzMode",
         "AfterConfirm", "BuzAfterConfirm", "Status", "Label"];

    public DeviceConfigCsvService(PickingEngine       engine,
                                   LightModuleRegistry registry,
                                   ILogger<DeviceConfigCsvService> logger)
    {
        _engine   = engine;
        _registry = registry;
        _log      = logger;
    }

    // ─── Export ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes <paramref name="modules"/> to a UTF-8 CSV file with a self-documenting
    /// comment header.  Existing files are overwritten.
    /// </summary>
    public void Export(IReadOnlyList<LightModuleState> modules,
                       string filePath,
                       string gatewayDescription = "")
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var w = new StreamWriter(filePath, append: false, Encoding.UTF8);

        // ── Instructional header ───────────────────────────────────────────
        w.WriteLine("# P2L Device Configuration Export");
        if (!string.IsNullOrEmpty(gatewayDescription))
            w.WriteLine($"# Gateway:  {gatewayDescription}");
        w.WriteLine($"# Exported: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        w.WriteLine("#");
        w.WriteLine("# HOW TO USE:");
        w.WriteLine("#   1. Edit parameters in this file (see valid values below)");
        w.WriteLine("#   2. Save the file");
        w.WriteLine("#   3. Run:  p2l import <this-file>");
        w.WriteLine("#");
        w.WriteLine("# VALID VALUES:");
        w.WriteLine($"#   Color:          {ParameterParser.ValidColorsHint}");
        w.WriteLine($"#   LedMode:        {ParameterParser.ValidLedModesHint}");
        w.WriteLine($"#   SegMode:        {ParameterParser.ValidSegModesHint}");
        w.WriteLine($"#   BuzMode:        {ParameterParser.ValidBuzModesHint}");
        w.WriteLine($"#   AfterConfirm:   {ParameterParser.ValidAfterHint}");
        w.WriteLine($"#   BuzAfterConfirm:{ParameterParser.ValidBuzModesHint}");
        w.WriteLine("#   Display:        integer  (leave blank to keep current displayed value)");
        w.WriteLine("#   Status:         informational only — ignored on import");
        w.WriteLine("#");

        // ── Column headers ────────────────────────────────────────────────
        w.WriteLine(ToCsvRow(ColumnHeaders));

        // ── Data rows ─────────────────────────────────────────────────────
        foreach (var m in modules.OrderBy(m => m.Address))
        {
            var r = CsvDeviceRecord.FromState(m);
            w.WriteLine(ToCsvRow(
                r.Address, r.Display ?? "", r.Color ?? "", r.LedMode ?? "",
                r.SegMode ?? "", r.BuzMode ?? "", r.AfterConfirm ?? "",
                r.BuzAfterConfirm ?? "", r.Status, r.Label ?? ""));
        }

        _log.LogInformation("Exported {Count} modules to {Path}", modules.Count, filePath);
    }

    // ─── Import / parse ──────────────────────────────────────────────────────

    /// <summary>
    /// Parses a CSV file and returns all valid data records plus any per-row parse errors.
    /// The header row may appear in any column order; empty fields resolve to current
    /// registry state when <see cref="ApplyAsync"/> is called.
    /// </summary>
    public (IReadOnlyList<CsvDeviceRecord> Records, IReadOnlyList<CsvParseError> Errors)
        ParseFile(string filePath)
    {
        var records = new List<CsvDeviceRecord>();
        var errors  = new List<CsvParseError>();

        using var reader = new StreamReader(filePath, Encoding.UTF8);

        // ── Locate header row (skip comments / blanks) ─────────────────────
        string? headerLine = null;
        int lineNo = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            if (IsSkippableLine(line)) continue;
            headerLine = line;
            break;
        }

        if (headerLine is null)
        {
            errors.Add(new CsvParseError(0, "", "File contains no data (only comments or blank lines)."));
            return (records, errors);
        }

        var colIndex = BuildColumnIndex(ParseCsvLine(headerLine));

        // ── Data rows ─────────────────────────────────────────────────────
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            if (IsSkippableLine(line)) continue;

            var fields = ParseCsvLine(line);
            var addr   = GetField(fields, colIndex, "address").Trim();

            if (string.IsNullOrWhiteSpace(addr))
            {
                errors.Add(new CsvParseError(lineNo, "", "Row has no Address — skipped."));
                continue;
            }

            addr = addr.PadLeft(4, '0');

            records.Add(new CsvDeviceRecord
            {
                Address         = addr,
                SourceRow       = lineNo,
                Display         = Nullable(GetField(fields, colIndex, "display")),
                Color           = Nullable(GetField(fields, colIndex, "color")),
                LedMode         = Nullable(GetField(fields, colIndex, "ledmode")),
                SegMode         = Nullable(GetField(fields, colIndex, "segmode")),
                BuzMode         = Nullable(GetField(fields, colIndex, "buzmode")),
                AfterConfirm    = Nullable(GetField(fields, colIndex, "afterconfirm")),
                BuzAfterConfirm = Nullable(GetField(fields, colIndex, "buzafterconfirm")),
                Status          = GetField(fields, colIndex, "status"),
                Label           = Nullable(GetField(fields, colIndex, "label")),
            });
        }

        return (records, errors);
    }

    // ─── Apply ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies each validated record to hardware via <see cref="PickingEngine.ConfigureModuleAsync"/>.
    /// Records are applied sequentially to avoid overwhelming the gateway.
    /// </summary>
    public async Task<CsvApplyResult> ApplyAsync(
        IReadOnlyList<CsvDeviceRecord> records,
        IProgress<int>?                progress = null,
        CancellationToken              ct       = default)
    {
        int applied = 0;
        int failed  = 0;
        var errors  = new List<string>();
        int done    = 0;

        foreach (var rec in records)
        {
            ct.ThrowIfCancellationRequested();

            var current = _registry.Get(rec.Address);

            if (!rec.TryToParameters(current, out var parameters, out var display, out var parseError))
            {
                var msg = $"Row {rec.SourceRow} ({rec.Address}): {parseError}";
                errors.Add(msg);
                _log.LogWarning("{Msg}", msg);
                failed++;
            }
            else
            {
                try
                {
                    await _engine.ConfigureModuleAsync(rec.Address, display, parameters, ct)
                                 .ConfigureAwait(false);
                    applied++;
                    _log.LogDebug("Applied row {Row} → {Addr}", rec.SourceRow, rec.Address);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var msg = $"Row {rec.SourceRow} ({rec.Address}): {ex.Message}";
                    errors.Add(msg);
                    _log.LogWarning("{Msg}", msg);
                    failed++;
                }
            }

            progress?.Report(++done);
        }

        return new CsvApplyResult { Applied = applied, Failed = failed, Errors = errors };
    }

    // ─── RFC 4180 writer ─────────────────────────────────────────────────────

    private static string ToCsvRow(params string[] fields)
        => string.Join(",", fields.Select(EscapeField));

    private static string EscapeField(string value)
    {
        if (value.Contains(',') || value.Contains('"') ||
            value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    // ─── RFC 4180 parser ─────────────────────────────────────────────────────

    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        int i = 0;

        do
        {
            string field;
            if (i < line.Length && line[i] == '"')
            {
                i++; // skip opening quote
                var sb = new StringBuilder();
                while (i < line.Length)
                {
                    if (line[i] == '"' && i + 1 < line.Length && line[i + 1] == '"')
                    { sb.Append('"'); i += 2; }
                    else if (line[i] == '"')
                    { i++; break; }
                    else
                    { sb.Append(line[i++]); }
                }
                field = sb.ToString();
            }
            else
            {
                int start = i;
                while (i < line.Length && line[i] != ',') i++;
                field = line[start..i].Trim();
            }

            fields.Add(field);

            if (i < line.Length && line[i] == ',')
                i++;   // advance past comma, read next field
            else
                break; // end of line
        }
        while (true);

        return [.. fields];
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static bool IsSkippableLine(string line)
        => string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#');

    private static Dictionary<string, int> BuildColumnIndex(string[] headers)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Length; i++)
        {
            var key = Normalize(headers[i]);
            if (!string.IsNullOrEmpty(key) && !index.ContainsKey(key))
                index[key] = i;
        }
        return index;
    }

    /// <summary>Strips spaces, hyphens and underscores for flexible header matching.</summary>
    private static string Normalize(string s)
        => s.ToLowerInvariant()
             .Replace(" ", "").Replace("-", "").Replace("_", "").Trim();

    private static string GetField(string[] fields, Dictionary<string, int> idx, string name)
    {
        if (!idx.TryGetValue(name, out int col) || col >= fields.Length) return "";
        return fields[col];
    }

    private static string? Nullable(string s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
