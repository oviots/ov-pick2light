namespace P2L.Shared.Enums;

/// <summary>
/// Status codes returned by the Az (module status inquiry) command and t-interrupt events.
/// </summary>
public enum ModuleStatus
{
    Unknown    = -1,
    PowerOn    = 0x30,  // '0' – Module just powered on
    Standby    = 0x31,  // '1' – Idle, no instruction active
    Operating  = 0x32,  // '2' – Instruction active (normal)
    Operating2 = 0x33,  // '3' – Instruction active (alternate state)
    Completed  = 0x34,  // '4' – CONFIRM button pressed, waiting for D command
    Shortage   = 0x35,  // '5' – +/- key pressed, quantity modified
    Maintenance= 0x36,  // '6' – Maintenance mode
    Malfunction= 0x3F,  // '?' – Hardware error
}

/// <summary>
/// Completion status codes embedded in the "t" interrupt packet (2-digit decimal).
/// </summary>
public enum CompletionStatus
{
    Normal   = 0,   // "00"
    Abnormal = 1,   // "01"
    Shortage = 10,  // "10"
}
