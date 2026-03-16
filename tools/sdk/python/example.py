"""P2L Python SDK — usage examples."""

import asyncio
from p2l import P2LClient, LightColor, LightMode, BatchItem, TLSConfig


# ---------------------------------------------------------------------------
# Example 1: Turn on a single light and wait for confirmation
# ---------------------------------------------------------------------------
async def example_single_pick():
    async with P2LClient("localhost", 8883) as p2l:
        print("Lighting up module 0001 — qty 5, green flash")
        p2l.light_on("0001", quantity=5, color=LightColor.GREEN, led_mode=LightMode.FLASH)

        print("Waiting for operator confirmation…")
        event = await p2l.wait_confirm("0001", timeout=30)
        print(f"Confirmed: {event}")


# ---------------------------------------------------------------------------
# Example 2: Batch pick — light several modules, wait for all
# ---------------------------------------------------------------------------
async def example_batch_pick():
    async with P2LClient("localhost", 8883) as p2l:
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
# Example 3: React to any confirmation in real-time
# ---------------------------------------------------------------------------
async def example_event_stream():
    p2l = P2LClient("localhost", 8883)

    def on_confirm(ev):
        print(f"Button pressed: addr={ev.address} status={ev.status.value} adj={ev.adjusted_display}")

    def on_bridge(bs):
        print(f"Bridge: online={bs.online} gateway={bs.gateway_connected}")

    p2l.on_confirm(on_confirm)
    p2l.on_bridge_status(on_bridge)

    await p2l.connect()
    try:
        print("Listening for 60 seconds…")
        await asyncio.sleep(60)
    finally:
        await p2l.disconnect()


# ---------------------------------------------------------------------------
# Example 4: With TLS + credentials
# ---------------------------------------------------------------------------
async def example_tls():
    tls = TLSConfig(ca_cert="/path/to/ca.pem")
    async with P2LClient("broker.example.com", 8883,
                         username="user", password="pass", tls=tls) as p2l:
        p2l.light_on("0001", quantity=1, color=LightColor.BLUE)
        await asyncio.sleep(5)
        p2l.light_off("0001")


if __name__ == "__main__":
    asyncio.run(example_single_pick())
