using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using P2L.Shared.Enums;
using P2L.Shared.Models;

namespace P2L.Core;

/// <summary>Atomic versioned owner/replay snapshot; accepts legacy Operating-only arrays.</summary>
public sealed class RegistryPersistenceService : IDisposable
{
    private readonly LightModuleRegistry _registry;
    private readonly string _filePath;
    private readonly ILogger _log;
    private readonly object _saveLock = new();
    private bool _loaded;
    public RegistryPersistenceService(LightModuleRegistry registry, ILogger<RegistryPersistenceService> log,
        string? filePath = null, TimeSpan staleThreshold = default)
    {
        _registry = registry;
        _log = log;
        _filePath = filePath ?? Path.Combine(AppContext.BaseDirectory, "p2l_state.json");
        // Keep this legacy optional argument for API compatibility. Pending owners are
        // no longer discarded by age: elapsed time does not prove their lights are off.
    }
    public void Load()
    {
        lock (_registry.SyncRoot)
        {
            if (_loaded)
                return;
            bool exists;
            try
            {
                _ = File.GetAttributes(_filePath);
                exists = true;
            }
            catch (FileNotFoundException) { exists = false; }
            catch (DirectoryNotFoundException) { exists = false; }
            if (exists)
            {
                try
                {
                    var json = File.ReadAllText(_filePath);
                    using var document = JsonDocument.Parse(json);
                    RegistrySnapshot snapshot;
                    if (document.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        var legacy = JsonSerializer.Deserialize(json, CoreJsonContext.Default.ListPersistedModuleEntry) ?? throw new JsonException("Empty legacy state.");
                        snapshot = new RegistrySnapshot
                        {
                            Modules = legacy.Select(e => new LightModuleState
                            {
                                Address = e.Address,
                                Status = ModuleStatus.Operating,
                                DisplayData = e.Quantity.ToString().PadLeft(5, '0'),
                                Color = e.Color,
                                LedMode = e.LedMode,
                                SegMode = e.SegMode,
                                BuzMode = e.BuzMode,
                                BlockNo = e.BlockNo,
                                CorrelationId = e.CorrelationId,
                                CommandItemId = e.CommandItemId,
                                PickingDetailId = e.PickingDetailId,
                                LastInstructionAt = e.LastInstructionAt
                            }).ToList()
                        };
                    }
                    else
                    {
                        if (!document.RootElement.TryGetProperty("version", out var version) || version.GetInt32() != 2
                            || !document.RootElement.TryGetProperty("modules", out _)
                            || !document.RootElement.TryGetProperty("completedItems", out _)
                            || !document.RootElement.TryGetProperty("interrupts", out _))
                            throw new JsonException("Incomplete versioned state envelope.");
                        snapshot = JsonSerializer.Deserialize(json, CoreJsonContext.Default.RegistrySnapshot) ?? throw new JsonException("Empty state snapshot.");
                    }
                    Validate(snapshot);
                    _registry.Restore(snapshot);
                    _log.LogInformation("[State] Restored {Count} module owners and replay guards", snapshot.Modules.Count);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "[State] Existing state cannot be loaded; refusing an empty-state startup: {File}", _filePath);
                    throw new IOException("Existing Pick2Light state is invalid or unreadable; reconcile it before startup.", ex);
                }
            }
            _registry.PersistenceRequired += SaveRequired;
            _loaded = true;
        }
    }
    private static void Validate(RegistrySnapshot snapshot)
    {
        if (snapshot.Version != 2 || snapshot.Modules is null || snapshot.CompletedItems is null || snapshot.Interrupts is null
            || snapshot.Modules.Count > 9999 || snapshot.CompletedItems.Count > LightModuleRegistry.CompletedHistoryLimit
            || snapshot.Interrupts.Count > LightModuleRegistry.InterruptHistoryLimit)
            throw new JsonException("Invalid state version or capacity.");
        if (snapshot.PendingBatchCompletions is null || snapshot.PendingBatchCompletions.Count > 256
            || snapshot.PendingBatchCompletions.Any(c => c is null || string.IsNullOrWhiteSpace(c.EventId) || c.CompletedAt == default))
            throw new JsonException("Invalid pending batch completions.");
        if (snapshot.ClosedSessions is null || snapshot.ClosedSessions.Count > LightModuleRegistry.CompletedHistoryLimit
            || snapshot.ClosedSessions.Any(c => c is null || string.IsNullOrWhiteSpace(c.CorrelationId)
                || c.BlockNo.Length != 2 || !c.BlockNo.All(char.IsAsciiDigit) || c.ClosedAt == default)
            || snapshot.ClosedSessions.Select(c => (c.BlockNo, c.CorrelationId)).Distinct().Count() != snapshot.ClosedSessions.Count)
            throw new JsonException("Invalid closed-session replay history.");
        if (snapshot.PendingMissingConfirmations is null || snapshot.PendingMissingConfirmations.Count > 256
            || snapshot.PendingMissingConfirmations.Any(e => e is null || string.IsNullOrWhiteSpace(e.EventId)
                || e.Address.Length != 4 || !e.Address.All(char.IsAsciiDigit) || e.Address == "0000"
                || e.BlockNo.Length != 2 || !e.BlockNo.All(char.IsAsciiDigit)
                || e.DetectedAt == default || e.LastInstructionAt == default || !Enum.IsDefined(e.ObservedStatus)))
            throw new JsonException("Invalid pending missing confirmations.");
        var addresses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in snapshot.Modules)
        {
            if (m is null || m.Address.Length != 4 || !addresses.Add(m.Address) || !Enum.IsDefined(m.Status) || m.LastInstructionAt is null)
                throw new JsonException("Invalid persisted module.");
            if ((m.ConfirmationMissingAt is null) != (m.ConfirmationMissingEventId is null)
                || (m.ConfirmationMissingAt is not null && (m.Status != P2L.Shared.Enums.ModuleStatus.Operating
                    || string.IsNullOrWhiteSpace(m.ConfirmationMissingEventId))))
                throw new JsonException("Invalid missing-confirmation state.");
            PickingEngine.ValidateInstructions([new PickingInstruction {Address=m.Address,Quantity=int.TryParse(m.DisplayData.Trim(),out var q)?q:-1,
                BlockNo=m.BlockNo,Color=m.Color,LedMode=m.LedMode,SegMode=m.SegMode,BuzMode=m.BuzMode}]);
        }
        foreach (var c in snapshot.CompletedItems)
            if (c is null || c.CompletedAt == default || (string.IsNullOrEmpty(c.CommandItemId) && string.IsNullOrEmpty(c.PickingDetailId)))
                throw new JsonException("Invalid completed-item guard.");
        foreach (var i in snapshot.Interrupts)
            if (i is null || (i.Source == "manual_recovery" ? i.SequenceNo != -1 : i.SequenceNo is < 0 or > 999)
                || i.Address.Length != 4 || !i.Address.All(char.IsAsciiDigit) || i.Address == "0000"
                || !Enum.IsDefined(i.Status) || i.ProcessedAt == default
                || (i.PendingState is not null && (string.IsNullOrEmpty(i.EventId) || i.PendingState.Address != i.Address
                    || i.PendingState.Status is not (ModuleStatus.Completed or ModuleStatus.Shortage))))
                throw new JsonException("Invalid interrupt guard.");
    }
    private void SaveRequired() => Save(true);
    private void Save(bool throwOnError)
    {
        // Every caller uses registry -> save lock ordering, including Dispose.
        lock (_registry.SyncRoot)
            lock (_saveLock)
            {
                try
                {
                    var json = JsonSerializer.Serialize(_registry.Snapshot(), CoreJsonContext.Default.RegistrySnapshot);
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_filePath))!);
                    var temporary = _filePath + ".tmp";
                    using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        stream.Write(System.Text.Encoding.UTF8.GetBytes(json));
                        stream.Flush(true);
                    }
                    File.Move(temporary, _filePath, true);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "[State] Durable state transition failed");
                    if (throwOnError)
                        throw;
                }
            }
    }
    public void Dispose()
    {
        lock (_registry.SyncRoot)
        {
            if (!_loaded)
                return;
            _registry.PersistenceRequired -= SaveRequired;
            Save(false);
            _loaded = false;
        }
    }
}

// Legacy array schema remains readable. The next successful transition upgrades it.
public sealed class PersistedModuleEntry
{
    public string Address { get; set; } = "";
    public int Quantity { get; set; }
    public LightColor Color { get; set; } = LightColor.Green;
    public LightState LedMode { get; set; } = LightState.On;
    public LightState SegMode { get; set; } = LightState.On;
    public LightState BuzMode { get; set; } = LightState.Off;
    public string BlockNo { get; set; } = "00";
    public string? CorrelationId { get; set; }
    public string? CommandItemId { get; set; }
    public string? PickingDetailId { get; set; }
    public DateTime LastInstructionAt { get; set; }
}
[JsonSerializable(typeof(List<PersistedModuleEntry>))]
[JsonSerializable(typeof(RegistrySnapshot))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, WriteIndented = true)]
internal sealed partial class CoreJsonContext : JsonSerializerContext
{
}
