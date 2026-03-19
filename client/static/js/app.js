/* P2L Web Dashboard — Frontend Logic */
'use strict';

// ── State ──────────────────────────────────────────────────────────────────
const state = {
  connected: false,      // P2L service is running
  mqttConnected: false,  // Python client → MQTT broker
  host: 'localhost',
  port: 8883,
  devices: {},           // address → device object
  batchItems: [],        // items in the batch builder (not yet sent)
  batchActive: false,
  batchPending: new Set(),
  eventFeed: [],
};

let ws = null;
let wsReconnectTimer = null;

// ── Color definitions ──────────────────────────────────────────────────────
const COLORS = ['OFF', 'RED', 'GREEN', 'BLUE', 'YELLOW', 'CYAN', 'MAGENTA', 'WHITE'];
const COLOR_HEX = {
  OFF: '#374151', RED: '#ef4444', GREEN: '#22c55e', BLUE: '#3b82f6',
  YELLOW: '#eab308', CYAN: '#06b6d4', MAGENTA: '#d946ef', WHITE: '#e2e8f0',
};

const LED_MODES = ['ON', 'FLASH', 'HIGH_FLASH'];

// ── DOM helpers ────────────────────────────────────────────────────────────
const $ = id => document.getElementById(id);
const el = (tag, cls, html) => { const e = document.createElement(tag); if (cls) e.className = cls; if (html) e.innerHTML = html; return e; };

// ── Toast ──────────────────────────────────────────────────────────────────
function toast(msg, type = 'info') {
  const container = $('toast-container');
  const t = el('div', `toast ${type}`);
  const icons = { info: '●', success: '✓', error: '✗', warning: '⚠' };
  t.innerHTML = `<span>${icons[type] || '●'}</span><span>${msg}</span>`;
  container.appendChild(t);
  setTimeout(() => t.remove(), 3000);
}

// ── Tabs ───────────────────────────────────────────────────────────────────
function initTabs() {
  const DATA_TABS = new Set(['dashboard', 'devices', 'batch', 'events', 'logs']);
  document.querySelectorAll('.tab-btn').forEach(btn => {
    btn.addEventListener('click', () => {
      const tab = btn.dataset.tab;
      document.querySelectorAll('.tab-btn').forEach(b => b.classList.remove('active'));
      document.querySelectorAll('.tab-pane').forEach(p => p.classList.remove('active'));
      btn.classList.add('active');
      $('pane-' + tab).classList.add('active');
      if (tab === 'settings') { refreshServiceBadge(); refreshP2lServiceBadge(); }
      // Hide overlay on settings/about tabs so users can configure even when disconnected
      const overlay = $('disconnected-overlay');
      if (overlay) {
        if (!state.mqttConnected && DATA_TABS.has(tab)) overlay.classList.remove('hidden');
        else overlay.classList.add('hidden');
      }
    });
  });
}

function initSubTabs() {
  document.querySelectorAll('.sub-tab-btn').forEach(btn => {
    btn.addEventListener('click', () => {
      const subtab = btn.dataset.subtab;
      const bar = btn.closest('.sub-tab-bar');
      const container = bar.parentElement;
      // Toggle button active state
      bar.querySelectorAll('.sub-tab-btn').forEach(b => b.classList.remove('active'));
      btn.classList.add('active');
      // Show/hide sub-panes via inline style (avoids CSS cache issues)
      container.querySelectorAll(':scope > .sub-tab-pane').forEach(p => {
        p.style.display = 'none';
      });
      const pane = document.getElementById('sub-pane-' + subtab);
      if (pane) pane.style.display = 'block';
      if (subtab === 'bridge') loadAppsettings();
    });
  });
}

// ── WebSocket ─────────────────────────────────────────────────────────────
function connectWs() {
  const wsUrl = `ws://${location.host}/ws`;
  ws = new WebSocket(wsUrl);

  ws.onopen = () => {
    clearTimeout(wsReconnectTimer);
    // ping every 25s
    setInterval(() => ws && ws.readyState === 1 && ws.send('ping'), 25000);
  };

  ws.onmessage = e => {
    try {
      const msg = JSON.parse(e.data);
      handleWsMessage(msg);
    } catch { /* ignore */ }
  };

  ws.onclose = () => {
    ws = null;
    wsReconnectTimer = setTimeout(connectWs, 3000);
  };

  ws.onerror = () => ws && ws.close();
}

function handleWsMessage(msg) {
  switch (msg.type) {
    case 'init':
      state.connected = msg.data.connected;
      state.mqttConnected = msg.data.mqtt_connected || false;
      state.host = msg.data.host;
      state.port = msg.data.port;
      // Clear stale rows from previous session
      Object.keys(_addrMap).forEach(k => delete _addrMap[k]);
      Object.keys(state.devices).forEach(k => delete state.devices[k]);
      { const tbody = $('address-tbody'); if (tbody) tbody.innerHTML = ''; }
      msg.data.devices.forEach(d => {
        state.devices[d.address] = d;
        upsertAddressRow(d);
      });
      updateConnectionUI();
      renderDevices();
      break;

    case 'connection_changed':
      state.connected = msg.data.connected;
      state.mqttConnected = msg.data.mqtt_connected || false;
      state.host = msg.data.host || state.host;
      state.port = msg.data.port || state.port;
      updateConnectionUI();
      if (msg.data.mqtt_connected !== undefined) {
        toast(msg.data.mqtt_connected
          ? `MQTT broker connected: ${msg.data.host}:${msg.data.port}`
          : 'MQTT broker disconnected', msg.data.mqtt_connected ? 'success' : 'warning');
      }
      break;

    case 'bridge_status': {
      pushEvent('bridge_status', msg.data);
      const badge = $('mqtt-status-badge');
      if (badge) {
        badge.dataset.bridge = '1';
        if (msg.data.online) {
          badge.textContent = msg.data.gateway_connected ? 'Bridge Online ✓' : 'Bridge Online (gw offline)';
          badge.className = 'status-badge OPERATING';
        } else {
          badge.textContent = 'Bridge Offline';
          badge.className = 'status-badge STANDBY';
          delete badge.dataset.bridge;
        }
      }
      break;
    }

    case 'state_update': {
      const incoming = new Set(msg.data.map(d => d.address));
      msg.data.forEach(d => {
        upsertAddressRow(d);
        pushOperatingRow(d);
        pushEvent('light_changed', d);
        state.devices[d.address] = d;
      });
      _scanOnStateUpdate(msg.data);  // feed into active scan session if any
      // Remove devices no longer in the full registry (e.g. stale DB entries cleared by server)
      Object.keys(_addrMap).forEach(addr => {
        if (!incoming.has(addr)) {
          delete _addrMap[addr];
          const tbody = $('address-tbody');
          const row = tbody?.querySelector(`tr[data-addr="${addr}"]`);
          if (row) row.remove();
        }
      });
      Object.keys(state.devices).forEach(addr => {
        if (!incoming.has(addr)) delete state.devices[addr];
      });
      renderDevices();
      updateStats();
      break;
    }

    case 'confirmation': {
      const evt = msg.data;
      pushEvent('confirmed', evt);
      addEvent(`${evt.address} confirmed`, evt.status === 'SHORTAGE' ? 'warning' : 'success');
      if (state.batchPending.has(evt.address)) {
        state.batchPending.delete(evt.address);
        updateBatchStatus();
      }
      renderDevices();
      break;
    }

    case 'batch_completed':
      pushEvent('batch_completed', {});
      toast('Batch complete! All items confirmed.', 'success');
      state.batchActive = false;
      state.batchPending.clear();
      updateBatchStatus();
      break;

    case 'log':
      appendLog(msg.data);
      break;
  }
}

// ── Connection UI ─────────────────────────────────────────────────────────
function updateConnectionUI() {
  const badge = $('connection-badge');
  badge.className = 'connection-badge' + (state.connected ? ' connected' : '');
  let label;
  if (state.connected && state.mqttConnected) label = `Online · ${state.host}:${state.port}`;
  else if (state.connected) label = 'Service Running';
  else label = 'Disconnected';
  badge.innerHTML = `<span class="dot"></span>${label}`;

  $('btn-connect').disabled = state.mqttConnected;
  $('btn-disconnect').disabled = !state.mqttConnected;

  // Show/hide disconnected overlay — hide on settings/about tabs or when MQTT connected
  const overlay = $('disconnected-overlay');
  if (overlay) {
    const activeTab = document.querySelector('.tab-btn.active')?.dataset.tab ?? 'dashboard';
    const DATA_TABS = new Set(['dashboard', 'devices', 'batch', 'events', 'logs']);
    if (state.mqttConnected || !DATA_TABS.has(activeTab)) overlay.classList.add('hidden');
    else overlay.classList.remove('hidden');
  }

  // Show "Scan Devices" sub-tab only when MQTT is connected
  const scanTabBtn = $('sub-tab-btn-scan');
  if (scanTabBtn) {
    scanTabBtn.style.display = state.mqttConnected ? '' : 'none';
    // If scan sub-tab is active but we just disconnected, switch back to client sub-tab
    if (!state.mqttConnected && scanTabBtn.classList.contains('active')) {
      scanTabBtn.classList.remove('active');
      document.querySelector('#sub-pane-scan')?.classList.remove('active');
      document.querySelector('.sub-tab-btn[data-subtab="client"]')?.classList.add('active');
      document.querySelector('#sub-pane-client')?.classList.add('active');
    }
  }

  // Update bridge status badge
  const mqttBadge = $('mqtt-status-badge');
  if (mqttBadge && !mqttBadge.dataset.bridge) {
    mqttBadge.textContent = state.connected ? 'Connected' : 'Offline';
    mqttBadge.className = `status-badge ${state.connected ? 'OPERATING' : 'STANDBY'}`;
  }

  updateStats();
}

function updateStats() {
  const devices = Object.values(state.devices);
  $('stat-total').textContent = devices.length;
  $('stat-active').textContent = devices.filter(d => d.status === 'OPERATING' || d.status === 'OPERATING2').length;
  $('stat-confirm').textContent = devices.filter(d => d.status === 'COMPLETED').length;
  $('stat-connected').textContent = state.connected ? '1' : '0';
}

// ── Device Rendering ──────────────────────────────────────────────────────
const STATUS_PRIORITY = { OPERATING: 0, OPERATING2: 1, SHORTAGE: 2, COMPLETED: 3, MAINTENANCE: 4, POWER_ON: 5, STANDBY: 6 };

function renderDevices() {
  const grid = $('device-grid');
  const devices = Object.values(state.devices).sort((a, b) => {
    const sa = STATUS_PRIORITY[a.status] ?? 9, sb = STATUS_PRIORITY[b.status] ?? 9;
    if (sa !== sb) return sa - sb;
    const oa = a.order_no ?? 0, ob = b.order_no ?? 0;
    return oa !== ob ? oa - ob : a.address.localeCompare(b.address);
  });

  if (devices.length === 0) {
    grid.innerHTML = `<div class="empty-state" style="grid-column:1/-1">
      <p>No devices registered. Add a device or connect to the gateway.</p></div>`;
    return;
  }

  // Remove static placeholder (from HTML template) once we have real cards
  grid.querySelector('.empty-state')?.remove();

  // Remove stale cards
  const existing = new Set(Object.keys(state.devices));
  grid.querySelectorAll('.device-card[data-address]').forEach(card => {
    if (!existing.has(card.dataset.address)) card.remove();
  });

  // Render in sorted order — appendChild on an existing element moves it
  devices.forEach(d => {
    let card = grid.querySelector(`.device-card[data-address="${d.address}"]`);
    const isEditing = card?.querySelector('.device-label-input:focus, .device-order-input:focus');
    if (!card) {
      card = createDeviceCard(d);
      grid.appendChild(card);
    } else {
      updateDeviceCard(card, d);
      if (!isEditing) grid.appendChild(card); // reposition to maintain sort order
    }
  });

  updateStats();
}

function createDeviceCard(d) {
  const card = el('div', `device-card status-${d.status}`);
  card.dataset.address = d.address;
  card.innerHTML = deviceCardHTML(d);
  _bindCardActions(card, d.address);
  return card;
}

function updateDeviceCard(card, d) {
  // Don't clobber if the user is currently editing label or order
  if (card.querySelector('.device-label-input:focus, .device-order-input:focus')) {
    card.className = `device-card status-${d.status}`;
    return;
  }
  card.className = `device-card status-${d.status}`;
  card.innerHTML = deviceCardHTML(d);
  _bindCardActions(card, d.address);
}

function _bindCardActions(card, address) {
  card.querySelector('.btn-light-on')?.addEventListener('click', () => openLightModal(address));
  card.querySelector('.btn-light-off')?.addEventListener('click', () => lightOff(address));
  card.querySelector('.btn-check')?.addEventListener('click', () => checkDevice(address));
  card.querySelector('.btn-remove')?.addEventListener('click', () => removeDevice(address));

  // ── Order number inline edit ──
  const orderText = card.querySelector('.device-order-text');
  const orderInput = card.querySelector('.device-order-input');
  if (orderText && orderInput) {
    function startOrderEdit(e) {
      e?.stopPropagation();
      orderText.style.display = 'none';
      orderInput.style.display = 'inline-block';
      orderInput.focus();
      orderInput.select();
    }
    async function saveOrder() {
      const newOrder = parseInt(orderInput.value) || 0;
      orderText.textContent = `#${newOrder}`;
      orderText.style.display = '';
      orderInput.style.display = 'none';
      try {
        await api('PATCH', `/api/devices/${address}`, { order_no: newOrder });
        if (state.devices[address]) {
          state.devices[address].order_no = newOrder;
          renderDevices();
        }
      } catch (e) {
        toast(e.message, 'error');
      }
    }
    orderText.addEventListener('click', startOrderEdit);
    orderInput.addEventListener('click', e => e.stopPropagation());
    orderInput.addEventListener('blur', saveOrder);
    orderInput.addEventListener('keydown', e => {
      e.stopPropagation();
      if (e.key === 'Enter') { e.preventDefault(); orderInput.blur(); }
      if (e.key === 'Escape') {
        orderInput.value = state.devices[address]?.order_no ?? 0;
        orderInput.blur();
      }
    });
  }

  // ── Label inline edit ──
  const editBtn = card.querySelector('.btn-edit-label');
  const labelText = card.querySelector('.device-label-text');
  const labelInput = card.querySelector('.device-label-input');
  if (!editBtn || !labelText || !labelInput) return;

  function startEdit() {
    labelText.style.display = 'none';
    editBtn.style.display = 'none';
    labelInput.style.display = 'inline-block';
    labelInput.focus();
    labelInput.select();
  }

  async function saveLabel() {
    const newLabel = labelInput.value.trim();
    labelText.textContent = newLabel;
    labelText.title = newLabel;
    labelText.style.display = '';
    editBtn.style.display = '';
    labelInput.style.display = 'none';
    try {
      await api('PATCH', `/api/devices/${address}`, { label: newLabel });
      if (state.devices[address]) state.devices[address].label = newLabel;
    } catch (e) {
      toast(e.message, 'error');
    }
  }

  editBtn.addEventListener('click', startEdit);
  labelText.addEventListener('dblclick', startEdit);
  labelInput.addEventListener('blur', saveLabel);
  labelInput.addEventListener('keydown', e => {
    if (e.key === 'Enter') { e.preventDefault(); labelInput.blur(); }
    if (e.key === 'Escape') { labelInput.value = labelText.textContent; labelInput.blur(); }
  });
}

function deviceCardHTML(d) {
  const statusLabel = d.status.replace('_', ' ');
  const rawDisplay = d.adjusted_quantity || d.display_data || '00000';
  const displayVal = rawDisplay.trim().replace(/^0+/, '') || '0';
  const labelVal = escHtml(d.label || '');
  const orderVal = d.order_no ?? 0;

  return `
    <div class="device-header">
      <span class="device-order-text" title="Display order">#${orderVal}</span>
      <input class="device-order-input" type="number" value="${orderVal}" min="0" style="display:none">
      <span class="device-address">${d.address}</span>
      <span class="status-badge ${d.status}">${statusLabel}</span>
    </div>
    <div class="device-label-row">
      <span class="device-label-text${labelVal ? ' has-label' : ''}" title="${labelVal}">${labelVal || '<span style="opacity:.35">no label</span>'}</span>
      <input class="device-label-input" value="${labelVal}" placeholder="label…" style="display:none">
      <button class="btn btn-xs btn-ghost btn-edit-label" title="Edit label">✎</button>
    </div>
    <div class="color-indicator ${d.color !== 'OFF' && d.status === 'OPERATING' ? 'color-' + d.color : 'color-OFF'}"></div>
    <div class="device-display">${displayVal}</div>
    <div class="device-actions">
      <button class="btn btn-sm btn-success btn-light-on">ON</button>
      <button class="btn btn-sm btn-danger btn-light-off">OFF</button>
      <button class="btn btn-sm btn-warning btn-check" title="Flash green for 3 s then off">Check</button>
      <button class="btn btn-sm btn-ghost btn-remove" title="Remove device">✕</button>
    </div>`;
}

// ── Light Modal ────────────────────────────────────────────────────────────
let _lightModalAddress = null;

function openLightModal(address) {
  _lightModalAddress = address;
  $('light-modal-title').textContent = `Light Module ${address}`;
  const d = state.devices[address];
  if (d) {
    $('light-qty').value = parseInt(d.display_data) || 1;
    selectColor(d.color || 'GREEN');
    $('light-block').value = d.block_no || '00';
    $('light-label').value = d.label || '';
  }
  $('light-modal').classList.remove('hidden');
}

function closeLightModal() {
  $('light-modal').classList.add('hidden');
  _lightModalAddress = null;
}

function selectColor(name) {
  const picker = $('light-color-picker');
  if (picker) {
    picker.querySelectorAll('.color-swatch').forEach(s => {
      s.classList.toggle('selected', s.dataset.color === name);
    });
  }
  $('light-modal').dataset.selectedColor = name;
}

async function confirmLightOn() {
  if (!_lightModalAddress) return;
  const color = $('light-color-picker')?.querySelector('.color-swatch.selected')?.dataset.color
    || $('light-modal').dataset.selectedColor || 'GREEN';
  const qty = parseInt($('light-qty').value) || 1;
  const led_mode = $('light-mode').value || 'ON';
  const buzz = $('light-buzz')?.checked !== false;
  const block_no = $('light-block').value || '00';
  const label = $('light-label').value || '';

  try {
    await api('POST', `/api/devices/${_lightModalAddress}/light`, { quantity: qty, color, led_mode, buzz, block_no, label });
    toast(`Module ${_lightModalAddress} → ON (${color})`, 'success');
    closeLightModal();
  } catch (e) {
    toast(e.message, 'error');
  }
}

// ── Add Device Modal ───────────────────────────────────────────────────────
function openAddDeviceModal() {
  $('add-device-modal').classList.remove('hidden');
  $('new-device-addr').value = '';
  $('new-device-label').value = '';
  $('new-device-addr').focus();
}

function closeAddDeviceModal() {
  $('add-device-modal').classList.add('hidden');
}

async function confirmAddDevice() {
  const address = $('new-device-addr').value.trim().padStart(4, '0').slice(-4);
  const label = $('new-device-label').value.trim();
  if (!address) { toast('Address required', 'warning'); return; }
  try {
    await api('POST', '/api/devices', { address, label });
    toast(`Device ${address} added`, 'success');
    closeAddDeviceModal();
  } catch (e) {
    toast(e.message, 'error');
  }
}

// ── Device Actions ─────────────────────────────────────────────────────────
async function checkDevice(address) {
  const btn = document.querySelector(`.device-card[data-address="${address}"] .btn-check`);
  if (btn) { btn.disabled = true; btn.textContent = '…'; }
  try {
    await api('POST', `/api/devices/${address}/light`, { quantity: 1, color: 'GREEN', led_mode: 'ON' });
    toast(`Check ${address} — light on for 3 s`, 'info');
    setTimeout(async () => {
      await api('POST', `/api/devices/${address}/off`).catch(() => {});
      if (btn) { btn.disabled = false; btn.textContent = 'Check'; }
    }, 3000);
  } catch (e) {
    toast(e.message, 'error');
    if (btn) { btn.disabled = false; btn.textContent = 'Check'; }
  }
}

async function lightOff(address) {
  try {
    await api('POST', `/api/devices/${address}/off`);
    toast(`Module ${address} → OFF`, 'info');
  } catch (e) {
    toast(e.message, 'error');
  }
}

async function removeDevice(address) {
  try {
    await api('DELETE', `/api/devices/${address}`);
    delete state.devices[address];
    renderDevices();
  } catch (e) {
    toast(e.message, 'error');
  }
}

// ── Connection Actions ─────────────────────────────────────────────────────
async function connect() {
  $('btn-connect').disabled = true;
  $('btn-connect').textContent = 'Connecting…';
  try {
    const result = await api('POST', '/api/connect');
    if (result.service_started) toast('P2L service started', 'success');
    // REST only returns after MQTT is connected — sync state immediately
    const s = await api('GET', '/api/status');
    state.connected = s.connected;
    state.mqttConnected = s.mqtt_connected || false;
    state.host = s.host;
    state.port = s.port;
    updateConnectionUI();
    refreshServiceBadge();
  } catch (e) {
    toast(e.message, 'error');
    $('btn-connect').disabled = false;
  } finally {
    $('btn-connect').textContent = 'Connect';
  }
}

async function refreshServiceBadge() {
  try {
    const s = await api('GET', '/api/service/status');
    const badge = $('svc-status-badge');
    if (!badge) return;
    badge.textContent = s.running ? 'Running' : 'Stopped';
    badge.className = `status-badge ${s.running ? 'OPERATING' : 'STANDBY'}`;
  } catch { /* ignore */ }
}

// ── P2L Bridge service control ─────────────────────────────────────────────
async function refreshP2lServiceBadge() {
  if (!$('p2l-svc-badge')) return;
  try {
    const s = await api('GET', '/api/p2l-service/status');
    _applyP2lSvcUI(s.running);
  } catch { /* ignore */ }
}

function _applyP2lSvcUI(running) {
  const badge      = $('p2l-svc-badge');
  const btnStart   = $('btn-p2l-svc-start');
  const btnStop    = $('btn-p2l-svc-stop');
  if (!badge) return;
  badge.textContent = running ? 'Running' : 'Stopped';
  badge.className   = `status-badge ${running ? 'OPERATING' : 'STANDBY'}`;
  if (btnStart) { btnStart.style.display = running ? 'none' : ''; btnStart.disabled = false; }
  if (btnStop)  { btnStop.style.display  = running ? ''     : 'none'; btnStop.disabled = false; }
}

async function p2lServiceAction(action) {
  const btnStart = $('btn-p2l-svc-start');
  const btnStop  = $('btn-p2l-svc-stop');
  if (btnStart) btnStart.disabled = true;
  if (btnStop)  btnStop.disabled  = true;
  try {
    await api('POST', `/api/p2l-service/${action}`);
    toast(`P2L service ${action}ping…`, 'success');
    await new Promise(r => setTimeout(r, 2500));
    await refreshP2lServiceBadge();
  } catch (e) {
    toast(e.message, 'error');
    await refreshP2lServiceBadge();
  }
}

// ── Scan Session State ────────────────────────────────────────────────────
let _scan = null; // { fromAddr, toAddr, found: Map<addr,device>, timer, tickTimer, startMs, durationMs }

function _scanOnStateUpdate(devices) {
  if (!_scan) return;
  devices.forEach(d => {
    const n = parseInt(d.address, 10);
    if (n >= _scan.fromAddr && n <= _scan.toAddr) {
      _scan.found.set(d.address, d);
      _renderScanResultsLive();
    }
  });
}

function _renderScanResultsLive() {
  const wrap = $('scan-results-wrap');
  const tbody = $('scan-result-tbody');
  const summary = $('scan-result-summary');
  if (!wrap || !tbody) return;
  wrap.style.display = '';
  const entries = [..._scan.found.values()].sort((a, b) => a.address.localeCompare(b.address));
  summary.textContent = `${entries.length} device${entries.length !== 1 ? 's' : ''} found so far…`;
  tbody.innerHTML = entries.map(d => `
    <tr style="border-bottom:1px solid var(--border)">
      <td style="padding:0.3rem 0.5rem;font-family:monospace">${d.address}</td>
      <td style="padding:0.3rem 0.5rem">${d.label || '—'}</td>
      <td style="padding:0.3rem 0.5rem"><span class="status-badge ${d.status ?? 'STANDBY'}" style="font-size:0.72rem">${d.status ?? 'Unknown'}</span></td>
    </tr>`).join('');
}

function _finalizeScan() {
  if (!_scan) return;
  clearInterval(_scan.tickTimer);
  _scan.tickTimer = null;

  const btn     = $('btn-scan-devices');
  const progWrap = $('scan-progress-wrap');
  const fill    = $('scan-progress-fill');
  const infoTxt = $('scan-info-text');
  const summary = $('scan-result-summary');

  if (fill) fill.style.width = '100%';
  if (infoTxt) infoTxt.textContent = 'Scan complete';
  setTimeout(() => { if (progWrap) progWrap.style.display = 'none'; }, 800);

  const count = _scan.found.size;
  if (summary) summary.textContent = `Scan complete — ${count} device${count !== 1 ? 's' : ''} found`;

  // Show empty-state if nothing found
  const wrap = $('scan-results-wrap');
  if (wrap && count === 0) {
    wrap.style.display = '';
    const tbody = $('scan-result-tbody');
    if (tbody) tbody.innerHTML = '<tr><td colspan="3" style="padding:0.75rem 0.5rem;color:var(--text-muted)">No devices found in this range.</td></tr>';
    if (summary) summary.textContent = 'Scan complete — no devices found';
  }

  if (btn) { btn.disabled = false; btn.textContent = 'Start Scan'; }
  _scan = null;
}

async function scanDevices() {
  const btn  = $('btn-scan-devices');
  const from = parseInt($('scan-from')?.value ?? '1',  10) || 1;
  const to   = parseInt($('scan-to')?.value   ?? '99', 10) || 99;

  if (from < 1 || to < from || to > 9999) {
    toast('Invalid address range (1–9999, from ≤ to)', 'error');
    return;
  }

  // Estimated duration: ceil(range / concurrency=4) batches × 1.5s + 4s buffer
  const durationMs = Math.ceil((to - from + 1) / 4) * 1500 + 4000;

  // Reset UI
  const progWrap = $('scan-progress-wrap');
  const fill     = $('scan-progress-fill');
  const infoTxt  = $('scan-info-text');
  const elapsed  = $('scan-elapsed');
  const wrap     = $('scan-results-wrap');
  const tbody    = $('scan-result-tbody');
  const summary  = $('scan-result-summary');

  if (wrap)    { wrap.style.display = 'none'; }
  if (tbody)   tbody.innerHTML = '';
  if (summary) summary.textContent = '';
  if (fill)    fill.style.transition = 'none';
  if (fill)    { fill.style.width = '0%'; }
  if (progWrap) progWrap.style.display = '';
  if (infoTxt) infoTxt.textContent = `Scanning ${from}–${to}…`;
  if (btn) { btn.disabled = true; btn.textContent = 'Scanning…'; }

  _scan = { fromAddr: from, toAddr: to, found: new Map(), startMs: Date.now(), durationMs, timer: null, tickTimer: null };

  try {
    await api('POST', `/api/scan-devices?addr_from=${from}&addr_to=${to}`);
  } catch (e) {
    toast(e.message, 'error');
    if (btn) { btn.disabled = false; btn.textContent = 'Start Scan'; }
    if (progWrap) progWrap.style.display = 'none';
    _scan = null;
    return;
  }

  // Animate progress bar
  requestAnimationFrame(() => {
    if (fill) { fill.style.transition = `width ${durationMs}ms linear`; fill.style.width = '100%'; }
  });

  // Tick: update elapsed time every second
  _scan.tickTimer = setInterval(() => {
    if (!_scan) return;
    const sec = Math.round((Date.now() - _scan.startMs) / 1000);
    if (elapsed) elapsed.textContent = `${sec}s`;
  }, 1000);

  _scan.timer = setTimeout(_finalizeScan, durationMs);
}


async function disconnect() {
  try {
    await api('POST', '/api/disconnect');
    state.connected = false;
    updateConnectionUI();
    toast('Disconnected', 'warning');
  } catch (e) {
    toast(e.message, 'error');
  }
}

async function terminateAll() {
  if (!confirm('Terminate ALL active lights?')) return;
  try {
    await api('POST', '/api/terminate-all');
    toast('All lights terminated', 'warning');
  } catch (e) {
    toast(e.message, 'error');
  }
}

// ── Batch Builder ──────────────────────────────────────────────────────────
function renderBatchBuilder() {
  const tbody = $('batch-builder-body');
  tbody.innerHTML = '';
  if (state.batchItems.length === 0) {
    tbody.innerHTML = '<tr><td colspan="7" class="text-muted" style="text-align:center;padding:1rem">No items added yet</td></tr>';
    return;
  }
  state.batchItems.forEach((item, i) => {
    const tr = el('tr');
    const buzzIcon = item.buzzer ? '🔊' : '🔇';
    tr.innerHTML = `
      <td class="addr-cell">${item.address}</td>
      <td>${item.quantity}</td>
      <td><span class="color-dot" style="background:${COLOR_HEX[item.color] || '#374151'}"></span> ${item.color}</td>
      <td>${item.led_mode}</td>
      <td>${buzzIcon}</td>
      <td>${item.label || '—'}</td>
      <td><button class="btn btn-sm btn-ghost" onclick="removeBatchItem(${i})">✕</button></td>`;
    tbody.appendChild(tr);
  });
}

function addBatchItem() {
  const addr = $('batch-addr').value.trim().padStart(4, '0').slice(-4);
  const qty = parseInt($('batch-qty').value) || 1;
  const color = document.querySelector('#batch-color-picker .color-swatch.selected')?.dataset.color || 'GREEN';
  const led_mode = $('batch-mode').value || 'ON';
  const buzzer = $('batch-buzz')?.checked !== false;
  const label = $('batch-label').value.trim();

  if (!addr || addr === '0000') { toast('Valid address required', 'warning'); return; }

  state.batchItems.push({ address: addr, quantity: qty, color, led_mode, buzzer, label });
  renderBatchBuilder();

  // Reset quick fields
  $('batch-addr').value = '';
  $('batch-qty').value = '1';
  $('batch-label').value = '';
  $('batch-addr').focus();
}

function removeBatchItem(idx) {
  state.batchItems.splice(idx, 1);
  renderBatchBuilder();
}

async function startBatch() {
  if (state.batchItems.length === 0) { toast('Add items first', 'warning'); return; }
  const block_no = $('batch-block').value || '00';
  const buzz = $('batch-buzz')?.checked !== false;
  try {
    const result = await api('POST', '/api/batch/start', { block_no, buzz, items: state.batchItems });
    state.batchActive = true;
    state.batchPending = new Set(state.batchItems.map(i => i.address.padStart(4, '0').slice(-4)));
    state.batchItems = [];
    renderBatchBuilder();
    updateBatchStatus();
    toast(`Batch started: ${result.count} modules`, 'success');
  } catch (e) {
    toast(e.message, 'error');
  }
}

async function finishBatch() {
  try {
    await api('POST', '/api/batch/finish');
    state.batchActive = false;
    state.batchPending.clear();
    updateBatchStatus();
    toast('Batch finished', 'info');
  } catch (e) {
    toast(e.message, 'error');
  }
}

function updateBatchStatus() {
  const statusEl = $('batch-status');
  const btnFinish = $('btn-finish-batch');

  if (!state.batchActive) {
    statusEl.innerHTML = '<span class="text-muted">No active batch</span>';
    if (btnFinish) btnFinish.disabled = true;
    return;
  }

  const pending = state.batchPending.size;
  statusEl.innerHTML = `<span class="badge badge-blue">Active</span> &nbsp;${pending} pending confirmation(s)`;
  if (btnFinish) btnFinish.disabled = false;

  // Refresh the active batch table
  const tbody = $('active-batch-body');
  if (!tbody) return;
  const devices = Object.values(state.devices);
  tbody.innerHTML = '';
  devices.filter(d => d.status === 'OPERATING' || d.status === 'COMPLETED' || d.status === 'SHORTAGE')
    .sort((a, b) => a.address.localeCompare(b.address))
    .forEach(d => {
      const confirmed = d.status === 'COMPLETED' || d.status === 'SHORTAGE';
      const tr = el('tr');
      tr.innerHTML = `
        <td class="addr-cell">${d.address}</td>
        <td>${(d.adjusted_quantity || d.display_data || '').trim().replace(/^0+/, '') || '0'}</td>
        <td><span class="color-dot" style="background:${COLOR_HEX[d.color] || '#374151'}"></span></td>
        <td>${d.label || '—'}</td>
        <td>${confirmed ? '<span class="confirm-icon">✓</span>' : '<span class="pending-icon">○</span>'}</td>`;
      tbody.appendChild(tr);
    });
}

// ── Data Processing ────────────────────────────────────────────────────────

const MAX_EVENTS = 500;
const _evtStore  = [];        // { time, type, data }
const _opStore   = [];        // operating event rows
const _addrMap   = {};        // address → latest device state + updated time

function _resortAddressRow(tbody, row) {
  const order = parseInt(row.dataset.order) || 0;
  const addr  = row.dataset.addr || '';
  const rows  = [...tbody.querySelectorAll('tr[data-addr]')].filter(r => r !== row);
  const after = rows.find(r => {
    const ro = parseInt(r.dataset.order) || 0;
    return ro > order || (ro === order && (r.dataset.addr || '') > addr);
  });
  tbody.insertBefore(row, after || null);
}

function upsertAddressRow(d) {
  const time = new Date().toLocaleTimeString();
  _addrMap[d.address] = { ...d, _updated: time };

  const tbody = $('address-tbody');
  if (!tbody) return;

  const empty = $('address-empty-row');
  if (empty) empty.remove();

  const rawQty   = d.adjusted_quantity || d.display_data || '00000';
  const qty      = rawQty.trim().replace(/^0+/, '') || '0';
  const colorHex = COLOR_HEX[d.color] || '#374151';
  const statusClass = (d.status || 'STANDBY').toUpperCase();
  const orderNo  = d.order_no ?? 0;

  // Preserve any label the user has typed but not yet saved
  const existingRow = tbody.querySelector(`tr[data-addr="${d.address}"]`);
  const editingNow  = existingRow?.querySelector('.dp-label-input:focus');

  const html =
    `<td class="dp-order">${orderNo}</td>` +
    `<td class="dp-addr">${d.address}</td>` +
    `<td><span class="status-badge ${statusClass}">${d.status || '—'}</span></td>` +
    `<td class="dp-qty">${qty}</td>` +
    `<td><span class="dp-color-dot" style="background:${colorHex}"></span>${d.color || '—'}</td>` +
    `<td>${d.led_mode || '—'}</td>` +
    `<td>${d.block_no || '—'}</td>` +
    `<td class="dp-label-cell"><span class="dp-label-text">${escHtml(d.label || '')}</span>` +
      `<input class="dp-label-input" value="${escHtml(d.label || '')}" placeholder="label…"></td>` +
    `<td class="dp-time">${time}</td>`;

  let row = existingRow;
  if (row) {
    row.dataset.order = orderNo;
    if (!editingNow) {
      row.innerHTML = html;
      _bindLabelEdit(row, d.address);
    }
    row.classList.remove('dp-new');
    void row.offsetWidth;
    row.classList.add('dp-new');
    // Re-sort the row in case order_no changed
    _resortAddressRow(tbody, row);
  } else {
    row = el('tr', 'dp-new');
    row.dataset.addr = d.address;
    row.dataset.order = orderNo;
    row.innerHTML = html;
    _bindLabelEdit(row, d.address);
    _resortAddressRow(tbody, row);
  }
}

function _bindLabelEdit(row, address) {
  const text  = row.querySelector('.dp-label-text');
  const input = row.querySelector('.dp-label-input');
  if (!text || !input) return;

  text.addEventListener('click', () => {
    text.style.display = 'none';
    input.style.display = 'inline-block';
    input.focus();
    input.select();
  });

  async function saveLabel() {
    const newLabel = input.value.trim();
    text.textContent = newLabel || '';
    text.style.display = '';
    input.style.display = 'none';
    try {
      await api('PATCH', `/api/devices/${address}`, { label: newLabel });
      if (state.devices[address]) state.devices[address].label = newLabel;
    } catch (e) {
      toast(e.message, 'error');
    }
  }

  input.addEventListener('blur', saveLabel);
  input.addEventListener('keydown', e => {
    if (e.key === 'Enter') { e.preventDefault(); input.blur(); }
    if (e.key === 'Escape') { input.value = text.textContent; input.blur(); }
  });
}

function pushOperatingRow(d) {
  if (d.status !== 'OPERATING' && d.status !== 'OPERATING2') return;
  const time = new Date().toLocaleTimeString();
  _opStore.push({ time, ...d });
  if (_opStore.length > MAX_EVENTS) _opStore.shift();

  const tbody = $('operating-tbody');
  if (!tbody) return;

  // Remove empty-state row on first real row
  const empty = $('operating-empty-row');
  if (empty) empty.remove();

  const rawQty = d.adjusted_quantity || d.display_data || '00000';
  const qty    = rawQty.trim().replace(/^0+/, '') || '0';
  const colorHex = COLOR_HEX[d.color] || '#374151';
  const tr = el('tr', 'dp-new');
  tr.innerHTML =
    `<td class="dp-time">${time}</td>` +
    `<td class="dp-addr">${d.address}</td>` +
    `<td class="dp-qty">${qty}</td>` +
    `<td><span class="dp-color-dot" style="background:${colorHex}"></span>${d.color}</td>` +
    `<td>${d.led_mode || '—'}</td>` +
    `<td>${d.block_no || '—'}</td>` +
    `<td>${d.label || '—'}</td>`;
  tbody.insertBefore(tr, tbody.firstChild);
  while (tbody.rows.length > MAX_EVENTS) tbody.deleteRow(tbody.rows.length - 1);
}

const KNOWN_EVT_TYPES = ['confirmed','light_changed','batch_completed','bridge_status'];

function _colorizeJson(obj) {
  return JSON.stringify(obj, null, 2)
    .replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;')
    .replace(/("[\w]+")\s*:/g, '<span class="k">$1</span>:')
    .replace(/:\s*(".*?")/g, ': <span class="s">$1</span>')
    .replace(/:\s*(\d+\.?\d*)/g, ': <span class="n">$1</span>')
    .replace(/:\s*(true|false)/g, ': <span class="b">$1</span>');
}

function _renderEvtRow(evt) {
  const row = el('div', 'evt-row');
  const typeClass = KNOWN_EVT_TYPES.includes(evt.type) ? evt.type : 'other';
  row.dataset.evtType = evt.type;
  row.innerHTML =
    `<span class="evt-time">${evt.time}</span>` +
    `<span class="evt-type ${typeClass}">${evt.type}</span>` +
    `<span class="evt-payload">${_colorizeJson(evt.data)}</span>`;
  return row;
}

function pushEvent(type, data) {
  const time = new Date().toLocaleTimeString();
  const evt = { time, type, data };
  _evtStore.push(evt);
  if (_evtStore.length > MAX_EVENTS) _evtStore.shift();

  const filter = $('evt-filter')?.value || '';
  if (filter && filter !== type) return;

  const container = $('evt-container');
  if (!container) return;
  container.insertBefore(_renderEvtRow(evt), container.firstChild);
  // Trim rendered rows to MAX_EVENTS
  while (container.children.length > MAX_EVENTS) container.removeChild(container.lastChild);
}

function applyEvtFilter() {
  const filter = $('evt-filter')?.value || '';
  const container = $('evt-container');
  if (!container) return;
  container.innerHTML = '';
  const filtered = filter ? _evtStore.filter(e => e.type === filter) : _evtStore;
  [...filtered].reverse().forEach(evt => container.appendChild(_renderEvtRow(evt)));
}

// ── Events Feed ────────────────────────────────────────────────────────────
function addEvent(message, type = 'info') {
  const feed = $('events-feed');
  const item = el('div', 'event-item');
  const now = new Date().toLocaleTimeString();
  item.innerHTML = `<span class="event-type">${message}</span><span class="event-time">${now}</span>`;
  feed.insertBefore(item, feed.firstChild);
  // Keep max 20 events
  while (feed.children.length > 20) feed.removeChild(feed.lastChild);
}

// ── Logs ───────────────────────────────────────────────────────────────────
function appendLog(entry) {
  const container = $('log-container');
  const div = el('div', 'log-entry');
  const time = new Date(entry.time).toLocaleTimeString();
  div.innerHTML = `<span class="log-time">${time}</span><span class="log-level ${entry.level}">${entry.level.toUpperCase()}</span><span class="log-msg">${escHtml(entry.message)}</span>`;
  container.appendChild(div);
  container.scrollTop = container.scrollHeight;
}

async function loadLogs() {
  try {
    const data = await api('GET', '/api/logs');
    const container = $('log-container');
    container.innerHTML = '';
    data.logs.forEach(appendLog);
  } catch { /* ignore */ }
}

async function clearLogs() {
  await api('DELETE', '/api/logs');
  $('log-container').innerHTML = '';
}

// ── Settings ───────────────────────────────────────────────────────────────
async function loadConfig() {
  try {
    const cfg = await api('GET', '/api/config');
    const set = (id, val) => { const el = $(id); if (el) el.value = val; };
    const chk = (id, val) => { const el = $(id); if (el) el.checked = val; };

    set('cfg-block', cfg.block_no || '00');

    const mq = cfg.mqtt || {};
    set('cfg-mqtt-host',         mq.host || '');
    set('cfg-mqtt-port',         mq.port || 8883);
    set('cfg-mqtt-client-id',    mq.client_id || '');
    set('cfg-mqtt-username',     mq.username || '');
    set('cfg-mqtt-cmd-topic',    mq.command_topic || 'p2l/commands');
    set('cfg-mqtt-event-topic',  mq.event_topic || 'p2l/events');
    set('cfg-mqtt-devices-topic',mq.devices_topic || 'p2l/devices');
    chk('cfg-mqtt-tls',          mq.tls?.enabled ?? true);
    chk('cfg-mqtt-tls-untrusted',mq.tls?.allow_untrusted_certs || false);
    set('cfg-mqtt-ca-cert',      mq.tls?.ca_cert_path || '');

    const svc = cfg.service || {};
    set('cfg-svc-path',    svc.binary_path || '');
    set('cfg-svc-args',    (svc.args || []).join(' '));
    set('cfg-svc-workdir', svc.working_dir || '');
    chk('cfg-svc-autostart', svc.auto_start !== false);
  } catch (e) {
    toast(e.message, 'error');
  }
}

async function saveConfig() {
  const val = id => $(id)?.value ?? '';
  const chkd = id => $(id)?.checked ?? false;
  const argsRaw = val('cfg-svc-args').trim();
  const body = {
    block_no: val('cfg-block'),
    auto_connect: false,
    mqtt_host: val('cfg-mqtt-host'),
    mqtt_port: parseInt(val('cfg-mqtt-port')) || 8883,
    mqtt_client_id: val('cfg-mqtt-client-id'),
    mqtt_username: val('cfg-mqtt-username') || null,
    mqtt_password: val('cfg-mqtt-password') || null,
    mqtt_command_topic: val('cfg-mqtt-cmd-topic'),
    mqtt_event_topic: val('cfg-mqtt-event-topic'),
    mqtt_devices_topic: val('cfg-mqtt-devices-topic'),
    mqtt_tls_enabled: chkd('cfg-mqtt-tls'),
    mqtt_tls_allow_untrusted: chkd('cfg-mqtt-tls-untrusted'),
    mqtt_tls_ca_cert_path: val('cfg-mqtt-ca-cert') || null,
    service_binary_path: val('cfg-svc-path') || null,
    service_args: argsRaw ? argsRaw.split(/\s+/) : [],
    service_working_dir: val('cfg-svc-workdir') || null,
    service_auto_start: chkd('cfg-svc-autostart'),
  };
  try {
    await api('PUT', '/api/config', body);
    toast('Settings saved — restarting service…', 'success');
    try {
      await api('POST', '/api/self/restart');
    } catch (_) { /* service is restarting, connection will drop */ }
  } catch (e) {
    toast(e.message, 'error');
  }
}

// ── Bridge Appsettings ─────────────────────────────────────────────────────
async function loadAppsettings() {
  try {
    const d = await api('GET', '/api/appsettings');
    const set = (id, val) => { const el = $(id); if (el) el.value = val; };
    const chk = (id, val) => { const el = $(id); if (el) el.checked = val; };

    if ($('appsettings-path')) $('appsettings-path').textContent = d.path || '—';

    const gw = d.gateway || {};
    set('as-gw-host',  gw.host || '');
    set('as-gw-port',  gw.port ?? '');
    set('as-gw-block', gw.block_no || '');

    const mq = d.mqtt || {};
    chk('as-mqtt-enabled',    mq.enabled ?? true);
    set('as-mqtt-host',       mq.host || '');
    set('as-mqtt-port',       mq.port ?? '');
    set('as-mqtt-client-id',  mq.client_id || '');
    set('as-mqtt-username',   mq.username || '');
    set('as-mqtt-password',   '');   // never pre-fill passwords
    set('as-mqtt-db-path',    mq.client_db_path || '');
    set('as-mqtt-cmd-topic',  mq.command_topic || '');
    set('as-mqtt-event-topic',mq.event_topic || '');

    const tls = mq.tls || {};
    chk('as-mqtt-tls',          tls.enabled ?? false);
    chk('as-mqtt-tls-untrusted',tls.allow_untrusted ?? false);
    set('as-mqtt-ca-cert',      tls.ca_cert_path || '');

    const sh = d.selfheal || {};
    chk('as-sh-enabled',      sh.enabled ?? true);
    chk('as-state-persist',   d.enable_state_persistence ?? false);
    set('as-sh-max-restarts', sh.max_restarts ?? '');
    set('as-sh-window',       sh.restart_window_minutes ?? '');
    set('as-sh-backoff',      sh.backoff_seconds ?? '');

    const lvlSel = $('as-log-level');
    if (lvlSel) lvlSel.value = d.log_level || 'Information';

    if ($('appsettings-save-status')) $('appsettings-save-status').textContent = '';
  } catch (e) {
    if ($('appsettings-save-status')) $('appsettings-save-status').textContent = 'Load failed: ' + e.message;
  }
}

async function saveAppsettings() {
  const body = {
    gateway_host: $('as-gw-host').value || null,
    gateway_port: parseInt($('as-gw-port').value) || null,
    gateway_block_no: $('as-gw-block').value || null,
    mqtt_enabled: $('as-mqtt-enabled').checked,
    mqtt_host: $('as-mqtt-host').value || null,
    mqtt_port: parseInt($('as-mqtt-port').value) || null,
    mqtt_client_id: $('as-mqtt-client-id').value || null,
    mqtt_username: $('as-mqtt-username').value || null,
    mqtt_password: $('as-mqtt-password').value || null,
    mqtt_client_db_path: $('as-mqtt-db-path').value || null,
    mqtt_command_topic: $('as-mqtt-cmd-topic').value || null,
    mqtt_event_topic: $('as-mqtt-event-topic').value || null,
    mqtt_tls_enabled: $('as-mqtt-tls').checked,
    mqtt_tls_allow_untrusted: $('as-mqtt-tls-untrusted').checked,
    mqtt_tls_ca_cert_path: $('as-mqtt-ca-cert').value || null,
    selfheal_enabled: $('as-sh-enabled').checked,
    enable_state_persistence: $('as-state-persist').checked,
    selfheal_max_restarts: parseInt($('as-sh-max-restarts').value) || null,
    selfheal_restart_window_minutes: parseInt($('as-sh-window').value) || null,
    selfheal_backoff_seconds: parseInt($('as-sh-backoff').value) || null,
    log_level: $('as-log-level')?.value || null,
  };
  const status = $('appsettings-save-status');
  try {
    await api('PUT', '/api/appsettings', body);
    status.style.color = 'var(--success)';
    status.textContent = '✓ Saved';
    setTimeout(() => { status.textContent = ''; }, 3000);
  } catch (e) {
    status.style.color = 'var(--danger)';
    status.textContent = 'Error: ' + e.message;
  }
}

// ── Device Export ──────────────────────────────────────────────────────────
function exportDevicesCSV() {
  const devices = Object.values(state.devices).sort((a, b) => {
    const oa = a.order_no ?? 0, ob = b.order_no ?? 0;
    return oa !== ob ? oa - ob : a.address.localeCompare(b.address);
  });
  if (devices.length === 0) { toast('No devices to export', 'warning'); return; }

  function csvStr(v) {
    const s = String(v ?? '').replace(/"/g, '""');
    return `"${s}"`;
  }

  const rows = ['order_no,address,label,status,color,qty'];
  devices.forEach(d => {
    const qty = (d.adjusted_quantity || d.display_data || '00000').trim().replace(/^0+/, '') || '0';
    rows.push([
      d.order_no ?? 0,
      csvStr(d.address),        // quoted → preserves leading zeros (e.g. "0042")
      csvStr(d.label),          // quoted → safe against commas in label
      d.status,
      d.color,
      qty,
    ].join(','));
  });

  const blob = new Blob([rows.join('\n')], { type: 'text/csv' });
  const url = URL.createObjectURL(blob);
  const a = el('a');
  a.href = url;
  a.download = `devices_${new Date().toISOString().slice(0,10)}.csv`;
  a.click();
  URL.revokeObjectURL(url);
  toast(`Exported ${devices.length} device(s)`, 'success');
}

// ── CSV Sample Download ────────────────────────────────────────────────────
function downloadSampleCSV() {
  // Generate sample with actual device labels from database for easier mapping
  const deviceLabels = Object.entries(state.devices)
    .slice(0, 4)
    .map(([addr, dev]) => ({ addr, label: dev.label || `Device-${addr}` }))
    .filter(d => d.label);
  
  // Create label-based format (NEW - recommended)
  // Format: label,quantity,color,led_mode,buzzer
  const ledModes = ['ON', 'FLASH', 'HIGH_FLASH'];
  const labelFormatRows = [
    'label,quantity,color,led_mode,buzzer',
    ...(deviceLabels.length > 0 
      ? deviceLabels.map((d, i) => `${d.label},${i + 1},${COLORS[i % COLORS.length]},${ledModes[i % ledModes.length]},${i % 2 === 0 ? 'yes' : 'no'}`)
      : [
          'Shelf-A1,5,GREEN,ON,yes',
          'Shelf-A2,3,YELLOW,FLASH,yes',
          'Shelf-B1,10,RED,HIGH_FLASH,no',
          'Shelf-B2,1,BLUE,ON,no'
        ]),
  ];
  
  const blob = new Blob([labelFormatRows.join('\n')], { type: 'text/csv' });
  const url = URL.createObjectURL(blob);
  const a = el('a');
  a.href = url;
  a.download = 'batch_sample.csv';
  a.click();
  URL.revokeObjectURL(url);
}

// ── Label to Address Mapping ──────────────────────────────────────────────────
// Lookup light label in devices database and return corresponding address
function mapLabelToAddress(label) {
  if (!label) return null;
  const labelLower = label.toLowerCase();
  for (const [addr, device] of Object.entries(state.devices)) {
    if (device.label && device.label.toLowerCase() === labelLower) {
      return addr;
    }
  }
  return null;
}

// ── CSV Import ────────────────────────────────────────────────────────────
function importCSV() {
  const input = el('input');
  input.type = 'file';
  input.accept = '.csv,text/csv';
  input.onchange = e => {
    const file = e.target.files[0];
    if (!file) return;
    const reader = new FileReader();
    reader.onload = evt => {
      const lines = evt.target.result.split('\n').filter(l => l.trim());
      let importedCount = 0;
      let skippedCount = 0;
      const unmappedLabels = [];
      
      lines.forEach((line, idx) => {
        const parts = line.split(',').map(p => p.trim());
        if (parts.length < 1) return;
        
        // Skip header rows (label,quantity,color OR address,quantity,color,label OR new format with led_mode,buzzer)
        if (idx === 0 && (
          (parts[0].toLowerCase() === 'label' && parts[1]?.toLowerCase() === 'quantity') ||
          (parts[0].toLowerCase() === 'address' && parts[1]?.toLowerCase() === 'quantity')
        )) {
          return;
        }
        
        // Parse CSV: Can be (label, qty, color) or (address, qty, color, label) or new format with led_mode,buzzer
        let address = null;
        let quantity = 1;
        let color = 'GREEN';
        let led_mode = 'ON';
        let buzzer = true;  // Default buzzer on
        let label = '';
        
        // Try to detect format: if first column is numeric, treat as address; otherwise as label
        const firstCol = parts[0];
        const isNumeric = /^\d+$/.test(firstCol);
        
        if (isNumeric) {
          // Format: address, qty, color, [label, led_mode, buzzer] (backward compatible)
          address = firstCol.padStart(4, '0').slice(-4);
          quantity = parseInt(parts[1]) || 1;
          color = parts[2] ? parts[2].toUpperCase() : 'GREEN';
          label = parts[3] || '';
          led_mode = parts[4] ? parts[4].toUpperCase() : 'ON';
          buzzer = parts[5] ? /^(yes|true|1|on)$/i.test(parts[5]) : true;
        } else {
          // Format: label, qty, color, [led_mode, buzzer] (NEW - map label to address from database)
          label = firstCol;
          quantity = parseInt(parts[1]) || 1;
          color = parts[2] ? parts[2].toUpperCase() : 'GREEN';
          led_mode = parts[3] ? parts[3].toUpperCase() : 'ON';
          buzzer = parts[4] ? /^(yes|true|1|on)$/i.test(parts[4]) : true;
          
          // Auto-map label to address from devices database
          address = mapLabelToAddress(label);
          if (!address) {
            unmappedLabels.push(label);
            skippedCount++;
            return;
          }
        }
        
        // Validate address, color, and led_mode
        if (!address || !/^\d{4}$/.test(address)) return;
        if (!COLORS.includes(color)) color = 'GREEN';
        if (!LED_MODES.includes(led_mode)) led_mode = 'ON';
        
        state.batchItems.push({
          address,
          quantity: Math.max(1, Math.min(quantity, 99999)),
          color,
          led_mode,
          buzzer,
          label,
        });
        importedCount++;
      });
      
      renderBatchBuilder();
      
      let msg = `Imported ${importedCount} item(s)`;
      if (skippedCount > 0) {
        msg += ` • ${skippedCount} skipped (unmapped: ${unmappedLabels.join(', ')})`;
        toast(msg, 'warning');
      } else {
        toast(msg, 'success');
      }
    };
    reader.readAsText(file);
  };
  input.click();
}

// ── API helper ────────────────────────────────────────────────────────────
async function api(method, path, body) {
  const opts = { method, headers: { 'Content-Type': 'application/json' } };
  if (body && method !== 'GET') opts.body = JSON.stringify(body);
  const res = await fetch(path, opts);
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.detail || `HTTP ${res.status}`);
  return data;
}

function escHtml(s) {
  return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

// ── Init ───────────────────────────────────────────────────────────────────
function buildColorPickers() {
  document.querySelectorAll('.color-picker').forEach(picker => {
    picker.innerHTML = '';
    COLORS.forEach(c => {
      const sw = el('div', `color-swatch swatch-${c}`);
      sw.dataset.color = c;
      sw.title = c;
      sw.addEventListener('click', () => {
        picker.querySelectorAll('.color-swatch').forEach(s => s.classList.remove('selected'));
        sw.classList.add('selected');
      });
      picker.appendChild(sw);
    });
    // Default select
    const def = picker.dataset.default || 'GREEN';
    const defSwatch = picker.querySelector(`[data-color="${def}"]`);
    if (defSwatch) defSwatch.classList.add('selected');
  });
}

async function init() {
  initTabs();
  initSubTabs();
  buildColorPickers();

  // Wire up header buttons
  $('btn-connect').addEventListener('click', connect);
  $('btn-disconnect').addEventListener('click', disconnect);

  // Wire up dashboard buttons
  $('btn-terminate-all')?.addEventListener('click', terminateAll);

  // Wire up device tab
  $('btn-terminate-all-devices')?.addEventListener('click', terminateAll);
  $('btn-export-devices')?.addEventListener('click', exportDevicesCSV);
  $('btn-add-device')?.addEventListener('click', openAddDeviceModal);

  // Wire up add device modal
  $('btn-confirm-add-device')?.addEventListener('click', confirmAddDevice);
  $('btn-cancel-add-device')?.addEventListener('click', closeAddDeviceModal);
  $('add-device-modal')?.addEventListener('click', e => { if (e.target === $('add-device-modal')) closeAddDeviceModal(); });

  // Wire up light modal
  $('btn-confirm-light')?.addEventListener('click', confirmLightOn);
  $('btn-cancel-light')?.addEventListener('click', closeLightModal);
  $('light-modal')?.addEventListener('click', e => { if (e.target === $('light-modal')) closeLightModal(); });

  // Wire up batch tab
  $('btn-add-batch-item')?.addEventListener('click', addBatchItem);
  $('btn-start-batch')?.addEventListener('click', startBatch);
  $('btn-finish-batch')?.addEventListener('click', finishBatch);
  $('btn-terminate-all-batch')?.addEventListener('click', terminateAll);
  $('btn-import-csv')?.addEventListener('click', importCSV);
  $('btn-download-sample-csv')?.addEventListener('click', downloadSampleCSV);
  $('batch-addr')?.addEventListener('keydown', e => { if (e.key === 'Enter') { $('batch-qty').focus(); } });
  $('batch-qty')?.addEventListener('keydown', e => { if (e.key === 'Enter') addBatchItem(); });

  // Wire up settings
  $('btn-save-config')?.addEventListener('click', saveConfig);
  $('btn-save-appsettings')?.addEventListener('click', saveAppsettings);

  // Wire up data processing tab
  $('btn-clear-address-table')?.addEventListener('click', () => {
    Object.keys(_addrMap).forEach(k => delete _addrMap[k]);
    const tbody = $('address-tbody');
    if (tbody) {
      tbody.innerHTML =
        '<tr id="address-empty-row"><td colspan="9" class="text-muted"' +
        ' style="text-align:center;padding:1.5rem">No data yet. Waiting for events…</td></tr>';
    }
  });
$('btn-clear-events')?.addEventListener('click', () => {
    _evtStore.length = 0;
    const c = $('evt-container');
    if (c) c.innerHTML = '';
  });
  $('evt-filter')?.addEventListener('change', applyEvtFilter);

  // Wire up logs
  $('btn-clear-logs')?.addEventListener('click', clearLogs);

  // Load initial data
  await loadConfig();
  await loadLogs();
  refreshServiceBadge();

  // Load initial status
  try {
    const status = await api('GET', '/api/status');
    state.connected = status.connected;
    state.mqttConnected = status.mqtt_connected || false;
    state.host = status.host;
    state.port = status.port;
    updateConnectionUI();
  } catch { /* ignore */ }

  // Load initial devices
  try {
    const data = await api('GET', '/api/devices');
    state.devices = {};
    data.devices.forEach(d => { state.devices[d.address] = d; });
    renderDevices();
  } catch { /* ignore */ }

  // Connect WebSocket
  connectWs();

  // Periodic status sync — keeps badge in sync if WS message was missed
  setInterval(async () => {
    try {
      const s = await api('GET', '/api/status');
      state.connected = s.connected;
      state.mqttConnected = s.mqtt_connected || false;
      state.host = s.host;
      state.port = s.port;
      updateConnectionUI();
    } catch { /* ignore */ }
  }, 5000);

  // Poll batch status periodically
  setInterval(async () => {
    if (!state.connected) return;
    try {
      const bs = await api('GET', '/api/batch/status');
      state.batchActive = bs.active;
      if (bs.active) {
        state.batchPending = new Set(bs.pending);
        updateBatchStatus();
      }
    } catch { /* ignore */ }
  }, 5000);
}

document.addEventListener('DOMContentLoaded', init);

// Disconnect MQTT immediately when the browser tab/window closes.
window.addEventListener('beforeunload', () => {
  if (state.mqttConnected || state.connected) {
    navigator.sendBeacon('/api/disconnect');
  }
});
