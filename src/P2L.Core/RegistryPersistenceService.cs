using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using P2L.Shared.Enums;
using P2L.Shared.Models;

namespace P2L.Core;

/// <summary>
/// Persists the <see cref="LightModuleRegistry"/> state to a JSON file so that
/// modules which were <see cref="ModuleStatus.Operating"/> in a previous process
/// invocation are still known to the next one.
///
/// This prevents P1 block-instruct from extinguishing previously lit modules
/// when the CLI is invoked again as a new one-shot process.
///
/// Call <see cref="Load"/> once after construction (before any gateway commands).
/// The service then auto-saves whenever the registry state changes (debounced).
/// </summary>
public sealed class RegistryPersistenceService : IDisposable
{
    private readonly LightModuleRegistry _registry;
    private readonly string              _filePath;
    private readonly ILogger             _log;
    private readonly TimeSpan            _staleThreshold;

    private Timer?       _debounceTimer;
    private readonly object _timerLock = new();

    /// <summary>
    /// Creates the service.
    /// </summary>
    /// <param name="registry">The live module registry to persist.</param>
    /// <param name="filePath">Path of the state JSON file (default: p2l_state.json next to the binary).</param>
    /// <param name="staleThreshold">
    /// Operating entries older than this are ignored on load (prevents re-lighting
    /// modules from a previous shift). Default: 8 hours.
    /// </param>
    public RegistryPersistenceService(LightModuleRegistry registry,
                                      ILogger<RegistryPersistenceService> log,
                                      string? filePath = null,
                                      TimeSpan staleThreshold = default)
    {
        _registry       = registry;
        _log            = log;
        _filePath       = filePath ?? Path.Combine(AppContext.BaseDirectory, "p2l_state.json");
        _staleThreshold = staleThreshold == default ? TimeSpan.FromHours(8) : staleThreshold;
    }

    // ─── Load ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the state file and restores all non-stale Operating modules into
    /// the registry, then subscribes to future state changes for auto-save.
    /// Safe to call multiple times — subsequent calls are no-ops.
    /// </summary>
    public void Load()
    {
        if (File.Exists(_filePath))
        {
            try
            {
                var json    = File.ReadAllText(_filePath);
                var entries = JsonSerializer.Deserialize(json, CoreJsonContext.Default.ListPersistedModuleEntry);
                if (entries is not null)
                {
                    var cutoff  = DateTime.UtcNow - _staleThreshold;
                    int loaded  = 0;
                    int skipped = 0;

                    foreach (var e in entries)
                    {
                        if (e.LastInstructionAt < cutoff)
                        {
                            skipped++;
                            continue;
                        }

                        _registry.SetInstructed(new PickingInstruction
                        {
                            Address  = e.Address,
                            Quantity = e.Quantity,
                            Color    = e.Color,
                            LedMode  = e.LedMode,
                        });
                        loaded++;
                    }

                    _log.LogInformation(
                        "[State] Loaded {Loaded} module(s) from {File} ({Skipped} stale skipped)",
                        loaded, Path.GetFileName(_filePath), skipped);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("[State] Could not read state file {File}: {Err}", _filePath, ex.Message);
            }
        }

        // Subscribe AFTER load so we don't save immediately due to load-induced events
        _registry.StateChanged += OnStateChanged;
    }

    // ─── Save ─────────────────────────────────────────────────────────────

    private void OnStateChanged(LightModuleState _) => ScheduleSave();

    private void ScheduleSave()
    {
        lock (_timerLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => Save(), null, dueTime: 300, period: Timeout.Infinite);
        }
    }

    private void Save()
    {
        try
        {
            var entries = _registry.All
                .Where(m => m.Status == ModuleStatus.Operating)
                .Select(m => new PersistedModuleEntry
                {
                    Address            = m.Address,
                    Quantity           = int.TryParse(m.DisplayData.Trim(), out int q) ? q : 0,
                    Color              = m.Color,
                    LedMode            = m.LedMode,
                    LastInstructionAt  = m.LastInstructionAt ?? DateTime.UtcNow,
                })
                .ToList();

            var json = JsonSerializer.Serialize(entries, CoreJsonContext.Default.ListPersistedModuleEntry);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            _log.LogWarning("[State] Could not save state file: {Err}", ex.Message);
        }
    }

    // ─── Disposal ─────────────────────────────────────────────────────────

    public void Dispose()
    {
        _registry.StateChanged -= OnStateChanged;

        lock (_timerLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        // Final flush so the last state change is always persisted
        Save();
    }
}

// ─── Persisted DTO ────────────────────────────────────────────────────────

/// <summary>Serializable snapshot of a single Operating module.</summary>
public sealed class PersistedModuleEntry
{
    public string    Address           { get; set; } = "";
    public int       Quantity          { get; set; }
    public LightColor Color            { get; set; } = LightColor.Green;
    public LightState LedMode          { get; set; } = LightState.On;
    public DateTime  LastInstructionAt { get; set; }
}

// ─── AOT-safe JSON context ────────────────────────────────────────────────

[JsonSerializable(typeof(List<PersistedModuleEntry>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy   = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented          = true)]
internal sealed partial class CoreJsonContext : JsonSerializerContext { }
