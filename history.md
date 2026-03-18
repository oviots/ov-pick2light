# Pick2Light (P2L) — Project History & Architecture

---

## Project Genesis

The P2L system was born from a real warehouse operations problem: **mis-picks**. In high-throughput logistics, workers picking items from shelves make costly mistakes — wrong item, wrong quantity, wrong shelf — especially in multi-SKU environments where dozens of bins look identical.

The solution was a hardware-assisted guidance system: **Pick-to-Light**. LED modules mounted on each bin light up with the target quantity displayed on a 7-segment display. The worker picks the exact amount, then presses the confirmation button on the module. The light turns off. Move on. Simple, fast, error-proof.

The hardware of choice was the **Aioi Systems AIW/AW Series** — industrial-grade light modules and TCP gateways designed for warehouse automation. However, Aioi only ships a Windows-only DLL library, tightly coupled to a GUI tool. For a warehouse running Linux PLCs and industrial handhelds on 24/7 operation, this was unacceptable.

The goal: **build a cross-platform, Native AOT-compatible CLI system in .NET that speaks the raw Aioi binary protocol over TCP, with a live operator dashboard, crash recovery, and optional MQTT integration for WMS connectivity.**

---

## Evolution Timeline

| Phase | What Was Built |
|---|---|
| **v0.1 — Protocol Reverse Engineering** | Decoded Aioi binary frame format from hardware docs. Built `AioiPacket`, `AioiEncoder`, `AioiDecoder`. Confirmed STX/ETX framing, ASCII seq numbers, command types. |
| **v0.2 — TCP Transport** | `TcpTransport` with Channel-based send queue, auto-reconnect, incremental frame parser. Proved reliable under 24/7 conditions without connection drops. |
| **v0.3 — Gateway Orchestrator** | `AioiGateway` with seq-keyed `TaskCompletionSource` request/response matching. The "t" interrupt (button press) → immediate "O" ACK path established. |
| **v0.4 — Core Business Logic** | `LightModuleRegistry` (thread-safe state), `PickingEngine` (batch orchestration, P1→P5 fallback), `HeartbeatService` (keep-alive). |
| **v0.5 — CLI Dashboard** | Spectre.Console live warehouse view. Operator can see all module states in real-time. First usable version deployed to a test shelf. |
| **v0.6 — CSV Pick Tasks** | `CsvPickTaskRunner` reads a pick list from CSV, lights all modules at once, auto-deletes rows as confirmations arrive. Integrated into WMS via file-drop. |
| **v0.7 — Self-Healing** | `SelfHealHandler` + `WatchdogService`. Process restarts itself on crash or prolonged TCP disconnect. Crash-loop guard prevents runaway restart spiral. |
| **v0.8 — MQTT Bridge** | Optional `P2L.Mqtt` project — connects to broker, subscribes to commands, publishes events. Enables real-time WMS integration without file polling. |
| **v1.0 — AOT Hardening** | Removed all reflection. Source-gen JSON, manual config key indexer, BinaryPrimitives for endianness. Published as single-file native binary for Linux systemd. |

---

## Architecture Flow

```
┌──────────────────────────────────────────────────────────────────────────┐
│                        P2L SYSTEM ARCHITECTURE                           │
└──────────────────────────────────────────────────────────────────────────┘

  ┌─────────────────────────────────────────────────────────────────────┐
  │                         P2L.CLI (Entry Point)                       │
  │                                                                     │
  │  Program.cs ──► DI Container ──► Spectre.Cli App                   │
  │      │                                                              │
  │      ├── SelfHealHandler (crash recovery, watchdog restart)        │
  │      ├── RotatingFileLogger (audit trail: all TX/RX)               │
  │      ├── AppSettings (appsettings.json + P2L_* env vars)           │
  │      └── CancellationToken (SIGTERM / Ctrl-C graceful shutdown)    │
  │                                                                     │
  │  Commands:                                                          │
  │  ┌─────────────────┐  ┌──────────────────┐  ┌───────────────────┐ │
  │  │ RootCommand     │  │ LightCommands     │  │ RunPickCommand    │ │
  │  │ (dashboard mode)│  │ on/off/terminate  │  │ (CSV batch mode)  │ │
  │  └────────┬────────┘  └────────┬─────────┘  └────────┬──────────┘ │
  │           │                    │                      │            │
  │           └────────────────────┴──────────────────────┘            │
  │                                │                                   │
  │              ┌─────────────────▼──────────────────┐               │
  │              │         WarehouseDashboard           │               │
  │              │  (Spectre.Live 250ms refresh)        │               │
  │              │  ┌────────────────────────────────┐ │               │
  │              │  │ Header: connection + counts    │ │               │
  │              │  │ Table:  address | qty | color  │ │               │
  │              │  │         | mode | status | time │ │               │
  │              │  │ Events: last 20 confirmations  │ │               │
  │              │  │ Footer: keyboard shortcuts     │ │               │
  │              │  └────────────────────────────────┘ │               │
  │              └────────────────────────────────────┘               │
  └───────────────────────────┬─────────────────────────────────────────┘
                              │ calls
  ┌───────────────────────────▼─────────────────────────────────────────┐
  │                         P2L.Core (Business Logic)                   │
  │                                                                     │
  │  ┌──────────────────────────────────────────────────────────────┐  │
  │  │  PickingEngine                                               │  │
  │  │  ┌─────────────────────────────────────────────────────────┐│  │
  │  │  │ _pendingAddresses: HashSet<string>                      ││  │
  │  │  │                                                         ││  │
  │  │  │ StartBatchAsync(instructions[])                         ││  │
  │  │  │   ├── update registry (optimistic)                      ││  │
  │  │  │   └── send P1/P5 command to gateway                     ││  │
  │  │  │                                                         ││  │
  │  │  │ OnConfirmed(ConfirmationEvent)                          ││  │
  │  │  │   ├── remove from _pending                              ││  │
  │  │  │   ├── registry.SetConfirmed()                           ││  │
  │  │  │   └── if pending==0 → fire BatchCompleted               ││  │
  │  │  └─────────────────────────────────────────────────────────┘│  │
  │  └──────────────────────────────────────────────────────────────┘  │
  │                                                                     │
  │  ┌────────────────────────┐   ┌──────────────────────────────────┐ │
  │  │  LightModuleRegistry   │   │  HeartbeatService                │ │
  │  │  ConcurrentDictionary  │   │  every 30s → Az0000              │ │
  │  │  address → State       │   │  (keep-alive, no-op read)        │ │
  │  │  StateChanged event    │   └──────────────────────────────────┘ │
  │  └────────────────────────┘                                        │
  │                                                                     │
  │  ┌────────────────────────┐   ┌──────────────────────────────────┐ │
  │  │  WatchdogService       │   │  CsvPickTaskRunner               │ │
  │  │  monitors disconnect   │   │  reads CSV → StartBatchAsync()   │ │
  │  │  ≥5min → RestartReq    │   │  deletes rows on confirm         │ │
  │  └────────────────────────┘   └──────────────────────────────────┘ │
  └───────────────────────────┬─────────────────────────────────────────┘
                              │ calls
  ┌───────────────────────────▼─────────────────────────────────────────┐
  │                       P2L.Hardware (Protocol)                       │
  │                                                                     │
  │  ┌─────────────────────────────────────────────────────────────┐   │
  │  │  AioiGateway                                                │   │
  │  │                                                             │   │
  │  │  _pending: ConcurrentDict<seqNo, TaskCompletionSource>      │   │
  │  │                                                             │   │
  │  │  SendCommandAsync(cmd)                                      │   │
  │  │    ├── allocate seq  ──────────────────────────────────┐    │   │
  │  │    ├── register TCS in _pending                        │    │   │
  │  │    ├── enqueue to TcpTransport                         │    │   │
  │  │    └── await TCS (5s timeout)  ◄───────── ACK "o" ────┘    │   │
  │  │                                                             │   │
  │  │  OnPacketReceived(packet)                                   │   │
  │  │    ├── "t" interrupt ──► SendImmediateAsync("O" ACK)        │   │
  │  │    │                 ──► fire Confirmed event               │   │
  │  │    ├── "o" ACK      ──► complete pending TCS               │   │
  │  │    └── "n" NACK     ──► fail pending TCS (AioiNackException)│   │
  │  └─────────────────────────────────────────────────────────────┘   │
  │                                                                     │
  │  ┌──────────────────────────────────────────────────────────────┐  │
  │  │  TcpTransport                                                │  │
  │  │                                                              │  │
  │  │  SendChannel (bounded 512) ──► SendLoop ──► NetworkStream   │  │
  │  │                                                              │  │
  │  │  RecvLoop ──► TryParse(AioiPacket) ──► PacketReceived event │  │
  │  │                                                              │  │
  │  │  EnsureConnectedAsync(): exp backoff (1s→2s→4s→...→30s max) │  │
  │  │  AllocSeq(): Interlocked 0–999 wrap                         │  │
  │  └──────────────────────────────────────────────────────────────┘  │
  │                                                                     │
  │  AioiEncoder ──► raw ASCII command strings                         │
  │  AioiDecoder ──► ParsedResponse (ACK / NACK / Interrupt / Status)  │
  │  AioiPacket  ──► STX|SeqNo(3)|DataLen(4)|Command|ETX              │
  └───────────────────────────┬─────────────────────────────────────────┘
                              │ TCP port 5003
  ┌───────────────────────────▼─────────────────────────────────────────┐
  │                 Aioi Hardware (Physical Layer)                       │
  │                                                                     │
  │  ┌─────────────────────┐    ┌──────────────────────────────────┐   │
  │  │  Aioi Gateway       │    │  Light Modules (AIW/AW Series)   │   │
  │  │  (TCP Server)       │◄──►│  ■ 7-segment display (qty)       │   │
  │  │  192.168.1.254:5003 │    │  ■ RGB LED (color signal)        │   │
  │  │                     │    │  ■ Buzzer (optional)              │   │
  │  │  Bridges TCP to     │    │  ■ Confirm button (press = done) │   │
  │  │  RS-485 bus         │    │                                  │   │
  │  └─────────────────────┘    └──────────────────────────────────┘   │
  └─────────────────────────────────────────────────────────────────────┘

  OPTIONAL: MQTT Bridge
  ┌─────────────────────────────────────────────────────────────────────┐
  │  P2L.Mqtt                                                           │
  │  MqttBridgeService ◄──► Broker ◄──► WMS / ERP / External System   │
  │  topics: p2l/commands (inbound) / p2l/events (outbound)            │
  │  OfflineBufferService: JSONL queue when broker unavailable          │
  └─────────────────────────────────────────────────────────────────────┘
```

---

## Pick Batch Usage Flow

```
OPERATOR / WMS                  P2L SYSTEM                    HARDWARE
─────────────────────────────────────────────────────────────────────────

 Run: p2l run tasks.csv         ─── loads CSV rows ──►
                                    PickingEngine.StartBatchAsync()
                                    registry.SetInstructed()         ──► P1 command (batch instruct)
                                                                         [ Module A: qty=5, Green ]
                                                                         [ Module B: qty=3, Yellow ]
                                                                         [ Module C: qty=8, Red ]

                                ◄── "o" ACK ─────────────────────────── Gateway ACK

                                    Dashboard: A=Operating(5) B=Operating(3) C=Operating(8)

 Worker walks to Module A ──────────────────────────────────────────────►  [GREEN LED ON, "5" displayed]
 Worker picks 5 items
 Worker presses button ─────────────────────────────────────────────────►  button press event

                                ◄── "t" interrupt ───────────────────────
                                    IMMEDIATE: SendImmediateAsync("O") ──► ACK sent (highest priority)
                                    fire Confirmed(A, Normal, qty=5)
                                    registry.SetConfirmed(A)
                                    _pending removes A
                                    Dashboard: A=Completed(5 ✓)          ──► [Module A LED OFF]

 Worker walks to Module B       ────────────────────────────────────────►  [YELLOW LED ON, "3" displayed]
 Worker picks 3, presses button ────────────────────────────────────────►
                                ◄── "t" interrupt, Confirmed(B) ─────────
                                    Dashboard: B=Completed(3 ✓)

 Worker walks to Module C       ────────────────────────────────────────►  [RED LED ON, "8" displayed]
 Worker picks 8, presses button ────────────────────────────────────────►
                                ◄── "t" interrupt, Confirmed(C) ─────────
                                    _pending == 0
                                    fire BatchCompleted event
                                    CSV rows deleted (confirmed)
                                    Dashboard: ALL modules Standby

─────────────────────────────────────────────────────────────────────────
```

---

## Self-Healing Flow

```
RUNTIME                         SELF-HEAL SYSTEM
──────────────────────────────────────────────────────────────────────

  UnhandledException / NACK storm / TCP disconnect

  Case A: Crash
  ─────────────
  Exception thrown ──────────► SelfHealHandler.OnUnhandledException()
                                  write crash_history.jsonl entry
                                  count crashes in last 10 min
                                  if count < 5:
                                    sleep 3s (backoff)
                                    spawn Process.Start(same args)
                                    Environment.Exit(1)
                                  else:
                                    LOG "crash loop detected"
                                    Environment.Exit(1)  ◄── ops team alert

  Case B: Prolonged Disconnect
  ────────────────────────────
  TCP drops ─────────────────► TcpTransport: retry loop (exp backoff)
  still disconnected at 5min ► WatchdogService fires RestartRequested
                              ► SelfHealHandler.TriggerWatchdog(reason)
                                  write crash_history.jsonl entry
                                  restart process (same as Case A)

  Case C: Normal Reconnect
  ────────────────────────
  TCP drops ─────────────────► TcpTransport: retry loop
  reconnected ───────────────► WatchdogService: reset disconnect timer
                              ► HeartbeatService: resume Az0000 keep-alive
                              ► Gateway: re-send Z (initialize)
                              ► Operations resume normally
```

---

## Frame Protocol Anatomy

```
WIRE FORMAT (one command packet):

  ┌──────┬──────────────┬──────────────────┬─────────────────────┬──────┐
  │ STX  │  SeqNo (3)   │  DataLen (4)     │  Command (variable) │ ETX  │
  │ 0x02 │ ASCII "000"  │ ASCII "0012"     │  "PP10001050101001" │ 0x03 │
  └──────┴──────────────┴──────────────────┴─────────────────────┴──────┘
     1B        3B              4B                   N bytes          1B

  Command types:
  ┌────┬────────────────────────────────────────────────────────────────┐
  │ Z  │ Initialize gateway                                             │
  │ Z1 │ Hardware reset                                                 │
  │ M  │ Set mode array (binary LED state per channel per address)      │
  │ PP1│ Block instruct (light batch of modules)                        │
  │ PP5│ Block overwrite (replace active batch)                         │
  │ L  │ Single module instruct                                         │
  │ D  │ Terminate (turn off) module or block                           │
  │ Az │ Module status inquiry (keep-alive + discovery)                 │
  ├────┼────────────────────────────────────────────────────────────────┤
  │ o  │ ACK response (gateway → client)                                │
  │ n  │ NACK response (error)                                          │
  │ t  │ Confirm interrupt (button press — PUSHED by gateway)           │
  │ O  │ ACK the "t" interrupt (must send immediately)                  │
  │ a  │ Module status response                                         │
  └────┴────────────────────────────────────────────────────────────────┘
```

---

## Project Key Stats

| Metric | Value |
|---|---|
| Target Runtime | .NET 10, Native AOT |
| Transport | TCP port 5003 |
| Protocol | Binary/ASCII hybrid |
| Max Modules | 7999 addresses |
| Keep-alive interval | 30 seconds |
| Reconnect backoff | 1s → 30s max |
| Response timeout | 5 seconds |
| Send queue depth | 512 commands |
| Seq number range | 000–999 (wraps) |
| Crash loop guard | 5 crashes / 10 min |
| Log rotation | 10 MB per file |
| Deploy format | Single native binary |
| Service type | Linux systemd / Windows SCM |
