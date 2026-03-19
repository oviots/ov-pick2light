# Pick2Light (P2L) — One-Page Summary

## What P2L Is

P2L is a pick-to-light platform for warehouse operations. It uses lights to guide operators to the correct pick locations and captures confirmations directly at the device.

## Core Value

- faster picking operations
- fewer operational errors
- real-time visibility
- easier integration with **SFT Pickwave / Splitting MML**

## High-Level Model

```text
SFT Pickwave / Splitting MML / Dashboard
        │
        ▼
   MQTT / Web UI
        │
        ▼
   P2L Bridge (.NET)
        │
        ▼
    AIOI Gateway
        │
        ▼
    Light Modules
```

## What MQTT Is

MQTT is a lightweight publish/subscribe messaging standard. In P2L, it allows business systems to send commands and receive status updates without connecting directly to the hardware layer.

## Main Components

- **Web Client / Dashboard**: operations and monitoring interface
- **P2L Bridge**: central orchestration and hardware control layer
- **MQTT Layer**: integration channel for external systems
- **AIOI Gateway + Light Modules**: physical execution layer on the shop floor

## Operating Flow

1. a system creates a picking instruction
2. the bridge sends the instruction to the gateway
3. lights turn on at the required locations
4. the operator confirms on the module
5. status flows back to the dashboard and upstream systems

## Operational Strengths

- reconnect support
- heartbeat
- logging
- 24/7-friendly runtime design
- self-contained deployment model

## Summary

P2L is a practical pick-to-light platform that improves productivity, reduces errors, and creates a strong foundation for future warehouse integration.
