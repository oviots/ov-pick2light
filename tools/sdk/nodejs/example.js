'use strict';
/**
 * P2L Node.js SDK — usage examples.
 * Run: node example.js
 */

const { P2LClient, LightColor, LightMode } = require('./p2l');

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
