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
      protocol:    opts.tls ? 'mqtts' : 'mqtt',
      rejectUnauthorized: opts.tls?.rejectUnauthorized ?? true,
      ...opts.tls,
    };

    this._client = null;

    /** @type {Map<string, {resolve: Function, reject: Function, timer: NodeJS.Timeout}>} */
    this._confirmWaiters = new Map();
    /** @type {{resolve: Function, reject: Function, timer: NodeJS.Timeout} | null} */
    this._batchWaiter = null;

    // User callbacks
    this._onConfirm       = null;
    this._onBatchComplete = null;
    this._onStateChange   = null;
    this._onBridgeStatus  = null;
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

    } else if (kind === 'light_changed') {
      const state = {
        address:      data.address,
        moduleStatus: data.moduleStatus,
        color:        data.color,
        ledMode:      data.ledMode,
        displayData:  data.displayData,
        blockNo:      data.blockNo,
      };
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

  /** @param {(status: {online, gatewayConnected}) => void} fn */
  onBridgeStatus(fn)  { this._onBridgeStatus = fn; }

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
   * @param {Array<{address, quantity, color?, label?}>} items
   * @param {object} [opts]
   * @param {string} [opts.ledMode=LightMode.ON]
   * @param {string} [opts.buzMode]  - omit to mirror ledMode
   */
  batchStart(items, opts = {}) {
    const cmd = {
      type:    'batch_start',
      ledMode: opts.ledMode ?? LightMode.ON,
      items:   items.map(i => ({
        address:  i.address,
        quantity: i.quantity,
        color:    i.color ?? LightColor.GREEN,
        ...(i.label ? { label: i.label } : {}),
      })),
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

module.exports = { P2LClient, LightColor, LightMode, CompletionStatus };
