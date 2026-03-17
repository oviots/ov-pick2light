"""P2L Python SDK — usage examples."""

import asyncio
import signal
from p2l import P2LClient, LightColor, LightMode, BatchItem, TLSConfig

# EMQX broker (from appsettings.json / note.txt)
BROKER   = "s28f7f16.ala.asia-southeast1.emqxsl.com"
PORT     = 8883          # TLS auto-enabled at port 8883
USERNAME = "pick2light"
PASSWORD = "P2l123456"


# ---------------------------------------------------------------------------
# Example 1: Turn on a single light and wait for confirmation
# ---------------------------------------------------------------------------
async def example_single_pick():
    async with P2LClient(BROKER, PORT, username=USERNAME, password=PASSWORD) as p2l:
        print("Lighting up module 0001 — qty 5, green flash")
        p2l.light_on("0001", quantity=5, color=LightColor.GREEN, led_mode=LightMode.FLASH)

        print("Waiting for operator confirmation…")
        event = await p2l.wait_confirm("0001", timeout=30)
        print(f"Confirmed: {event}")


# ---------------------------------------------------------------------------
# Example 2: Batch pick — light several modules, wait for all
# ---------------------------------------------------------------------------
async def example_batch_pick():
    async with P2LClient(BROKER, PORT, username=USERNAME, password=PASSWORD) as p2l:
        items = [
            BatchItem("0001", quantity=3, color=LightColor.GREEN, label="SKU-A"),
            BatchItem("0002", quantity=1, color=LightColor.YELLOW, label="FRAGILE"),
            BatchItem("0003", quantity=7, color=LightColor.RED),
        ]

        p2l.on_confirm(lambda ev: print(f"  ✓ {ev.address} confirmed ({ev.status.value})"))

        print("Starting batch…")
        p2l.batch_start(items, led_mode=LightMode.FLASH, buz_mode=LightMode.OFF)

        print("Waiting for all confirmations…")
        await p2l.wait_batch_complete(timeout=120)
        print("Batch complete!")


# ---------------------------------------------------------------------------
# Example 3: React to any confirmation in real-time (runs until Ctrl-C)
# ---------------------------------------------------------------------------
async def example_event_stream():
    p2l = P2LClient(BROKER, PORT, username=USERNAME, password=PASSWORD)

    p2l.on_confirm(lambda ev:
        print(f"[confirm]  addr={ev.address}  status={ev.status.value}  adj={ev.adjusted_display}"))
    p2l.on_state_change(lambda s:
        print(f"[state]    addr={s.address}  status={s.module_status}  color={s.color}  qty={s.display_data}"))
    p2l.on_task_event(lambda ev:
        print(f"[task]     {ev.event_type}  addr={ev.address}  qty={ev.quantity}  label={ev.label}"))
    p2l.on_bridge_status(lambda bs:
        print(f"[bridge]   online={bs.online}  gateway={bs.gateway_connected}"))

    stop = asyncio.Event()
    loop = asyncio.get_running_loop()
    for sig in (signal.SIGINT, signal.SIGTERM):
        loop.add_signal_handler(sig, stop.set)

    await p2l.connect()
    print("Listening… press Ctrl-C to stop.")
    try:
        await stop.wait()
    finally:
        await p2l.disconnect()
        print("Disconnected.")


# ---------------------------------------------------------------------------
# Example 4: With TLS + credentials
# ---------------------------------------------------------------------------
async def example_tls():
    # Custom CA cert — only needed if broker uses a self-signed certificate.
    # For EMQX cloud (trusted CA), TLSConfig() with no args is sufficient.
    tls = TLSConfig(ca_cert="/path/to/ca.pem")
    async with P2LClient(BROKER, PORT, username=USERNAME, password=PASSWORD, tls=tls) as p2l:
        p2l.light_on("0001", quantity=1, color=LightColor.BLUE)
        await asyncio.sleep(5)
        p2l.light_off("0001")


# ---------------------------------------------------------------------------
# Example 5: Monitor real-time data processing (CSV task lifecycle)
# ---------------------------------------------------------------------------
async def example_data_processing():
    async with P2LClient(BROKER, PORT, username=USERNAME, password=PASSWORD) as p2l:
        # Fine-grained callbacks for each step
        p2l.on_task_started(
            lambda ev: print(f"  → Started  [{ev.address}] qty={ev.quantity} color={ev.color} label={ev.label}")
        )
        p2l.on_task_confirmed(
            lambda ev: print(f"  ✓ Confirmed [{ev.address}] qty={ev.quantity}")
        )
        p2l.on_task_error(
            lambda ev: print(f"  ✗ Error     [{ev.address}] {ev.label}")
        )
        p2l.on_csv_complete(
            lambda: print("  ★ All CSV tasks completed!")
        )

        print("Listening for CSV data-processing events (60 s)…")
        await asyncio.sleep(60)


# ---------------------------------------------------------------------------
# Example 6: Wait for a full CSV run to finish programmatically
# ---------------------------------------------------------------------------
async def example_wait_csv_complete():
    async with P2LClient(BROKER, PORT, username=USERNAME, password=PASSWORD) as p2l:
        p2l.on_task_event(
            lambda ev: print(f"  [{ev.event_type}] {ev.address} qty={ev.quantity}")
        )

        print("Waiting for CSV pick task to finish (up to 5 min)…")
        try:
            await p2l.wait_csv_complete(timeout=300)
            print("CSV run complete!")
        except TimeoutError as exc:
            print(f"Timed out: {exc}")

if __name__ == "__main__":
    asyncio.run(example_event_stream())
