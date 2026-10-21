const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path'), vm = require('node:vm'), crypto = require('node:crypto');
const source = fs.readFileSync(path.join(__dirname, '../../client/static/js/app.js'), 'utf8');

function ui() {
  const nodes = new Map();
  function node(id) {
    if (!nodes.has(id)) nodes.set(id, {value: '', textContent: '', hidden: false, disabled: false, classList: {add() {}, remove() {}}});
    return nodes.get(id);
  }
  const box = vm.createContext({document: {addEventListener() {}, getElementById: node}, crypto, Uint8Array,
    console, setTimeout, clearTimeout, setInterval, clearInterval, AbortController, Set, Map});
  vm.runInContext(source, box);
  vm.runInContext("toast=()=>{};state.devices['0001']={address:'0001',status:'OPERATING',display_data:'00005',color:'GREEN',block_no:'01',correlation_id:'owner',command_item_id:'item',picking_detail_id:'detail',confirmation_missing_at:'2026-10-21T01:00:00Z',confirmation_missing_event_id:'missing',observed_status:'STANDBY',can_cancel_test:false};", box);
  return {box, node, run: code => vm.runInContext(code, box)};
}

test('missing confirmation renders its own warning and ordinary controls cannot bypass it', () => {
  const {run} = ui();
  const html = run("deviceCardHTML(state.devices['0001'])");
  assert.match(html, /Confirmation missing/);
  assert.match(html, /btn-recover/);
  assert.match(html, /btn-light-on\" disabled/);
  assert.doesNotMatch(run("deviceCardHTML({...state.devices['0001'],status:'COMPLETED'})"), /Confirmation missing/);
});

test('actual quantity starts blank; empty input cannot silently confirm full quantity', async () => {
  const {run, node} = ui();
  run("api=async()=>{globalThis.sent=true};openRecoveryModal('0001')");
  assert.equal(node('recovery-qty').value, '');
  await run("recoverItem('confirm')");
  assert.equal(run('globalThis.sent'), undefined);
  assert.match(node('recovery-message').textContent, /actual picked quantity/);
});

test('explicit zero keeps original identity and applied result does not confirm or clear pending locally', async () => {
  const {run, box, node} = ui();
  run("state.batchPending=new Set(['0001']);api=async(m,p,b,timeout)=>{globalThis.sent={p,b,timeout}};openRecoveryModal('0001')");
  node('recovery-qty').value = '0';
  await run("recoverItem('confirm')");
  const sent = JSON.parse(JSON.stringify(box.sent));
  assert.equal(sent.b.actual_quantity, 0);
  assert.equal(sent.b.block_no, '01');
  assert.equal(sent.b.correlation_id, 'owner');
  assert.equal(sent.b.command_item_id, 'item');
  assert.equal(sent.b.picking_detail_id, 'detail');
  assert.equal(sent.b.missing_event_id, 'missing');
  assert.ok(sent.b.command_id);
  assert.ok(sent.b.expires_at);
  assert.equal(sent.timeout, 35000);
  assert.equal(run("state.devices['0001'].status"), 'OPERATING');
  assert.equal(run("state.devices['0001'].confirmation_missing_event_id"), 'missing');
  assert.equal(run('state.batchPending.size'), 1);
  assert.equal(run('_recoveryIntents.size'), 0);
});

test('cancel test is hidden and blocked for WMS; own manual cancel includes no quantity', async () => {
  const {run, box, node} = ui();
  run("api=async(m,p,b)=>{globalThis.sent=b};openRecoveryModal('0001')");
  assert.equal(node('btn-recovery-cancel-test').hidden, true);
  await run("recoverItem('cancel')");
  assert.equal(box.sent, undefined);
  run("state.devices['0001'].can_cancel_test=true;openRecoveryModal('0001')");
  assert.equal(node('btn-recovery-cancel-test').hidden, false);
  await run("recoverItem('cancel')");
  assert.equal(box.sent.action, 'cancel');
  assert.equal(box.sent.actual_quantity, undefined);
});

test('an item change while modal is open prevents stale recovery publication', async () => {
  const {run, box, node} = ui();
  run("api=async()=>{globalThis.sent=true};openRecoveryModal('0001');state.devices['0001'].command_item_id='next-item'");
  await run("recoverItem('retry')");
  assert.equal(box.sent, undefined);
  assert.match(node('recovery-message').textContent, /Item state changed/);
});

test('lost HTTP outcome resends the same command ID, expiry and expected identity', async () => {
  const {run, box} = ui();
  run("globalThis.sent=[];api=async(m,p,b)=>{sent.push(JSON.stringify(b));throw new Error('lost result')};openRecoveryModal('0001')");
  await run("recoverItem('retry')");
  run("closeRecoveryModal();openRecoveryModal('0001')");
  await run("recoverItem('retry')");
  assert.equal(box.sent.length, 2);
  assert.equal(box.sent[0], box.sent[1]);
  assert.equal(JSON.parse(box.sent[0]).actual_quantity, undefined);
  assert.equal(run("state.devices['0001'].confirmation_missing_event_id"), 'missing');
});

test('HTTP rejection preserves warning; a later explicit attempt receives a distinct intent', async () => {
  const {run, box} = ui();
  run("globalThis.sent=[];api=async(m,p,b)=>{sent.push(b.command_id);const e=new Error('session no longer active');e.executionStatus='rejected';throw e};openRecoveryModal('0001')");
  await run("recoverItem('retry')");
  await run("recoverItem('retry')");
  assert.notEqual(box.sent[0], box.sent[1]);
  assert.equal(run("state.devices['0001'].status"), 'OPERATING');
});

test('cached unknown intents are bounded and expire or disappear with changed item state', async () => {
  const {run, node} = ui();
  run("state.devices['0001'].display_data='00200';api=async()=>{throw new Error('lost result')};openRecoveryModal('0001')");
  for (let quantity = 0; quantity < 130; quantity++) {
    node('recovery-qty').value = String(quantity);
    await run("recoverItem('confirm')");
  }
  assert.equal(run('_recoveryIntents.size'), 128);
  run("for(const entry of _recoveryIntents.values())entry.command.expires_at='2000-01-01T00:00:00Z';pruneRecoveryIntents()");
  assert.equal(run('_recoveryIntents.size'), 0);
  await run("recoverItem('retry')");
  assert.equal(run('_recoveryIntents.size'), 1);
  run("state.devices['0001'].confirmation_missing_event_id='next-warning';pruneRecoveryIntents()");
  assert.equal(run('_recoveryIntents.size'), 0);
});

test('recovery HTTP deadline aborts the local request instead of waiting indefinitely', async () => {
  const {run, box} = ui();
  box.fetch = async (_url, options) => new Promise((_resolve, reject) => options.signal.addEventListener('abort', () => {
    const error = new Error('aborted'); error.name = 'AbortError'; reject(error);
  }));
  await assert.rejects(run("api('POST','/api/devices/0001/recover',{},5)"), /Recovery result timed out/);
});
