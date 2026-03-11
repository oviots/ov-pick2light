using System.Text.Json.Serialization;

namespace P2L.Mqtt.Messages;

/// <summary>
/// AOT-safe JSON source-generation context for all MQTT message types.
/// Avoids runtime reflection for both serialization (events) and deserialization (commands).
/// </summary>
[JsonSerializable(typeof(LightOnCmd))]
[JsonSerializable(typeof(LightOffCmd))]
[JsonSerializable(typeof(BatchStartCmd))]
[JsonSerializable(typeof(BatchItemDto))]
[JsonSerializable(typeof(List<BatchItemDto>))]
[JsonSerializable(typeof(BatchFinishCmd))]
[JsonSerializable(typeof(TerminateAllCmd))]
[JsonSerializable(typeof(GetSnapshotCmd))]
[JsonSerializable(typeof(ConfirmedEvt))]
[JsonSerializable(typeof(LightChangedEvt))]
[JsonSerializable(typeof(BatchCompletedEvt))]
[JsonSerializable(typeof(BridgeStatusEvt))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy         = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter       = true,
    DefaultIgnoreCondition       = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented                = false)]
internal sealed partial class P2LMqttJsonContext : JsonSerializerContext { }
