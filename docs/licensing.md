# Pick2Light remote configuration

## Contract

The default application is now **one `p2l` process**: `p2l serve` hosts the embedded browser UI and the Bridge. Python/p2l-client is not required. Only the Bridge reads the device key. The .NET bridge requires online validation. There is no plaintext operational config cache and no fallback to legacy MQTT/gateway JSON or environment values. Every process startup/connect and MQTT reconnect validates the license. Both runtimes keep their startup/connect config stable during a running session; if config changes, automatic reconnect is blocked until a controlled reconnect/restart. No licensing code sends reset or termination commands to hardware.

`P2L_LICENSE_API_URL` is a deployment setting, for example `https://auth.example.com` (optional base path supported). Do not make it editable by warehouse users. HTTPS, normal certificate validation and no redirects are required. Configure this environment variable for **both** services, not only the shell/browser. No secrets in this value.

1. Admin creates a controller config and license at authen `/p2l-licenses`.
2. Run `p2l license activate` once in Terminal under the OS account that will run the Bridge. Enter the code at the masked prompt. No separate Client is needed.
3. Activation creates/reuses a 2048-bit RSA key in the OS vault **before** the API request. A lost response can be retried with the same key. License codes are not retained.
4. Device ID is lowercase SHA256 of SPKI DER. Before activation, request `/challenge` with `{deviceId, role: "activation"}`. Activate signs UTF-8 `p2l.activate.v2\n<licenseHash>\n<deviceId>\n<challenge>`; licenseHash is lowercase SHA256 hex of the trimmed, uppercased license. Activation consumes the challenge too.
5. Config fetch requests a 60-second one-use challenge and signs UTF-8 `p2l.config.v1\n<deviceId>\n<client|bridge>\n<challenge>` using RSA PKCS#1 v1.5 / SHA256; signatures are standard Base64.
6. Run `p2l serve`. It starts the local UI and Bridge, and opens `http://127.0.0.1:8765` in an interactive terminal. No second Client process or Keychain reader exists. MQTT credentials remain in Bridge memory; the browser receives safe status/config fields only.

Endpoints: `POST /api/p2l/v1/activate`, `/challenge`, `/config`. The response envelope is `{Status:true, Data:{...}}`. Config has `version`, `siteCode`, `gateway:{host,port,blockNo}`, `mqtt:{host,port,clientId,username,password,commandTopic,eventTopic,devicesTopic,statusTopic,tls:true}`. All responses have `Cache-Control: no-store`.

## Activate directly from the Bridge CLI

No Python Client or browser is required for a Bridge-only setup:

```bash
export P2L_LICENSE_API_URL=https://auth-supra.oviots.com
./p2l license activate
# Enter license at the masked prompt; no license argument is accepted.
./p2l license check
./p2l serve
```

`activate` saves the key in the same OS vault used by the Client, calls activation v2 and validates the Bridge config. If activation succeeds but fetching config fails, use `license check` to retry validation. Existing keys are reused; unreadable/corrupt keys are not silently replaced. `activate` and `check` run before hardware service registration and never connect to MQTT or the controller. They do not start `serve` automatically. `check` is optional. Keep the license out of shell arguments, history and logs. On this macOS build, activation uses Keychain under the account running Terminal.

## Integrated web UI

HTML/CSS/JS are embedded in the Bridge assembly. No Python, PyInstaller, external frontend folder or second MQTT login is required by `p2l serve`.

- `p2l serve`: run Bridge + local UI and open the browser when interactive.
- `p2l serve --no-browser`: serve the UI without opening a browser (service-friendly).
- `p2l serve --ui-port 8766`: choose another loopback port.
- `p2l serve --no-ui`: preserve headless Bridge operation without creating the UI catalog.

Stop with Ctrl+C. Stop the old standalone Client before using port 8765. A UI bind conflict fails before starting hardware connections. Closing the browser does not stop the Bridge or reset lights. The disconnect button disconnects the UI's command session; it does not terminate active device operations or the broker session.

The embedded API converts the existing UI requests into the same command payloads and calls the MQTT command dispatcher's existing gate/queue. External MQTT commands use that same dispatcher. This avoids requiring the Bridge account to publish to its subscribed command topic or fetching Client-role credentials. State, confirmations and batch completion come from the same registry/engine; MQTT event publishing is unchanged.

Device labels/order use the existing SQLite `devices` table. Unless Mqtt:ClientDbPath is explicitly configured, the default is `<LocalApplicationData>/P2L/p2l_devices.db` so regular users can write it. Old standalone Client databases are not automatically copied; point Mqtt:ClientDbPath to an existing database to retain its labels/order. Deleting a device in the UI removes its catalog entry, not the engine's active hardware context.

The server uses Kestrel bound to IPv4 loopback, strict local Host/Origin checks for HTTP and WebSocket, JSON-only mutations and a bounded request body. It exposes no runtime config edits, process start/stop/restart operations, activation form or MQTT secrets. Legacy Python Client source is retained separately; its old config/service controls are hidden in the integrated UI.

## Service account and credential vault

The CLI/Bridge uses this native credential (the optional legacy Python Client is compatible): Windows generic target `P2L.License/device`; macOS generic service `P2L.License`, account `device`; Linux Secret Service attributes `application=P2L.License`, `account=device` (`secret-tool` must be installed and a session keyring available). The value is Base64 PKCS#8 DER, encoded as ASCII/UTF-8.

Run CLI enrollment, UI backend (if used) and bridge under the **same OS account** with access to the same vault. A Windows service under LocalSystem cannot read a key enrolled by an interactive user. To use LocalSystem for both, perform enrollment through the running UI service. On macOS the keychain must be unlocked and grant the packaged bridge access. Headless Linux requires a provisioned Secret Service session. Failure to access a vault is an activation error; no plaintext-key fallback exists. Vault access for final packaged binaries and Windows Service identities requires platform UAT.

The integrated and legacy local web UIs bind to loopback; LAN binding overrides are removed. Middleware rejects foreign Origin and Host headers for HTTP and WebSocket. No additional browser credentials are persisted. Service executable path/arguments and working directory remain local in `config/p2l_local_settings.json`; this contains no MQTT settings. The old client and bridge config-editing APIs cannot write operational credentials.

## Authen deployment

Set `P2L_ENCRYPTION_KEY` to a dedicated random 32-byte key represented as 64 hex characters, provided by deployment secret management. Preserve/back up the key separately from MongoDB. Rotating this key requires re-encrypting existing config passwords; do not simply replace it. This is not APP_SECRET or the PII key.

Use HTTPS at ingress, disable request/response body capture for device endpoints, configure request body limits, and trust only your known reverse proxy when forwarding client IPs. Challenge and rate-limit keys use the existing Redis configured by `CACHE_REDIS` (same encoded connection configuration as authen), with a dedicated `P2L:v1:` prefix and TTL. Redis failure returns 503; there is no memory or Mongo fallback. Rate limits use shared Redis minute buckets: activation 10/minute/IP, challenge/config 120/minute/IP; challenge creation also limits each device/role to 30/minute. Redis `EVAL` must be allowed for atomic compare-and-delete and increment-with-expiry. The client supports host, port, db, username, password and boolean tls fields from CACHE_REDIS. With no trusted-proxy configuration all clients behind one proxy share its bucket. Never trust arbitrary X-Forwarded-For.

One Mongo collection: **P2LConfigurations**, registered as model P2LConfig. Each document holds Name/SiteCode, Version, Settings (gateway, MQTT and encrypted credentials per role), CodeHash/CodeHint, Status/ExpiresAt, DeviceId/PublicKey/DeviceName/LastSeenAt and BindingVersion. Draft records have no license yet. Issuing a license updates the same document atomically; the raw code is returned once and never stored. The admin screen is one list with edit, issue, lock/unlock, renew and unbind actions.

Confirm unique partial indexes on non-empty CodeHash and DeviceId before exposing endpoints. Mongo stores no challenges or counters. The earlier three-collection prototype was not deployed by this task; this source change does not migrate or delete any existing database records. If a separate environment already used that prototype, reconcile its data before switching binaries.

Provision distinct broker accounts for client and bridge of each controller and enforce topic ACL at the broker. This module stores configured credentials; it does **not** create broker users or ACLs automatically.

Device endpoints accept POST JSON with an allowlist of fields and reject browser Origin headers. This is an additional surface restriction, not an authentication substitute. The server still checks the machine's RSA signature, the one-use 60-second nonce bound to its role, the document binding version and the current active/unexpired license. No APP_SECRET, static shared desktop API key or MAC/IP-based identity is used. Changes to config, license state or device binding invalidate outstanding config challenges. HTTPS must be enforced at ingress and direct backend access restricted.

Deploy authen first, create config/license and verify test activation, then roll out licensed clients/bridge during a controlled maintenance window. Legacy JSON is ignored; remove old secret-bearing files from installed machines/backups according to your process and rotate old broker credentials. Source cleanup does not erase Git history or old packages.

## Offline event buffer path

The default `mqtt_offline_buffer.jsonl` is stored next to the **p2l executable** (`AppContext.BaseDirectory`), independent of the shell's working directory. The installation directory must be writable by the running OS account. An explicit `Mqtt:OfflineBufferPath` / `P2L_MQTT__OFFLINEBUFFERPATH` still overrides the default; parent directories are created before append. Existing buffer entries are replayed unchanged; changing the default does not recover events that previously failed to write.

## Revocation and operational limits

A lock/expiry/unbind blocks future config fetches and reconnects. It does not disconnect an already connected broker session. Immediate revocation needs broker-side revocation/disconnect. Unbind invalidates outstanding challenges and permits reactivation; the old device key remains in its OS vault but no longer has server authorization. Reinstalling the OS requires administrative unbind/re-enrollment.

An administrator on the workstation can inspect memory or use its key. The design removes plaintext config files and narrows credential scope; it does not hide runtime credentials from the OS administrator. It does not add restart-session persistence to the picking engine; that rollback remains in effect.

## Optional MQTT CA Certificate

In Authen's P2L configuration editor, paste a public CA PEM certificate or upload a PEM-encoded `.pem`, `.crt` or `.cer` file (maximum 32 KB). It is stored in the existing `P2LConfigurations.Settings.mqtt.tls` field. No additional collection is created.

When a CA is configured, the device API returns `mqtt.tls: {ca: "...PEM...", rejectUnauthorized: true}`, matching the TLS options used by Blocks. With no CA, the API keeps the existing `mqtt.tls: true` response and P2L uses the OS trust store. MQTT username/password remain required and unchanged in both cases. Client certificates and private keys are not part of this feature.

P2L loads the CA directly into memory. It continues to check the server's hostname, validity and trust chain. To remove the custom CA, clear the editor field and save (API: `mqtt.tls: {ca: ""}`). API updates that omit `mqtt.tls` preserve an existing CA. Restart P2L at a safe time after saving to apply the new configuration; reconnect detects configuration changes and requires a restart.

Deploy the Authen changes and run the new P2L binary to use custom CAs. Existing P2L binaries support the unchanged no-CA response only. This feature does not write or synchronize `P2L.Blocks`.

## Optional Axiom export

Authen's P2L config editor includes an Axiom section. Enable it and enter the dataset, an ingest-only API token and its endpoint: `https://us-east-1.aws.edge.axiom.co`, `https://eu-central-1.aws.edge.axiom.co`, or the legacy `https://api.axiom.co`. Use the edge where the dataset lives. The edge ingest path is `/v1/ingest/{dataset}`; legacy uses `/v1/datasets/{dataset}/ingest` (https://axiom.co/docs/restapi/ingest).

Example addition to the admin config save payload:

```json
{
  "axiom": {
    "enabled": true,
    "endpoint": "https://us-east-1.aws.edge.axiom.co",
    "dataset": "p2l-logs",
    "token": "<ingest-only API token>"
  }
}
```

The token is encrypted with the existing `P2L_ENCRYPTION_KEY` and stored in the same Mongo configuration document. Admin list/save responses return `hasToken`, never the token or ciphertext. Only the signed Bridge config API returns the decrypted token. Blank token on edit preserves it; omitting `axiom` preserves the previous settings. Set `axiom.enabled: false` to disable export. Restart P2L at a safe time after changing the settings. No token or telemetry spool is written to local JSON.

The integrated .NET P2L exports three record kinds:

- `service_log`: Trace through Critical service logs, including hardware TX/RX logs emitted by the service. Bootstrap failures before licensed config loads cannot be exported.
- `command_received`: every payload entering the shared MQTT/local UI dispatcher, before validation. Receipt does not mean the command completed.
- `event`: every generated MQTT event, including device state/snapshots, confirmations, batch completion, CSV lifecycle and Bridge status. Captured before MQTT delivery, including while the broker is offline. Broker-generated Last Will messages after a process crash cannot be captured by the stopped process. Replayed MQTT buffers are not emitted again to Axiom.

Records contain `_time`, `recordId`, `siteCode`, `clientId`, machine and block. `type`, `correlationId` and `address` are promoted when present for PickSession filtering. Token/password/license/private-key fields and known runtime secrets are redacted. Invalid JSON is represented by metadata instead of raw contents. Large records are truncated and marked; no event sampling is applied.

Export uses a separate bounded queue (1,000 records), batches up to 50 and retries transient HTTP/network errors up to three attempts. Redirects are disabled. P2L never waits for Axiom on a picking thread. This is best-effort diagnostics, not a durable audit ledger: a full queue, prolonged outage, process crash or failed shutdown flush can lose records. Warnings go to local stderr without credentials or response bodies. Shutdown permits up to five seconds to flush. The existing MQTT event buffer is independent of Axiom.

Verification: `dotnet run --project tests/P2L.AxiomTests -p:UseSharedCompilation=false` uses fake HTTP only. Web tests exercise command/event hooks with local fixtures. Actual dataset ingestion requires deployed Authen settings and a user-provided Axiom API token; it has not been verified by local tests.

## Stopping P2L

`Ctrl+C` (SIGINT) and SIGTERM cancel `serve`, stop the local UI, MQTT reconnect loop, heartbeat and gateway transport, and close sockets. Intentional stops disable self-heal before cleanup, so they do not spawn a replacement process. TCP loop cancellation is normal shutdown and disposal can safely be repeated. This does not send a reset or terminate-light command to the controller.

A few shutdown lines and up to five seconds of Axiom flush may occur before the shell prompt returns. The service should not keep reconnecting or writing heartbeat logs afterwards. Use the latest binary; an orphan from an older version must be stopped separately.

Regression check: `dotnet run --project tests/P2L.ShutdownTests -p:PublishAot=false -p:SelfContained=false -p:UseSharedCompilation=false`. It signals child processes with UI WebSockets open, controller connected/offline and MQTT awaiting CONNACK, using loopback fixtures only.

## One Controller, multiple picking blocks

Run one `p2l serve` instance per Controller. In Authen, keep one license/configuration and set the gateway default plus its allowed blocks:

```json
{
  "gateway": {
    "host": "192.168.1.254",
    "port": 5003,
    "blockNo": "01",
    "blockNos": ["01", "02"]
  }
}
```

The admin editor accepts `01,02` in "Các block được phép". Each entry is two digits, the default must be included, and `00` cannot coexist with other blocks. Older API responses without `blockNos` remain readable; save/deploy the new Authen settings to enforce an allowlist. Changes apply after a safe restart.

Configure the two Rocket `P2L.Blocks` records to publish to the same Controller's MQTT prefix, using distinct block numbers, PickSession codes and MQTT client IDs. Never copy the same MQTT clientId into both Rocket blocks; it would cause broker disconnects. No direct Mongo updates or Rocket deployment are performed by this change. Rocket already sends `blockNo` and `correlationId`; keep those fields on every session command. For example:

```json
{"type":"batch_start","blockNo":"01","correlationId":"PICK-A","items":[{"address":"0001","quantity":2}]}
{"type":"batch_start","blockNo":"02","correlationId":"PICK-B","items":[{"address":"0021","quantity":3}]}
```

Each block admits one active session. An identical `batch_start` retry is ignored. A different session cannot overwrite an active block. `light_on` may append a light to the same session/block and only rebuilds that block's P1/P5 instruction. A session cannot span blocks; active session address ownership is Controller-wide because hardware confirmation packets contain an address, not a session/block ID. Physically assign unique addresses across the two rows.

`confirmed` and `batch_completed` MQTT events now include both `blockNo` and `correlationId`. Legacy `BatchCompleted` callbacks remain available for CSV. The integrated UI exposes allowed blocks, filters batch status and completion by the selected block, and finishes only that block. Axiom event metadata uses the payload's block number.

Scoped stop: `{"type":"batch_finish","blockNo":"01","correlationId":"PICK-A"}` stops only row A; a mismatching session is rejected. Omitting the session on a scoped stop is an explicit operator cancellation of that block. Use `{"type":"terminate_all"}` for an explicit global stop; it sends D to each configured/known block. Hardware `D00` itself targets block 00; it is not a wildcard for blocks 01/02. The engine expands its legacy global-stop operation accordingly.

If a hardware write fails after dispatch may have started, keep that block reserved as uncertain until confirmation or explicit reconciliation/stop. Other blocks remain usable. Restored Operating lights are not silently adopted by a new session. This is not durable PickSession recovery: runtime batch tracking remains in memory, and a delayed hardware confirmation after address reassignment cannot identify its old session. Keep WMS reconciliation/quarantine rules for uncertain work.

Validation: `dotnet run --project tests/P2L.MultiBlockTests -p:UseSharedCompilation=false` uses one real loopback TCP connection and a synthetic Controller. It tests JSON dispatch, P1/P5 block separation, interleaved confirmations, scoped cancellation, global stop, conflicts, uncertain dispatch and CSV cleanup. Authen tests cover saving/preserving the list and returning it through the signed device API. Real Controller/broker/UAT validation is still required.

## Verification

Run Python/.NET commands in the `light` repo and Node/frontend commands in `wms-authen`.

- Python: `.venv/bin/python -m unittest discover -s tests -p 'test_licensing.py' -v`.
- Authen: `node --test server/test/p2lLicense.test.js server/test/p2lCache.test.js`, then the existing suite. The cache tests start an isolated redis-server on a private /tmp Unix socket; they do not use CACHE_REDIS or live data. redis-server must be on PATH (or set P2L_TEST_REDIS_BINARY).
- Integrated UI tests (local HTTP/WebSocket/SQLite and fake command sink, no UAT/vault/controller): `dotnet run --project tests/P2L.WebTests -p:PublishAot=false -p:SelfContained=false`.
- CLI protocol tests (no real API/vault): `dotnet run --project tests/P2L.LicensingTests`.
- .NET: build `src/P2L.CLI/P2L.CLI.csproj` for the intended RID. Native AOT publish and target-platform vault access are separate release checks.
- Test invalid/expired/locked licenses, second-machine activation, replay/concurrent challenge use, unbind/rebind, API unavailability and offline restart before hardware UAT.

No production service, broker configuration or hardware is changed by the local unit tests.
