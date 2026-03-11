using P2L.Hardware;
using P2L.IntegrationTests.Infrastructure;
using P2L.Shared.Enums;
using P2L.Shared.Models;
using Xunit;
using Xunit.Abstractions;

namespace P2L.IntegrationTests;

/// <summary>
/// End-to-end light toggle tests via the full AioiGateway stack.
///
/// Each test lights a module, optionally pauses so you can visually verify it,
/// then terminates the module. A failure at any point still runs cleanup.
///
/// Tests:
///   1. Single module ON (green, steady) → verify ACK → terminate
///   2. Single module ON (red, flash) → verify ACK → terminate
///   3. Block of 3 modules ON via PP1 → verify all ACK → terminate block
///   4. Mode cycle: Green → Red → Off on same address (PP5 overwrite)
///   5. Controller status inquiry (Az) — does not change module state
///
/// Requires: gateway powered on, module at address in testconfig.json connected.
/// </summary>
[Collection("Hardware")]
public sealed class LightToggleTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture    _fixture;
    private readonly ITestOutputHelper _out;
    private readonly TestConfig        _cfg;

    public LightToggleTests(GatewayFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _out     = output;
        _cfg     = fixture.Config;
        _out.WriteLine($"Config : {_cfg}");
        _out.WriteLine($"Gateway ready: {_fixture.Ready}");
    }

    // ── Test 1: Single module — green steady ──────────────────────────────

    [SkippableFact]
    [Trait("Category", "Hardware")]
    public async Task SingleModule_GreenSteady_LightsAndTerminates()
    {
        SkipIfNotReady();
        var addr = _cfg.TestModuleAddr;

        _out.WriteLine($"[1] Lighting {addr} GREEN STEADY for {_cfg.LightOnMs} ms...");

        await _fixture.Gateway.InstructSingleAsync(new PickingInstruction
        {
            Address  = addr,
            Quantity = 1,
            Color    = LightColor.Green,
            LedMode  = LightState.On,
        });

        _out.WriteLine("     → ACK received. Light should be GREEN.");
        await Task.Delay(_cfg.LightOnMs);

        _out.WriteLine("     → Terminating...");
        await _fixture.Gateway.TerminateAsync(addr);
        _out.WriteLine("     → Light OFF. PASS.");
    }

    // ── Test 2: Single module — red flash ─────────────────────────────────

    [SkippableFact]
    [Trait("Category", "Hardware")]
    public async Task SingleModule_RedFlash_LightsAndTerminates()
    {
        SkipIfNotReady();
        var addr = _cfg.TestModuleAddr;

        _out.WriteLine($"[2] Lighting {addr} RED FLASH for {_cfg.LightOnMs} ms...");

        await _fixture.Gateway.InstructSingleAsync(new PickingInstruction
        {
            Address  = addr,
            Quantity = 5,
            Color    = LightColor.Red,
            LedMode  = LightState.Flash,
        });

        _out.WriteLine("     → ACK received. Light should be RED FLASHING.");
        await Task.Delay(_cfg.LightOnMs);

        await _fixture.Gateway.TerminateAsync(addr);
        _out.WriteLine("     → Light OFF. PASS.");
    }

    // ── Test 3: Block of modules via PP1 ──────────────────────────────────

    [SkippableFact]
    [Trait("Category", "Hardware")]
    public async Task BlockInstruct_ThreeModules_AllLightAndTerminate()
    {
        SkipIfNotReady();

        // Use addresses 0001, 0002, 0003 — adjust testconfig if modules differ
        var instructions = new[]
        {
            MakeInstruction("0001", 10, LightColor.Green),
            MakeInstruction("0002", 20, LightColor.Yellow),
            MakeInstruction("0003", 30, LightColor.Red),
        };

        _out.WriteLine($"[3] PP1 block: lighting modules 0001/0002/0003 for {_cfg.LightOnMs} ms...");
        _out.WriteLine("     Expected: Green=10, Yellow=20, Red=30");

        await _fixture.Gateway.InstructBatchAsync(instructions);
        _out.WriteLine("     → ACK received. Three lights should be on.");

        await Task.Delay(_cfg.LightOnMs);

        _out.WriteLine("     → Terminating block 00...");
        await _fixture.Gateway.TerminateBlockAsync("00");
        _out.WriteLine("     → All lights OFF. PASS.");
    }

    // ── Test 4: PP5 overwrite — colour cycle on same address ──────────────
    // PP5 overwrites an *in-progress* instruction. The first instruction must
    // be started with PP1 (InstructSingleAsync); subsequent changes use PP5.

    [SkippableFact]
    [Trait("Category", "Hardware")]
    public async Task SingleModule_ColorCycle_OverwritesCorrectly()
    {
        SkipIfNotReady();
        var addr = _cfg.TestModuleAddr;
        int pauseMs = Math.Max(700, _cfg.LightOnMs / 3);

        _out.WriteLine($"[4] Colour cycle on {addr}: GREEN → BLUE → RED → OFF (pause={pauseMs} ms each)");

        // First: start with PP1 (InstructSingleAsync internally uses M + L)
        _out.WriteLine("     → GREEN (qty=1) via PP1...");
        await _fixture.Gateway.InstructSingleAsync(new PickingInstruction
        {
            Address  = addr,
            Quantity = 1,
            Color    = LightColor.Green,
            LedMode  = LightState.On,
            BlockNo  = "00",
        });
        await Task.Delay(pauseMs);

        // Subsequent: overwrite the active instruction with PP5
        foreach (var (color, qty, label) in new[]
        {
            (LightColor.Blue, 2, "BLUE"),
            (LightColor.Red,  3, "RED"),
        })
        {
            _out.WriteLine($"     → {label} (qty={qty}) via PP5...");
            await _fixture.Gateway.OverwriteBatchAsync(
            [
                new PickingInstruction
                {
                    Address  = addr,
                    Quantity = qty,
                    Color    = color,
                    LedMode  = LightState.On,
                    BlockNo  = "00",
                }
            ]);
            await Task.Delay(pauseMs);
        }

        await _fixture.Gateway.TerminateAsync(addr);
        _out.WriteLine("     → Light OFF. PASS.");
    }

    // ── Test 5: Module status inquiry (Az) ────────────────────────────────
    // Az polls the physical module for status. Use address 0000 (Number 0 error
    // display — always present in the gateway, no physical module required).

    [SkippableFact]
    [Trait("Category", "Hardware")]
    public async Task ModuleStatusInquiry_ErrorDisplayModule_ReturnsStatus()
    {
        SkipIfNotReady();
        const string errorDisplayAddr = "0000";

        _out.WriteLine($"[5] Querying Number 0 (error display) module status (Az{errorDisplayAddr})...");

        var response = await _fixture.Gateway.QueryModuleStatusAsync(errorDisplayAddr);

        _out.WriteLine($"     → Response kind : {response.Kind}");
        _out.WriteLine($"     → Raw response  : \"{response.Raw}\"");

        Assert.NotNull(response);

        // Az response is "a[addr][status]" e.g. "a0000?" (status '?' = 0x3F not present)
        // Parsed as ModuleStatus kind; '?' means the virtual error-display has no physical module
        Assert.Equal(P2L.Hardware.Protocol.AioiDecoder.ResponseKind.ModuleStatus, response.Kind);

        if (response.Raw.Length >= 6)
        {
            char statusByte = response.Raw[5];
            _out.WriteLine($"     → Status byte: 0x{(byte)statusByte:X2} ('{statusByte}')");
        }
        _out.WriteLine("     → PASS.");
    }

    // ── Test 6: Confirm interrupt listening ───────────────────────────────

    [SkippableFact]
    [Trait("Category", "Hardware")]
    public async Task SingleModule_WaitsForConfirmButtonPress()
    {
        SkipIfNotReady();
        var addr = _cfg.TestModuleAddr;

        _out.WriteLine($"[6] Lighting {addr} GREEN — press the CONFIRM button within 15 s...");
        _out.WriteLine("     (If no button press detected, test passes with a note — not a failure.)");

        var tcs = new TaskCompletionSource<ConfirmationEvent>();
        _fixture.Gateway.Confirmed += evt =>
        {
            if (evt.Address == addr.PadLeft(4, '0'))
                tcs.TrySetResult(evt);
        };

        await _fixture.Gateway.InstructSingleAsync(new PickingInstruction
        {
            Address  = addr,
            Quantity = 1,
            Color    = LightColor.Green,
            LedMode  = LightState.Flash,
        });

        _out.WriteLine("     → Module is flashing. Press the button now.");

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(15_000));

        await _fixture.Gateway.TerminateAsync(addr);

        if (completed == tcs.Task)
        {
            var evt = tcs.Task.Result;
            _out.WriteLine($"     → CONFIRM received! Address={evt.Address} Status={evt.Status}");
            Assert.Equal(CompletionStatus.Normal, evt.Status);
        }
        else
        {
            _out.WriteLine("     → No button press detected within 15 s.");
            _out.WriteLine("       (This is informational — hardware needs manual interaction.)");
            // Not a hard failure: physical interaction may not be available in CI
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void SkipIfNotReady()
    {
        Skip.If(!_fixture.Ready,
            $"Gateway at {_cfg.GatewayHost}:{_cfg.GatewayPort} is not reachable. " +
            "Ensure the device is powered on, the USB-LAN adapter is configured (192.168.1.100/24), " +
            "and re-run: dotnet test --filter Category=Hardware");
    }

    private static PickingInstruction MakeInstruction(
        string addr, int qty, LightColor color) => new()
    {
        Address  = addr,
        Quantity = qty,
        Color    = color,
        LedMode  = LightState.On,
        BlockNo  = "00",
    };
}

/// <summary>
/// Ensures all hardware tests in this assembly share one xUnit collection
/// (run sequentially) to avoid concurrent commands on the same gateway.
/// </summary>
[CollectionDefinition("Hardware")]
public sealed class HardwareCollection : ICollectionFixture<GatewayFixture> { }
