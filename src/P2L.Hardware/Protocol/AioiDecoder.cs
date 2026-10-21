using P2L.Shared.Enums;
using P2L.Shared.Models;

namespace P2L.Hardware.Protocol;

/// <summary>
/// Parses Aioi controller responses and interrupt (push) notifications.
///
/// Controller→PC message types:
///   "o"    — positive ACK to a PC command
///   "n"    — negative ACK (duplicate SeqNo detected on Ethernet)
///   "n1"   — duplicate SeqNo error
///   "t..."  — CONFIRM interrupt (button press notification)
///
/// Reference: L-Pick Controller Command Reference V4.1, §Response Commands
/// </summary>
public static class AioiDecoder
{
    public enum ResponseKind { Ack, Nack, DuplicateSeq, Interrupt, ModuleStatus, Unknown }

    /// <summary>
    /// Parsed payload of an 'a' response (Az module-status inquiry reply).
    /// Format: a[addr(4)][statusByte(1)], e.g. "a00011" or "a0001?" (not present).
    /// </summary>
    public sealed class ModuleStatusInfo
    {
        public string       Address   { get; init; } = string.Empty;
        public ModuleStatus Status    { get; init; }
        /// <summary>False when the status byte is '?' (module absent or in fault).</summary>
        public bool         IsPresent => Status != ModuleStatus.Malfunction;
    }

    public sealed class ParsedResponse
    {
        public ResponseKind       Kind       { get; init; }
        public string             Raw        { get; init; } = string.Empty;
        public ConfirmationEvent? Event      { get; init; }
        public ModuleStatusInfo?  ModuleInfo { get; init; }
    }

    /// <summary>
    /// Classifies and parses the Command portion of a received packet.
    /// </summary>
    public static ParsedResponse Parse(string command, int seqNo)
    {
        if (string.IsNullOrEmpty(command))
            return new ParsedResponse { Kind = ResponseKind.Unknown, Raw = command };

        return command[0] switch
        {
            'o' => new ParsedResponse { Kind = ResponseKind.Ack,          Raw = command },
            'n' => command == "n1"
                       ? new ParsedResponse { Kind = ResponseKind.DuplicateSeq, Raw = command }
                       : new ParsedResponse { Kind = ResponseKind.Nack,         Raw = command },
            't' => ParseInterruptResponse(command, seqNo),
            // Az response: a[addr(4)][statusByte(1)]  e.g. "a00011" (standby) / "a0000?" (not present)
            'a' => new ParsedResponse
            {
                Kind       = ResponseKind.ModuleStatus,
                Raw        = command,
                ModuleInfo = ParseModuleStatus(command),
            },
            // @ response: controller spec e.g. "@94----"
            '@' => new ParsedResponse { Kind = ResponseKind.Ack,          Raw = command },
            _ => new ParsedResponse { Kind = ResponseKind.Unknown, Raw = command },
        };
    }

    // ─── t-interrupt parsing ───────────────────────────────────────────────
    // Format variants:
    //   t[addr(4)][status(2)]                       — normal completion
    //   t[addr(4)][status(2)]<[display(5)]>         — Fn/+- key adjusted quantity
    //
    // Examples:
    //   t000200              — address 0002, status 00, no adjustment
    //   t000200<00006>       — address 0002, status 00, operator adjusted display to 6
    //   t100100<    12>      — address 1001, status 00, operator adjusted display to 12
    //
    // Status codes: "00"=normal, "01"=abnormal, "10"=shortage
    private static ParsedResponse ParseInterruptResponse(string cmd, int seqNo)
    {
        var evt = ParseInterrupt(cmd, seqNo);
        return new ParsedResponse { Kind = evt is null ? ResponseKind.Unknown : ResponseKind.Interrupt, Raw = cmd, Event = evt };
    }

    private static ConfirmationEvent? ParseInterrupt(string cmd, int seqNo)
    {
        // 't' + 4-digit addr + 2-digit status = 7 chars minimum
        if (cmd.Length != 7 && cmd.Length != 14) return null;

        var address   = cmd[1..5];
        if (address.Any(c => c is < '0' or > '9') || address == "0000") return null;
        var statusStr = cmd[5..7];
        var status    = ParseCompletionStatus(statusStr);
        if (status is null) return null;

        // Extended format: t[addr4][status2]<[display5]>  — total 14 chars
        // The '<' delimiter is at position 7 and '>' at position 13.
        string? adjusted = null;
        if (cmd.Length == 14)
        {
            if (cmd[7] != '<' || cmd[13] != '>') return null;
            adjusted = cmd[8..13];
            if (!int.TryParse(adjusted.Trim(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out _)) return null;
        }

        return new ConfirmationEvent
        {
            Address         = address,
            Status          = status.Value,
            AdjustedDisplay = adjusted,
            SequenceNo      = seqNo,
        };
    }

    private static CompletionStatus? ParseCompletionStatus(string s) => s switch
    {
        "00" => CompletionStatus.Normal,
        "01" => CompletionStatus.Abnormal,
        "10" => CompletionStatus.Shortage,
        _    => null,
    };

    // ─── Az response parsing ──────────────────────────────────────────────
    // Format: a[addr(4)][statusByte(1)]   minimum 6 chars
    // The status byte is the ASCII character whose value equals the ModuleStatus enum member.
    private static ModuleStatusInfo ParseModuleStatus(string cmd)
    {
        if (cmd.Length < 6)
            return new ModuleStatusInfo { Address = "0000", Status = ModuleStatus.Malfunction };

        var address = cmd[1..5];
        var status  = (ModuleStatus)(byte)cmd[5];   // enum values == ASCII codes

        // Treat any unexpected byte as Malfunction (module not present / error)
        if (!Enum.IsDefined(status))
            status = ModuleStatus.Malfunction;

        return new ModuleStatusInfo { Address = address, Status = status };
    }
}
