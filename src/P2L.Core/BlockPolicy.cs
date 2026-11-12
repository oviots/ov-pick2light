namespace P2L.Core;

/// <summary>Optional controller-wide allowlist, supplied only by licensed configuration.</summary>
public sealed class BlockPolicy
{
    private readonly HashSet<string>? _allowed;
    public string[] ConfiguredBlocks => _allowed?.ToArray() ?? [];
    public BlockPolicy(IEnumerable<string>? allowed = null)
    {
        if (allowed is null) return;
        var values = allowed.ToArray();
        if (values.Length == 0 || values.Any(b => !IsValid(b)) || values.Distinct().Count() != values.Length ||
            (values.Length > 1 && values.Contains("00")))
            throw new ArgumentException("Blocks must be unique two-digit numbers; 00 cannot be combined with other blocks.");
        _allowed = new(values, StringComparer.Ordinal);
    }
    public static bool IsValid(string? block) => block is {Length: 2} && block.All(char.IsAsciiDigit);
    public void Validate(string block)
    {
        if (!IsValid(block) || (_allowed is not null && !_allowed.Contains(block)))
            throw new InvalidOperationException($"Block {block} is not configured for this controller.");
    }
}
