"""P2L Python SDK — usage examples."""

import asyncio
import signal
from p2l import (
    P2LClient, LightColor, LightMode, BatchItem, TLSConfig,
    DispatchItem, WaveDispatcher,
    BatchDispatch, BatchDispatcher,
)

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
        p2l.light_on("0011", quantity=5, color=LightColor.GREEN, led_mode=LightMode.FLASH, buz_mode=LightMode.OFF)

        print("Waiting for operator confirmation…")
        event = await p2l.wait_confirm("0011", timeout=30)
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


# ---------------------------------------------------------------------------
# Example 7: Wave dispatch — queue of SKUs across multiple positions
# ---------------------------------------------------------------------------
async def example_wave_dispatch():
    """
    Products to distribute and available picking positions.

    The dispatcher fills all positions simultaneously.
    When a position confirms, it immediately receives the next item.
    Continues until every item has been dispatched and confirmed.
    """
    items = [
        DispatchItem("SKU-001", quantity=5),
        DispatchItem("SKU-002", quantity=3,  color=LightColor.BLUE),
        DispatchItem("SKU-003", quantity=10),
        DispatchItem("SKU-004", quantity=2,  color=LightColor.YELLOW),
        DispatchItem("SKU-005", quantity=7,  color=LightColor.CYAN),
        DispatchItem("SKU-006", quantity=1),
    ]

    # Addresses of available light positions
    positions = ["0001", "0019", "0001"]

    async with P2LClient(BROKER, PORT, username=USERNAME, password=PASSWORD) as p2l:
        dispatcher = WaveDispatcher(p2l, items, positions=positions)

        dispatcher.on_item_sent = lambda addr, item: \
            print(f"  → [{addr}] Dispatched {item.sku} x{item.quantity}")

        dispatcher.on_item_confirmed = lambda addr, item, evt: \
            print(f"  ✓ [{addr}] {item.sku} confirmed (status={evt.status.value})")

        dispatcher.on_wave_complete = lambda stats: \
            print(f"\n★ Wave complete — {stats.confirmed}/{stats.total} items "
                  f"confirmed across {stats.positions} position(s)")

        print(f"Starting wave: {len(items)} items → {len(positions)} positions\n")
        await dispatcher.start()
        stats = await dispatcher.wait()
        print(f"Final stats: {stats}")


# ---------------------------------------------------------------------------
# Example 8: Batch dispatch — items pre-assigned to specific positions by label
# ---------------------------------------------------------------------------
async def example_batch_dispatch():
    """
    Demonstrates two approaches using the JSON batch input format.

    Input:  each item carries a 'label' (position name) instead of a raw address.
            The P2L bridge resolves label → address via the client device database.
            Optional per-item fields: color, ledMode, buzMode.

    Approach A — Direct batch_start (one-shot, no queue tracking):
        All items sent simultaneously. No sequential queuing per position.
        Use when every position has exactly one item.

    Approach B — BatchDispatcher (queued, tracked):
        Items at the same position queue up sequentially.
        When a position confirms, the next item for that position fires immediately.
        Use when positions have multiple items to process in order.

        CH.001 queue: SKU-001 (qty 10, FLASH) → SKU-003 (qty 15, ON)   [sequential]
        CH.002 queue: SKU-002 (qty  5, ON)    → SKU-004 (qty  1, ON)   [sequential]
        CH.001 and CH.002 run in parallel.
    """
    import json

    # Full item options: sku, label, quantity, color, ledMode, buzMode
    raw_json = """{
        "batch_id": "jklklkj1212",
        "items": [
            {"sku": "SKU-001", "label": "CH.XXX", "quantity": 10, "color": "Green",  "ledMode": "Flash","buzMode": "Off"},
            {"sku": "SKU-002", "label": "CH.002", "quantity": 5,  "color": "Yellow", "ledMode": "Flash", "buzMode": "Off"},
            {"sku": "SKU-003", "label": "CH.XXX", "quantity": 15, "color": "Green",  "ledMode": "Flash", "buzMode": "Off"},
            {"sku": "SKU-004", "label": "CH.002", "quantity": 1,  "color": "Red",    "ledMode": "Flash", "buzMode": "Off"}
        ]
    }"""

    payload = json.loads(raw_json)

    async with P2LClient(BROKER, PORT, username=USERNAME, password=PASSWORD) as p2l:

        # ── Approach A: direct batch_start with label-only BatchItems ──────────
        # No address needed — the bridge resolves label → address automatically.
        # Per-item color, led_mode and buz_mode are fully supported.
        print("── Approach A: direct batch_start (label-only, full options) ──")
        items_a = [
            BatchItem(
                address=None,
                quantity=i["quantity"],
                color=LightColor(i["color"]),
                label=i["label"],
                led_mode=LightMode(i.get("ledMode", "On")),
                buz_mode=LightMode(i["buzMode"]) if "buzMode" in i else None,
            )
            for i in payload["items"][:2]   # first two items only
        ]
        p2l.on_confirm(lambda ev: print(f"  ✓ {ev.address} [{ev.label}] confirmed (display={ev.display_data})"))
        p2l.batch_start(items_a)
        print(f"  Sent {len(items_a)} items via batch_start\n")

        await asyncio.sleep(1)   # brief pause before approach B

        # ── Approach B: BatchDispatcher (queued, per-position tracking) ─────────
        # label_map() reads device states already cached from retained
        # light_changed messages received after connect().
        # Per-item color/ledMode/buzMode from JSON are preserved and used.
        print("── Approach B: BatchDispatcher (queued, full options) ──")
        label_map = p2l.label_map()
        print(f"  label_map: {label_map}\n")

        if not label_map:
            print("No devices found — ensure the P2L bridge is running with labels set.")
            return

        batch      = BatchDispatch.from_dict(payload)
        dispatcher = BatchDispatcher(p2l, batch)

        dispatcher.on_item_sent = lambda addr, item: \
            print(f"  → [{item.label}] {item.sku} x{item.quantity} "
                  f"color={item.color.value} led={item.led_mode.value} "
                  f"buz={item.buz_mode.value if item.buz_mode else '-'}  (addr={addr})")

        dispatcher.on_item_confirmed = lambda addr, item, evt: \
            print(f"  ✓ [{item.label}] {item.sku} confirmed "
                  f"(status={evt.status.value} label={evt.label} display={evt.display_data})")

        dispatcher.on_batch_complete = lambda stats: \
            print(f"\n★ Batch {stats['batch_id']} complete — "
                  f"{stats['confirmed']}/{stats['total']} items")

        print(f"Starting batch '{batch.batch_id}' "
              f"({len(batch.items)} items, {len(label_map)} position(s))\n")
        await dispatcher.start()
        stats = await dispatcher.wait()
        print(f"Final: {stats}")

if __name__ == "__main__":
    asyncio.run(example_batch_dispatch())
