using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using P2L.CLI.Commands;
using P2L.CLI.Infrastructure;
using P2L.Core;
using P2L.Core.Services;
using P2L.Hardware;
using P2L.Mqtt;
using P2L.Mqtt.Configuration;
using P2L.Shared.Enums;
using P2L.Shared.Models;
using System.Reflection;
using System.Text.Json;

namespace P2L.UnitTests;

public sealed class CliReliabilityTests
{
    [Fact]
    public void UnwritableAuditLogNeverThrowsIntoCallerAndDisposeIsSafe()
    {
        var path = Path.Combine(Path.GetTempPath(), "p2l-log-failure-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "blocks directory creation");
        try
        {
            using var provider = new RotatingFileLoggerProvider(path, 100);
            var logger = provider.CreateLogger("hardware");
            logger.LogInformation("confirmation");
            logger.LogError(new IOException("inert fault"), "failure");
            provider.Dispose();
            logger.LogInformation("late shutdown log");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ConfigRoundTripKeepsKnownModesAndUnknownModesAreExplicitNoChange()
    {
        var state = new LightModuleState { Address = "0001", AfterConfirm = LightState.On, BuzAfterConfirm = LightState.Flash };
        var record = CsvDeviceRecord.FromState(state);
        Assert.Equal("On", record.AfterConfirm);
        Assert.Equal("Flash", record.BuzAfterConfirm);
        Assert.True(new CsvDeviceRecord { Address = "0001", Color = "Red" }.TryToParameters(state, out var known, out _, out _));
        Assert.Equal(LightState.On, known.AfterConfirm);
        Assert.Equal(LightState.Flash, known.BuzAfterConfirm);
        var legacy = new LightModuleState { Address = "0002" };
        Assert.Null(CsvDeviceRecord.FromState(legacy).AfterConfirm);
        Assert.True(new CsvDeviceRecord { Address = "0002", Color = "Red" }.TryToParameters(legacy, out var unknown, out _, out _));
        Assert.Equal(LightState.NoChange, unknown.AfterConfirm);
        Assert.Equal(LightState.NoChange, unknown.BuzAfterConfirm);
    }

    [Fact]
    public async Task ParseErrorsAbortImportBeforeTransportUnlessSkipInvalidIsExplicit()
    {
        var path = Path.GetTempFileName();
        await using var gateway = new AioiGateway("do-not-connect.invalid", 1, NullLogger.Instance);
        using var engine = new PickingEngine(gateway, new LightModuleRegistry(), NullLogger<PickingEngine>.Instance);
        var csv = new DeviceConfigCsvService(engine, new LightModuleRegistry(), NullLogger<DeviceConfigCsvService>.Instance);
        var import = new ImportCommand(csv, gateway);
        File.WriteAllText(path, "Address,Display\n,1\n0001,2\n");
        try
        {
            Assert.Equal(1, await import.ExecuteAsync(null!, new ImportCommand.Settings { File = path, Yes = true }));
            Assert.Equal(0, await import.ExecuteAsync(null!, new ImportCommand.Settings { File = path, DryRun = true, SkipInvalid = true }));
            Assert.False(gateway.IsConnected);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task OneShotRegistrarConstructsDurableSinkBeforeAnyGatewayCanReceive()
    {
        var path = Path.Combine(Path.GetTempPath(), "p2l-one-shot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        var registry = new LightModuleRegistry();
        var gateway = new AioiGateway("do-not-connect.invalid", 1, NullLogger.Instance);
        var settings = new MqttSettings
        {
            Enabled = true, Host = "do-not-connect.invalid", OfflineBufferPath = Path.Combine(path, "events.jsonl"),
            CommandReceiptPath = Path.Combine(path, "receipts.db"), ClientDbPath = Path.Combine(path, "labels.db")
        };
        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        services.AddSingleton(registry);
        services.AddSingleton(gateway);
        services.AddSingleton(settings);
        services.AddSingleton<PickingEngine>();
        services.AddSingleton<PreemptionGate>();
        services.AddSingleton<CsvPickTaskRunner>();
        services.AddSingleton<DeviceScanService>();
        services.AddSingleton(sp => new DeviceLabelStore(settings.ClientDbPath, NullLogger<DeviceLabelStore>.Instance));
        services.AddSingleton<MqttBridgeService>();
        await using var registrar = new TypeRegistrar(services);
        using var resolver = (TypeResolver)registrar.Build();
        var engine = (PickingEngine)resolver.Resolve(typeof(PickingEngine))!;
        registry.SetInstructed(new PickingInstruction { Address = "0001", Quantity = 1, CorrelationId = "wms", CommandItemId = "A" });
        typeof(AioiGateway).GetMethod("OnPacketReceived", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(gateway,
            [new P2L.Hardware.Protocol.AioiPacket { SequenceNo = 17, Command = "t000100" }]);
        var entries = File.ReadAllLines(settings.OfflineBufferPath).Select(DurableEventBuffer.Parse).ToArray();
        Assert.Contains(entries, entry =>
        {
            using var json = JsonDocument.Parse(Convert.FromBase64String(entry.Payload));
            return json.RootElement.GetProperty("type").GetString() == "confirmed"
                && json.RootElement.GetProperty("commandItemId").GetString() == "A";
        });
        Assert.False(gateway.IsConnected);
        await registrar.DisposeAsync();
        await gateway.DisposeAsync();
        Directory.Delete(path, true);
    }
}
