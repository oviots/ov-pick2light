/**
 * P2L SDK — Node.js client for Pick2Light bridge.
 *
 * Communicates with the P2L .NET bridge via MQTT.
 * The bridge must be running and connected to the Aioi gateway.
 *
 * Install:
 *   npm install mqtt
 *
 * Quick start:
 *   const { P2LClient, LightColor, LightMode } = require('./p2l');
 *
 *   const p2l = new P2LClient('localhost');
 *   await p2l.connect();
 *   p2l.lightOn('0001', { quantity: 5, color: LightColor.GREEN });
 *   const event = await p2l.waitConfirm('0001', 30_000);
 *   console.log(event);
 *   await p2l.disconnect();
 */

'use strict';

const mqtt = require('mqtt');

// ---------------------------------------------------------------------------
// Enums
// ---------------------------------------------------------------------------

const LightColor = Object.freeze({
  OFF:     'Off',
  RED:     'Red',
  GREEN:   'Green',
  BLUE:    'Blue',
  YELLOW:  'Yellow',
  CYAN:    'Cyan',
  MAGENTA: 'Magenta',
  WHITE:   'White',
});

const LightMode = Object.freeze({
  OFF:        'Off',
  ON:         'On',
  FLASH:      'Flash',
  HIGH_FLASH: 'HighFlash',
  NO_CHANGE:  'NoChange',
});

const CompletionStatus = Object.freeze({
  NORMAL:   'Normal',
  ABNORMAL: 'Abnormal',
  SHORTAGE: 'Shortage',
});

// ---------------------------------------------------------------------------
// P2LClient
// ---------------------------------------------------------------------------

class P2LClient {
  /**
   * @param {string} host  - MQTT broker hostname
   * @param {number} [port=8883] - MQTT broker port
   * @param {object} [opts]
   * @param {string} [opts.username]
   * @param {string} [opts.password]
   * @param {string} [opts.clientId='p2l-sdk']
   * @param {object} [opts.tls]           - passed directly to mqtt.connect as `tls` option
   * @param {boolean} [opts.tls.rejectUnauthorized=true]
   * @param {string}  [opts.tls.ca]       - CA cert string (PEM)
   * @param {string}  [opts.tls.cert]     - client cert string (PEM)
   * @param {string}  [opts.tls.key]      - client key string (PEM)
   */
  constructor(host, port = 8883, opts = {}) {
    this._host     = host;
    this._port     = port;
    this._clientId = opts.clientId ?? 'p2l-sdk';

    this._mqttOpts = {
      clientId:    this._clientId,
      username:    opts.username,
      password:    opts.password,
      // Port 8883 = standard MQTTS: auto-enable TLS unless caller explicitly
      // passes tls: false to opt out (same convention as HTTPS on 443).
      protocol:    (opts.tls !== false && port === 8883) || opts.tls ? 'mqtts' : 'mqtt',
      rejectUnauthorized: opts.tls?.rejectUnauthorized ?? true,
      ...opts.tls,
    };

    this._client = null;

    /** @type {Map<string, {resolve: Function, reject: Function, timer: NodeJS.Timeout}>} */
    this._confirmWaiters = new Map();
    /** @type {{resolve: Function, reject: Function, timer: NodeJS.Timeout} | null} */
    this._batchWaiter = null;
    /** @type {{resolve: Function, reject: Function, timer: NodeJS.Timeout} | null} */
    this._csvCompleteWaiter = null;

    // User callbacks
    this._onConfirm       = null;
    this._onBatchComplete = null;
    this._onStateChange   = null;
    this._onBridgeStatus  = null;

    // Task-event (data-processing) callbacks
    this._onTaskEvent     = null;
    this._onTaskStarted   = null;
    this._onTaskConfirmed = null;
    this._onTaskError     = null;
    this._onCsvComplete   = null;

    /** @type {Map<string, object>} device state cache: address -> state */
    this._states = new Map();
  }

  // -------------------------------------------------------------------------
  // Connection
  // -------------------------------------------------------------------------

  /**
   * Connect to the MQTT broker.
   * @param {number} [timeoutMs=10000]
   * @returns {Promise<void>}
   */
  connect(timeoutMs = 10_000) {
    return new Promise((resolve, reject) => {
      const url = `${this._mqttOpts.protocol}://${this._host}:${this._port}`;
      this._client = mqtt.connect(url, this._mqttOpts);

      const timer = setTimeout(() => {
        this._client.end(true);
        reject(new Error(`Could not connect to broker at ${this._host}:${this._port}`));
      }, timeoutMs);

      this._client.once('connect', () => {
        clearTimeout(timer);
        this._client.subscribe(['p2l/events', 'p2l/devices/#', 'p2l/status'], { qos: 1 });
        resolve();
      });

      this._client.once('error', (err) => {
        clearTimeout(timer);
        reject(err);
      });

      this._client.on('message', (topic, payload) => {
        this._dispatch(topic, payload);
      });
    });
  }

  /**
   * Disconnect from the broker.
   * @returns {Promise<void>}
   */
  disconnect() {
    return new Promise((resolve) => {
      if (this._client) {
        this._client.end(false, {}, resolve);
      } else {
        resolve();
      }
    });
  }

  // -------------------------------------------------------------------------
  // Internal dispatch
  // -------------------------------------------------------------------------

  _dispatch(topic, payload) {
    let data;
    try {
      data = JSON.parse(payload.toString());
    } catch {
      return;
    }

    const kind = data.type;

    if (kind === 'confirmed') {
      const ev = {
        address:         data.address,
        status:          data.status ?? 'Normal',
        adjustedDisplay: data.adjustedDisplay ?? null,
        label:           data.label ?? null,
        displayData:     data.displayData ?? null,
      };
      const waiter = this._confirmWaiters.get(ev.address);
      if (waiter) {
        clearTimeout(waiter.timer);
        this._confirmWaiters.delete(ev.address);
        waiter.resolve(ev);
      }
      this._onConfirm?.(ev);

    } else if (kind === 'batch_completed') {
      if (this._batchWaiter) {
        clearTimeout(this._batchWaiter.timer);
        this._batchWaiter.resolve();
        this._batchWaiter = null;
      }
      this._onBatchComplete?.();

    } else if (kind === 'task_started' || kind === 'task_confirmed' ||
               kind === 'task_error'   || kind === 'csv_completed') {
      const ev = {
        eventType: kind,
        address:   data.address   ?? '',
        quantity:  data.quantity  ?? 0,
        color:     data.color     ?? '',
        label:     data.label     ?? null,
        timestamp: data.timestamp ?? null,
      };
      this._onTaskEvent?.(ev);
      if (kind === 'task_started')   this._onTaskStarted?.(ev);
      if (kind === 'task_confirmed') this._onTaskConfirmed?.(ev);
      if (kind === 'task_error')     this._onTaskError?.(ev);
      if (kind === 'csv_completed') {
        if (this._csvCompleteWaiter) {
          clearTimeout(this._csvCompleteWaiter.timer);
          this._csvCompleteWaiter.resolve();
          this._csvCompleteWaiter = null;
        }
        this._onCsvComplete?.();
      }

    } else if (kind === 'light_changed') {
      const state = {
        address:      data.address,
        moduleStatus: data.moduleStatus,
        color:        data.color,
        ledMode:      data.ledMode,
        displayData:  data.displayData,
        blockNo:      data.blockNo,
        label:        data.label ?? null,
      };
      this._states.set(state.address, state);
      this._onStateChange?.(state);

    } else if (kind === 'bridge_status') {
      const bs = {
        online:           data.online,
        gatewayConnected: data.gatewayConnected,
      };
      this._onBridgeStatus?.(bs);
    }
  }

  // -------------------------------------------------------------------------
  // Event subscriptions
  // -------------------------------------------------------------------------

  /** @param {(event: {address, status, adjustedDisplay}) => void} fn */
  onConfirm(fn)       { this._onConfirm = fn; }

  /** @param {() => void} fn */
  onBatchComplete(fn) { this._onBatchComplete = fn; }

  /** @param {(state: object) => void} fn */
  onStateChange(fn)   { this._onStateChange = fn; }

  /**
   * Return a `{label: address}` map built from device states received since connect.
   * Devices without a label are excluded.
   * @returns {Object.<string, string>}
   */
  labelMap() {
    const result = {};
    for (const state of this._states.values()) {
      if (state.label) result[state.label] = state.address;
    }
    return result;
  }

  /** @param {(status: {online, gatewayConnected}) => void} fn */
  onBridgeStatus(fn)  { this._onBridgeStatus = fn; }

  // -------------------------------------------------------------------------
  // Data-processing (task_event) subscriptions
  // -------------------------------------------------------------------------

  /** @param {(ev: {eventType, address, quantity, color, label, timestamp}) => void} fn */
  onTaskEvent(fn)     { this._onTaskEvent = fn; }

  /** @param {(ev: {eventType, address, quantity, color, label, timestamp}) => void} fn */
  onTaskStarted(fn)   { this._onTaskStarted = fn; }

  /** @param {(ev: {eventType, address, quantity, color, label, timestamp}) => void} fn */
  onTaskConfirmed(fn) { this._onTaskConfirmed = fn; }

  /** @param {(ev: {eventType, address, quantity, color, label, timestamp}) => void} fn */
  onTaskError(fn)     { this._onTaskError = fn; }

  /** @param {() => void} fn */
  onCsvComplete(fn)   { this._onCsvComplete = fn; }

  // -------------------------------------------------------------------------
  // Wait helpers
  // -------------------------------------------------------------------------

  /**
   * Wait until the operator presses the button on *address*.
   * @param {string} address
   * @param {number} [timeoutMs=30000]
   * @returns {Promise<{address, status, adjustedDisplay}>}
   */
  waitConfirm(address, timeoutMs = 30_000) {
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this._confirmWaiters.delete(address);
        reject(new Error(`No confirmation from ${address} within ${timeoutMs}ms`));
      }, timeoutMs);
      this._confirmWaiters.set(address, { resolve, reject, timer });
    });
  }

  /**
   * Wait until all items in the current batch are confirmed.
   * @param {number} [timeoutMs=120000]
   * @returns {Promise<void>}
   */
  waitBatchComplete(timeoutMs = 120_000) {
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this._batchWaiter = null;
        reject(new Error(`Batch not completed within ${timeoutMs}ms`));
      }, timeoutMs);
      this._batchWaiter = { resolve, reject, timer };
    });
  }

  /**
   * Wait until every row in the running CSV pick task has been confirmed.
   * @param {number} [timeoutMs=300000]
   * @returns {Promise<void>}
   */
  waitCsvComplete(timeoutMs = 300_000) {
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this._csvCompleteWaiter = null;
        reject(new Error(`CSV pick task not completed within ${timeoutMs}ms`));
      }, timeoutMs);
      this._csvCompleteWaiter = { resolve, reject, timer };
    });
  }

  // -------------------------------------------------------------------------
  // Commands
  // -------------------------------------------------------------------------

  _publish(payload) {
    if (!this._client?.connected) throw new Error('Not connected to broker');
    this._client.publish('p2l/commands', JSON.stringify(payload), { qos: 1 });
  }

  /**
   * Turn on a single light module.
   * @param {string} address - 4-digit address, e.g. "0001"
   * @param {object} [opts]
   * @param {number}    [opts.quantity=1]
   * @param {string}    [opts.color=LightColor.GREEN]
   * @param {string}    [opts.ledMode=LightMode.ON]
   * @param {string}    [opts.buzMode]   - omit to mirror ledMode
   * @param {string}    [opts.label]
   */
  lightOn(address, opts = {}) {
    const cmd = {
      type:     'light_on',
      address,
      quantity: opts.quantity ?? 1,
      color:    opts.color   ?? LightColor.GREEN,
      ledMode:  opts.ledMode ?? LightMode.ON,
    };
    if (opts.buzMode !== undefined) cmd.buzMode = opts.buzMode;
    if (opts.label)                 cmd.label   = opts.label;
    this._publish(cmd);
  }

  /**
   * Turn off a single light module.
   * @param {string} address
   */
  lightOff(address) {
    this._publish({ type: 'light_off', address });
  }

  /**
   * Start a picking batch.
   * Each item may have ``address`` (hardware address) **or** ``label`` (position label).
   * If ``address`` is omitted, the P2L bridge resolves it from ``label`` via the
   * client device database.  Items that cannot be resolved are skipped by the bridge.
   * @param {Array<{address?, quantity, color?, label?, ledMode?, buzMode?}>} items
   * @param {object} [opts]
   * @param {string} [opts.ledMode=LightMode.ON]
   * @param {string} [opts.buzMode]  - omit to mirror ledMode
   */
  batchStart(items, opts = {}) {
    const cmd = {
      type:    'batch_start',
      ledMode: opts.ledMode ?? LightMode.ON,
      items:   items.map(i => {
        const item = {
          quantity: i.quantity,
          color:    i.color ?? LightColor.GREEN,
          ledMode:  i.ledMode ?? LightMode.ON,
        };
        if (i.address)             item.address = i.address;
        if (i.label)               item.label   = i.label;
        if (i.buzMode !== undefined) item.buzMode = i.buzMode;
        return item;
      }),
    };
    if (opts.buzMode !== undefined) cmd.buzMode = opts.buzMode;
    this._publish(cmd);
  }

  /**
   * Terminate the current batch.
   * @param {string} [blockNo='00']
   */
  batchFinish(blockNo = '00') {
    this._publish({ type: 'batch_finish', blockNo });
  }

  /** Emergency: turn off all modules immediately. */
  terminateAll() {
    this._publish({ type: 'terminate_all' });
  }
}

// ---------------------------------------------------------------------------
// Wave dispatcher
// ---------------------------------------------------------------------------

/**
 * One item in a wave dispatch queue.
 *
 * @typedef {Object} DispatchItem
 * @property {string}  sku       - Displayed as label on the light module
 * @property {number}  quantity  - Number shown on segment display
 * @property {string}  [color]   - LED ring color (default GREEN)
 * @property {string}  [ledMode] - LED mode (default ON)
 * @property {string}  [buzMode] - Buzzer mode (optional)
 */

/**
 * Distributes a queue of picking items across multiple light positions in parallel.
 *
 * Positions act as parallel workers: each is filled immediately when it becomes
 * free (operator presses CONFIRM). The wave finishes when every item has been
 * dispatched **and** confirmed.
 *
 * @example
 * const items = [
 *   { sku: 'SKU-001', quantity: 5 },
 *   { sku: 'SKU-002', quantity: 3, color: LightColor.BLUE },
 * ];
 * const dispatcher = new WaveDispatcher(p2l, items, { positions: ['0001', '0002'] });
 * dispatcher.onItemSent      = (addr, item) => console.log(`→ ${addr} ${item.sku}`);
 * dispatcher.onItemConfirmed = (addr, item, evt) => console.log(`✓ ${addr} ${item.sku}`);
 * dispatcher.onWaveComplete  = (stats) => console.log('Done', stats);
 * dispatcher.start();
 * await dispatcher.wait();
 */
class WaveDispatcher {
  /**
   * @param {P2LClient}     client
   * @param {DispatchItem[]} items
   * @param {object}         opts
   * @param {string[]}       opts.positions - Array of device addresses
   */
  constructor(client, items, { positions }) {
    this._client    = client;
    this._queue     = [...items];          // treated as FIFO queue
    this._positions = [...positions];
    /** @type {Object.<string, DispatchItem|null>} address -> active item or null */
    this._active    = Object.fromEntries(positions.map(p => [p, null]));
    this._total     = items.length;
    this._confirmed = 0;
    this._cancelled = false;
    this._prevHandler = null;

    let _resolve;
    /** @type {Promise<{total, confirmed, positions, cancelled}>} */
    this._donePromise = new Promise(r => { _resolve = r; });
    this._resolve = _resolve;

    /** @type {((address: string, item: DispatchItem) => void) | null} */
    this.onItemSent      = null;
    /** @type {((address: string, item: DispatchItem, evt: object) => void) | null} */
    this.onItemConfirmed = null;
    /** @type {((stats: {total, confirmed, positions, cancelled}) => void) | null} */
    this.onWaveComplete  = null;
  }

  // -------------------------------------------------------------------------
  // Lifecycle
  // -------------------------------------------------------------------------

  /** Begin the wave: fill all positions with items from the queue. */
  start() {
    if (this._total === 0) {
      this._finish();
      return;
    }
    this._prevHandler = this._client._onConfirm;
    this._client._onConfirm = (evt) => this._handleConfirm(evt);
    for (const addr of this._positions) {
      this._tryDispatch(addr);
    }
  }

  /**
   * Wait until all items are confirmed (or timeout).
   * @param {number} [timeoutMs]
   * @returns {Promise<{total, confirmed, positions, cancelled}>}
   */
  wait(timeoutMs) {
    if (!timeoutMs) return this._donePromise;
    return Promise.race([
      this._donePromise,
      new Promise((_, reject) =>
        setTimeout(() => reject(new Error(
          `Wave did not complete within ${timeoutMs}ms ` +
          `(${this._confirmed}/${this._total} confirmed)`
        )), timeoutMs)
      ),
    ]);
  }

  /** Cancel the wave: turn off all active lights and stop dispatching. */
  cancel() {
    if (this._cancelled) return;
    this._cancelled = true;
    this._queue.length = 0;
    for (const [addr, item] of Object.entries(this._active)) {
      if (item !== null) {
        this._client.lightOff(addr);
        this._active[addr] = null;
      }
    }
    this._client._onConfirm = this._prevHandler;
    this._resolve({ total: this._total, confirmed: this._confirmed,
                    positions: this._positions.length, cancelled: true });
  }

  // -------------------------------------------------------------------------
  // Internal helpers
  // -------------------------------------------------------------------------

  _tryDispatch(address) {
    if (this._cancelled) return;
    if (this._queue.length === 0) {
      this._active[address] = null;
      if (Object.values(this._active).every(v => v === null)) {
        this._finish();
      }
      return;
    }
    const item = this._queue.shift();
    this._active[address] = item;
    this._client.lightOn(address, {
      quantity: item.quantity,
      color:    item.color   ?? LightColor.GREEN,
      ledMode:  item.ledMode ?? LightMode.ON,
      ...(item.buzMode !== undefined ? { buzMode: item.buzMode } : {}),
      label:    item.sku,
    });
    this.onItemSent?.(address, item);
  }

  _handleConfirm(evt) {
    if (!(evt.address in this._active)) {
      this._prevHandler?.(evt);
      return;
    }
    const item = this._active[evt.address];
    if (item === null) return;
    this._confirmed++;
    this.onItemConfirmed?.(evt.address, item, evt);
    this._tryDispatch(evt.address);
  }

  _finish() {
    this._client._onConfirm = this._prevHandler;
    const stats = {
      total:     this._total,
      confirmed: this._confirmed,
      positions: this._positions.length,
      cancelled: false,
    };
    this.onWaveComplete?.(stats);
    this._resolve(stats);
  }
}

// ---------------------------------------------------------------------------
// Batch dispatcher  (items pre-assigned to positions by label)
// ---------------------------------------------------------------------------

/**
 * Dispatches a batch where every item targets a specific position by *label*.
 *
 * Items sharing the same label are queued and processed **one at a time** at
 * that position. All positions run in **parallel**.
 *
 * The label→address mapping is resolved automatically from device states
 * already cached by the client (populated from retained `light_changed`
 * messages received after `connect()`).
 *
 * @example
 * const payload    = JSON.parse(rawJson);
 * const batch      = BatchDispatch.fromDict(payload);
 * const dispatcher = new BatchDispatcher(p2l, batch);
 *
 * dispatcher.onItemSent      = (addr, item) => console.log(`→ [${item.label}] ${item.sku}`);
 * dispatcher.onItemConfirmed = (addr, item, evt) => console.log(`✓ [${item.label}] ${item.sku}`);
 * dispatcher.onBatchComplete = (stats) => console.log('Done', stats);
 *
 * dispatcher.start();
 * const stats = await dispatcher.wait();
 */
class BatchDispatcher {
  /**
   * @param {P2LClient}  client
   * @param {BatchDispatch} batch
   * @param {object}     [opts]  - reserved for future use
   */
  constructor(client, batch, opts = {}) {
    this._client   = client;
    this._batchId  = batch.batchId;

    const labelMap = client.labelMap();

    // Group items by address, preserving order per position
    /** @type {Object.<string, Array>} */
    this._queues = {};
    const unknown = [];
    for (const item of batch.items) {
      const addr = labelMap[item.label];
      if (addr === undefined) {
        if (!unknown.includes(item.label)) unknown.push(item.label);
        continue;
      }
      if (!this._queues[addr]) this._queues[addr] = [];
      this._queues[addr].push(item);
    }
    if (unknown.length > 0) {
      throw new Error(
        `BatchDispatcher: label(s) not found in device states: ${unknown.join(', ')}. ` +
        `Available: ${Object.keys(labelMap).join(', ')}`
      );
    }

    /** @type {Object.<string, object|null>} */
    this._active      = Object.fromEntries(Object.keys(this._queues).map(a => [a, null]));
    this._total       = batch.items.length;
    this._confirmed   = 0;
    this._cancelled   = false;
    this._prevHandler = null;

    let _resolve;
    this._donePromise = new Promise(r => { _resolve = r; });
    this._resolve = _resolve;

    /** @type {((address: string, item: object) => void) | null} */
    this.onItemSent      = null;
    /** @type {((address: string, item: object, evt: object) => void) | null} */
    this.onItemConfirmed = null;
    /** @type {((stats: {batchId, total, confirmed}) => void) | null} */
    this.onBatchComplete = null;
  }

  /**
   * Begin dispatching the batch.
   *
   * The **first** item for every position is sent in a single
   * `batchStart` call so all lights turn on simultaneously.
   * Subsequent items (after each confirm) are sent individually via `lightOn`.
   */
  start() {
    if (this._total === 0 || Object.keys(this._queues).length === 0) {
      this._finish();
      return;
    }
    this._prevHandler = this._client._onConfirm;
    this._client._onConfirm = (evt) => this._handleConfirm(evt);

    // Pop the first item from each position and collect into one batchStart call
    const initial = [];
    for (const addr of Object.keys(this._queues)) {
      const queue = this._queues[addr];
      if (!queue || queue.length === 0) continue;
      const item = queue.shift();
      this._active[addr] = item;
      initial.push({
        address:  addr,
        quantity: item.quantity,
        color:    item.color   ?? LightColor.GREEN,
        label:    item.sku,
        ledMode:  item.ledMode ?? LightMode.ON,
        ...(item.buzMode !== undefined ? { buzMode: item.buzMode } : {}),
      });
      this.onItemSent?.(addr, item);
    }

    if (initial.length > 0) {
      this._client.batchStart(initial);
    }
  }

  /**
   * Wait until all items are confirmed (or timeout).
   * @param {number} [timeoutMs]
   * @returns {Promise<{batchId, total, confirmed, cancelled}>}
   */
  wait(timeoutMs) {
    if (!timeoutMs) return this._donePromise;
    return Promise.race([
      this._donePromise,
      new Promise((_, reject) =>
        setTimeout(() => reject(new Error(
          `Batch '${this._batchId}' did not complete within ${timeoutMs}ms ` +
          `(${this._confirmed}/${this._total} confirmed)`
        )), timeoutMs)
      ),
    ]);
  }

  /** Cancel: turn off active lights and stop dispatching. */
  cancel() {
    if (this._cancelled) return;
    this._cancelled = true;
    for (const [addr, item] of Object.entries(this._active)) {
      if (item !== null) {
        this._client.lightOff(addr);
        this._active[addr] = null;
      }
      if (this._queues[addr]) this._queues[addr].length = 0;
    }
    this._client._onConfirm = this._prevHandler;
    this._resolve({ batchId: this._batchId, total: this._total,
                    confirmed: this._confirmed, cancelled: true });
  }

  _dispatchNext(address) {
    if (this._cancelled) return;
    const queue = this._queues[address];
    if (!queue || queue.length === 0) {
      this._active[address] = null;
      const done = Object.keys(this._queues).every(
        a => (!this._queues[a] || this._queues[a].length === 0) && this._active[a] === null
      );
      if (done) this._finish();
      return;
    }
    const item = queue.shift();
    this._active[address] = item;
    this._client.lightOn(address, {
      quantity: item.quantity,
      color:    item.color   ?? LightColor.GREEN,
      ledMode:  item.ledMode ?? LightMode.ON,
      ...(item.buzMode !== undefined ? { buzMode: item.buzMode } : {}),
      label:    item.sku,
    });
    this.onItemSent?.(address, item);
  }

  _handleConfirm(evt) {
    if (!(evt.address in this._active)) {
      this._prevHandler?.(evt);
      return;
    }
    const item = this._active[evt.address];
    if (item === null) return;
    this._confirmed++;
    this.onItemConfirmed?.(evt.address, item, evt);
    this._dispatchNext(evt.address);
  }

  _finish() {
    this._client._onConfirm = this._prevHandler;
    const stats = { batchId: this._batchId, total: this._total,
                    confirmed: this._confirmed, cancelled: false };
    this.onBatchComplete?.(stats);
    this._resolve(stats);
  }
}

/**
 * Parsed batch from JSON input.
 * @example
 * const batch = BatchDispatch.fromDict(JSON.parse(rawJson));
 */
class BatchDispatch {
  constructor(batchId, items) {
    this.batchId = batchId;
    this.items   = items;
  }

  /**
   * @param {{batch_id: string, items: Array<{sku,label,quantity,color?,ledMode?}>}} d
   * @returns {BatchDispatch}
   */
  static fromDict(d) {
    return new BatchDispatch(
      d.batch_id,
      d.items.map(i => ({
        sku:      i.sku,
        label:    i.label,
        quantity: i.quantity,
        color:    i.color   ?? LightColor.GREEN,
        ledMode:  i.ledMode ?? LightMode.ON,
        buzMode:  i.buzMode,
      }))
    );
  }
}

module.exports = {
  P2LClient, LightColor, LightMode, CompletionStatus,
  WaveDispatcher, BatchDispatch, BatchDispatcher,
};

