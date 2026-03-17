'use strict';
/**
 * P2L Node.js SDK — usage examples.
 * Run: node example.js
 */

const { P2LClient, LightColor, LightMode, WaveDispatcher, BatchDispatch, BatchDispatcher } = require('./p2l');

// EMQX broker (from appsettings.json)
const BROKER   = 's28f7f16.ala.asia-southeast1.emqxsl.com';
const PORT     = 8883;          // TLS auto-enabled at port 8883
const USERNAME = 'pick2light';
const PASSWORD = 'P2l123456';

// ---------------------------------------------------------------------------
// Example 1: Single pick — light on, wait for confirmation
// ---------------------------------------------------------------------------
async function exampleSinglePick() {
  const p2l = new P2LClient(BROKER, PORT, { username: USERNAME, password: PASSWORD });
  await p2l.connect();

  console.log('Lighting up module 0001 — qty 5, green flash');
  p2l.lightOn('0001', { quantity: 5, color: LightColor.GREEN, ledMode: LightMode.FLASH });

  console.log('Waiting for operator confirmation…');
  const event = await p2l.waitConfirm('0001', 30_000);
  console.log('Confirmed:', event);

  await p2l.disconnect();
}

// ---------------------------------------------------------------------------
// Example 2: Batch pick — light several modules, wait for all
// ---------------------------------------------------------------------------
async function exampleBatchPick() {
  const p2l = new P2LClient(BROKER, PORT, { username: USERNAME, password: PASSWORD });
  await p2l.connect();

  p2l.onConfirm(ev => console.log(`  ✓ ${ev.address} confirmed (${ev.status})`));

  const items = [
    { address: '0001', quantity: 3, color: LightColor.GREEN,  label: 'SKU-A' },
    { address: '0002', quantity: 1, color: LightColor.YELLOW, label: 'FRAGILE' },
    { address: '0003', quantity: 7, color: LightColor.RED },
  ];

  console.log('Starting batch…');
  p2l.batchStart(items, { ledMode: LightMode.FLASH, buzMode: LightMode.OFF });

  const batchDone = p2l.waitBatchComplete(120_000);

  console.log('Waiting for all confirmations…');
  await batchDone;
  console.log('Batch complete!');

  await p2l.disconnect();
}

// ---------------------------------------------------------------------------
// Example 3: React to events in real-time (runs until Ctrl-C / SIGTERM)
// ---------------------------------------------------------------------------
async function exampleEventStream() {
  const p2l = new P2LClient(BROKER, PORT, { username: USERNAME, password: PASSWORD });

  p2l.onConfirm(ev =>
    console.log(`[confirm]  addr=${ev.address}  status=${ev.status}  adj=${ev.adjustedDisplay}`));
  p2l.onStateChange(s =>
    console.log(`[state]    addr=${s.address}  status=${s.moduleStatus}  color=${s.color}  qty=${s.displayData}`));
  p2l.onTaskEvent(ev =>
    console.log(`[task]     ${ev.eventType}  addr=${ev.address}  qty=${ev.quantity}  label=${ev.label}`));
  p2l.onBridgeStatus(bs =>
    console.log(`[bridge]   online=${bs.online}  gateway=${bs.gatewayConnected}`));

  await p2l.connect();
  console.log('Listening… press Ctrl-C to stop.');

  await new Promise((resolve) => {
    process.once('SIGINT',  resolve);
    process.once('SIGTERM', resolve);
  });

  await p2l.disconnect();
  console.log('Disconnected.');
}

// ---------------------------------------------------------------------------
// Example 4: With explicit TLS (custom CA cert — self-signed brokers only)
// For EMQX cloud with a trusted CA, TLS is auto-enabled at port 8883.
// ---------------------------------------------------------------------------
async function exampleTLS() {
  const fs = require('fs');
  const p2l = new P2LClient(BROKER, PORT, {
    username: USERNAME,
    password: PASSWORD,
    // Custom CA only needed for self-signed certs; omit for public cloud brokers.
    tls: { ca: fs.readFileSync('/path/to/ca.pem'), rejectUnauthorized: true },
  });

  await p2l.connect();
  p2l.lightOn('0001', { quantity: 1, color: LightColor.BLUE });
  await new Promise(r => setTimeout(r, 5_000));
  p2l.lightOff('0001');
  await p2l.disconnect();
}

// Run example 1 by default
exampleSinglePick().catch(console.error);

// ---------------------------------------------------------------------------
// Example 5: Monitor real-time data processing (CSV task lifecycle)
// ---------------------------------------------------------------------------
async function exampleDataProcessing() {
  const p2l = new P2LClient(BROKER, PORT, { username: USERNAME, password: PASSWORD });
  await p2l.connect();

  // Fine-grained callbacks for each step
  p2l.onTaskStarted(ev =>
    console.log(`  → Started   [${ev.address}] qty=${ev.quantity} color=${ev.color} label=${ev.label}`));

  p2l.onTaskConfirmed(ev =>
    console.log(`  ✓ Confirmed [${ev.address}] qty=${ev.quantity}`));

  p2l.onTaskError(ev =>
    console.log(`  ✗ Error     [${ev.address}] ${ev.label}`));

  p2l.onCsvComplete(() =>
    console.log('  ★ All CSV tasks completed!'));

  console.log('Listening for CSV data-processing events (60 s)…');
  await new Promise(r => setTimeout(r, 60_000));
  await p2l.disconnect();
}

// ---------------------------------------------------------------------------
// Example 6: Wait for a full CSV run to finish programmatically
// ---------------------------------------------------------------------------
async function exampleWaitCsvComplete() {
  const p2l = new P2LClient(BROKER, PORT, { username: USERNAME, password: PASSWORD });
  await p2l.connect();

  // Single callback for all task event types
  p2l.onTaskEvent(ev =>
    console.log(`  [${ev.eventType}] ${ev.address} qty=${ev.quantity}`));

  console.log('Waiting for CSV pick task to finish (up to 5 min)…');
  try {
    await p2l.waitCsvComplete(300_000);
    console.log('CSV run complete!');
  } catch (err) {
    console.error('Timed out:', err.message);
  }

  await p2l.disconnect();
}

// ---------------------------------------------------------------------------
// Example 7: Wave dispatch — queue of SKUs across multiple positions
// ---------------------------------------------------------------------------
async function exampleWaveDispatch() {
  /**
   * Products to distribute and available picking positions.
   *
   * The dispatcher fills all positions simultaneously.
   * When a position confirms, it immediately receives the next item.
   * Continues until every item has been dispatched and confirmed.
   */
  const items = [
    { sku: 'SKU-001', quantity: 5 },
    { sku: 'SKU-002', quantity: 3,  color: LightColor.BLUE },
    { sku: 'SKU-003', quantity: 10 },
    { sku: 'SKU-004', quantity: 2,  color: LightColor.YELLOW },
    { sku: 'SKU-005', quantity: 7,  color: LightColor.CYAN },
    { sku: 'SKU-006', quantity: 1 },
  ];

  // Addresses of available light positions
  const positions = ['0001', '0002', '0003'];

  const p2l = new P2LClient(BROKER, PORT, { username: USERNAME, password: PASSWORD });
  await p2l.connect();

  const dispatcher = new WaveDispatcher(p2l, items, { positions });

  dispatcher.onItemSent = (addr, item) =>
    console.log(`  → [${addr}] Dispatched ${item.sku} x${item.quantity}`);

  dispatcher.onItemConfirmed = (addr, item, evt) =>
    console.log(`  ✓ [${addr}] ${item.sku} confirmed (status=${evt.status})`);

  dispatcher.onWaveComplete = (stats) =>
    console.log(`\n★ Wave complete — ${stats.confirmed}/${stats.total} items confirmed across ${stats.positions} position(s)`);

  console.log(`Starting wave: ${items.length} items → ${positions.length} positions\n`);
  dispatcher.start();
  const stats = await dispatcher.wait();
  console.log('Final stats:', stats);

  await p2l.disconnect();
}

// ---------------------------------------------------------------------------
// Example 8: Batch dispatch — items pre-assigned to positions by label
// ---------------------------------------------------------------------------
async function exampleBatchDispatch() {
  /**
   * Demonstrates two approaches using the JSON batch input format.
   *
   * Input:  each item carries a 'label' (position name) instead of a raw address.
   *         The P2L bridge resolves label → address via the client device database.
   *         Optional per-item fields: color, ledMode, buzMode.
   *
   * Approach A — Direct batchStart (one-shot, no queue tracking):
   *   All items sent simultaneously. No sequential queuing per position.
   *   Use when every position has exactly one item.
   *
   * Approach B — BatchDispatcher (queued, tracked):
   *   Items at the same position queue up sequentially.
   *   When a position confirms, the next item for that position fires immediately.
   *
   *   CH.001: SKU-001 (qty 10, Flash) → SKU-003 (qty 15, On)  [sequential]
   *   CH.002: SKU-002 (qty  5, On)    → SKU-004 (qty  1, On)  [sequential]
   *   CH.001 and CH.002 run in parallel.
   */

  // Full item options: sku, label, quantity, color, ledMode, buzMode
  const rawJson = `{
    "batch_id": "jklklkj1212",
    "items": [
      {"sku": "SKU-001", "label": "CH.001", "quantity": 10, "color": "Green",  "ledMode": "Flash",  "buzMode": "Flash"},
      {"sku": "SKU-002", "label": "CH.002", "quantity": 5,  "color": "Yellow", "ledMode": "On",     "buzMode": "Off"},
      {"sku": "SKU-003", "label": "CH.001", "quantity": 15, "color": "Green",  "ledMode": "On"},
      {"sku": "SKU-004", "label": "CH.002", "quantity": 1,  "color": "Red",    "ledMode": "On"}
    ]
  }`;

  const payload = JSON.parse(rawJson);

  const p2l = new P2LClient(BROKER, PORT, { username: USERNAME, password: PASSWORD });
  await p2l.connect();

  // ── Approach A: direct batchStart with label-only items (full options) ──────
  // No address needed — the bridge resolves label → address automatically.
  // Per-item color, ledMode and buzMode are fully supported.
  console.log('── Approach A: direct batchStart (label-only, full options) ──');
  const itemsA = payload.items.slice(0, 2).map(i => ({
    label:   i.label,
    quantity: i.quantity,
    color:   i.color   ?? LightColor.GREEN,
    ledMode: i.ledMode ?? LightMode.ON,
    ...(i.buzMode !== undefined ? { buzMode: i.buzMode } : {}),
  }));
  p2l.onConfirm(ev =>
    console.log(`  ✓ ${ev.address} [${ev.label}] confirmed (display=${ev.displayData})`));
  p2l.batchStart(itemsA);
  console.log(`  Sent ${itemsA.length} items via batchStart\n`);

  await new Promise(r => setTimeout(r, 1_000));   // brief pause

  // ── Approach B: BatchDispatcher (queued, per-position tracking) ─────────────
  // labelMap() reads device states already cached from retained
  // light_changed messages received after connect().
  // Per-item color/ledMode/buzMode from JSON are preserved and used.
  console.log('── Approach B: BatchDispatcher (queued, full options) ──');
  const labelMap = p2l.labelMap();
  console.log('  labelMap:', labelMap, '\n');

  if (Object.keys(labelMap).length === 0) {
    console.log('No devices found — ensure the P2L bridge is running with labels configured.');
    await p2l.disconnect();
    return;
  }

  const batch      = BatchDispatch.fromDict(payload);
  const dispatcher = new BatchDispatcher(p2l, batch);

  dispatcher.onItemSent = (addr, item) =>
    console.log(`  → [${item.label}] ${item.sku} x${item.quantity} ` +
      `color=${item.color} led=${item.ledMode} buz=${item.buzMode ?? '-'}  (addr=${addr})`);

  dispatcher.onItemConfirmed = (addr, item, evt) =>
    console.log(`  ✓ [${item.label}] ${item.sku} confirmed ` +
      `(status=${evt.status} label=${evt.label} display=${evt.displayData})`);

  dispatcher.onBatchComplete = (stats) =>
    console.log(`\n★ Batch ${stats.batchId} complete — ${stats.confirmed}/${stats.total} items`);

  console.log(`Starting batch '${batch.batchId}' (${batch.items.length} items, ${Object.keys(labelMap).length} position(s))\n`);
  dispatcher.start();
  const stats = await dispatcher.wait();
  console.log('Final:', stats);

  await p2l.disconnect();
}
