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

    public sealed class ParsedResponse
    {
        public ResponseKind Kind        { get; init; }
        public string       Raw         { get; init; } = string.Empty;
        public ConfirmationEvent? Event { get; init; }
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
            't' => new ParsedResponse
            {
                Kind  = ResponseKind.Interrupt,
                Raw   = command,
                Event = ParseInterrupt(command, seqNo),
            },
            // Az response: a[addr(4)][statusByte(1)]  e.g. "a0000?" (0x3F = malfunction/not present)
            'a' => new ParsedResponse { Kind = ResponseKind.ModuleStatus, Raw = command },
            // @ response: controller spec e.g. "@94----"
            '@' => new ParsedResponse { Kind = ResponseKind.Ack,          Raw = command },
            _ => new ParsedResponse { Kind = ResponseKind.Unknown, Raw = command },
        };
    }

    // ─── t-interrupt parsing ───────────────────────────────────────────────
    // Format variants:
    //   t[addr(4)][status(2)]                    — normal completion
    //   t[addr(4)][status(2)][display(5)]        — +/- key adjusted quantity
    //
    // Status codes: "00"=normal, "01"=abnormal, "10"=shortage
    private static ConfirmationEvent ParseInterrupt(string cmd, int seqNo)
    {
        // 't' + 4-digit addr + 2-digit status = 7 chars minimum
        if (cmd.Length < 7)
            return UnknownEvent(cmd, seqNo);

        var address    = cmd[1..5];
        var statusStr  = cmd[5..7];
        var status     = ParseCompletionStatus(statusStr);
        string? adjusted = cmd.Length >= 12 ? cmd[7..12] : null;

        return new ConfirmationEvent
        {
            Address         = address,
            Status          = status,
            AdjustedDisplay = adjusted,
            SequenceNo      = seqNo,
        };
    }

    private static CompletionStatus ParseCompletionStatus(string s) => s switch
    {
        "00" => CompletionStatus.Normal,
        "01" => CompletionStatus.Abnormal,
        "10" => CompletionStatus.Shortage,
        _    => CompletionStatus.Normal,
    };

    private static ConfirmationEvent UnknownEvent(string raw, int seqNo) => new()
    {
        Address    = "0000",
        Status     = CompletionStatus.Abnormal,
        SequenceNo = seqNo,
    };
}
