# AGENTS.md - Pick2Light (P2L) Control System

## 1. Project Overview
- **Name:** Pick2Light (P2L)
- **Domain:** Warehouse Operations (Logistics)
- **Hardware:** Aioi Systems Light Modules & Gateways (AIW/AW Series).
- **Target:** Cross-platform (.NET 8/9 Native AOT) for Windows/Linux/macOS.
- **Role:** Control light modules (Turn ON/OFF, Change Color, Display Digits) and receive Confirmation (Button Press).

## 2. Technical Stack
- **Core:** .NET 8/9 (Native AOT compatible).
- **Network:** `System.Net.Sockets` (TCP/UDP) for Gateway communication.
- **UI:** `Spectre.Console` for warehouse operator dashboard & logging.
- **Protocol:** Aioi Systems Communication Protocol (Binary/Command-based).

## 3. Hardware Interaction Rules (Aioi Systems)
- **TCP Connection:** Implement a robust `TcpClient` with auto-reconnect logic. Gateways are usually the Server (Static IP).
- **Command Framing:** Follow Aioi's specific byte structure (Header, Device ID, Command, Data, Checksum/ETX).
- **Keep-Alive:** Send heartbeat packets to Gateways to prevent connection timeout.
- **Concurrency:** Use `System.Threading.Channels` to queue light commands to avoid flooding the hardware.

## 4. Coding Standards for P2L
- **AOT Compatibility:** Avoid `System.Reflection.Emit`. Use Source Generators for JSON/Message parsing if needed.
- **State Management:** Track the state of each Light Module (ID, Status, Last Confirmed) in an efficient `ConcurrentDictionary`.
- **Endianness:** Aioi hardware may use Little-endian or Big-endian; use `BinaryPrimitives` for safe conversion.
- **Logging:** Log all Hardware TX/RX to a rotating file for audit trails in case of "mis-picking".

## 5. Architectural Layers
- **P2L.CLI:** Dashboard, manual overrides, and configuration.
- **P2L.Hardware:** `AioiProtocolHandler`, `TcpTransport`, `SerialTransport`.
- **P2L.Core:** Business logic (Picking logic, Batching, Route optimization).
- **P2L.Shared:** DTOs and Shared Constants (Color codes, Command types).

## 6. Build & Deploy
- **Native Binary:** Publish as a single-file executable to run on warehouse handhelds or industrial PCs without .NET Runtime.
- **Linux Service:** Must be able to run as a `systemd` daemon for 24/7 warehouse uptime.