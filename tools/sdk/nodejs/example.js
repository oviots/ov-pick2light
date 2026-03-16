'use strict';
/**
 * P2L Node.js SDK — usage examples.
 * Run: node example.js
 */

const { P2LClient, LightColor, LightMode } = require('./p2l');

// ---------------------------------------------------------------------------
// Example 1: Single pick — light on, wait for confirmation
// ---------------------------------------------------------------------------
async function exampleSinglePick() {
  const p2l = new P2LClient('localhost');
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
  const p2l = new P2LClient('localhost');
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
// Example 3: React to events in real-time
// ---------------------------------------------------------------------------
async function exampleEventStream() {
  const p2l = new P2LClient('localhost');

  p2l.onConfirm(ev =>
    console.log(`Button pressed: addr=${ev.address} status=${ev.status} adj=${ev.adjustedDisplay}`));

  p2l.onBridgeStatus(bs =>
    console.log(`Bridge: online=${bs.online} gateway=${bs.gatewayConnected}`));

  await p2l.connect();
  console.log('Listening for 60 seconds…');
  await new Promise(r => setTimeout(r, 60_000));
  await p2l.disconnect();
}

// ---------------------------------------------------------------------------
// Example 4: With TLS + credentials
// ---------------------------------------------------------------------------
async function exampleTLS() {
  const fs = require('fs');
  const p2l = new P2LClient('broker.example.com', 8883, {
    username: 'user',
    password: 'pass',
    tls: {
      ca: fs.readFileSync('/path/to/ca.pem'),
      rejectUnauthorized: true,
    },
  });

  await p2l.connect();
  p2l.lightOn('0001', { quantity: 1, color: LightColor.BLUE });
  await new Promise(r => setTimeout(r, 5_000));
  p2l.lightOff('0001');
  await p2l.disconnect();
}

// Run example 1 by default
exampleSinglePick().catch(console.error);
