# API transport and ACK configuration

Configure these fields in the P2L license on authen. Restart P2L after saving.

```json
{
  "mqtt": { "enabled": false },
  "pickSessionApi": { "enabled": true },
  "confirmApi": { "enabled": true },
  "ackApi": {
    "enabled": false,
    "url": "https://gateway.example/p2l/ack",
    "token": "REPLACE_WITH_A_TOKEN_OF_AT_LEAST_32_CHARACTERS"
  }
}
```

This is a partial configuration: retain existing URLs/tokens for PickSession API
and Confirm API, and existing broker settings for switching MQTT back on.
Missing `mqtt.enabled` preserves the previous behaviour (MQTT enabled).
Disabling MQTT requires both APIs enabled.

With MQTT disabled, P2L keeps polling work and delivering durable Confirm receipts
through HTTP. It does not connect/reconnect to the broker, publish MQTT messages,
or append/replay the MQTT offline buffer. Axiom logging stays independent.
The embedded web client remains usable without a broker.

`confirmApi.enabled=true` sends durable WMS Confirm receipts through HTTP instead
of MQTT. SQLite deletes only event IDs accepted by the Confirm API.

ACK is preparation only: authen stores the encrypted token and returns it to the
bridge; P2L reads `ackApi` and provides an explicit-use HTTP client. No production
flow calls that client yet, even if `ackApi.enabled=true`. There is no new gateway
endpoint, automatic ACK, polling, or retry queue for ACK in this change. No ACK
payload contract or light/session state transitions are introduced.

## One-file macOS build

```sh
dotnet publish src/P2L.CLI/P2L.CLI.csproj -p:PublishProfile=osx-arm64-singlefile
```

Distribute only `artifacts/osx-arm64-singlefile/p2l`. The executable includes the
.NET runtime, SQLite native libraries, and embedded web client; no adjacent DLL,
client directory, or JSON config is required. Native libraries extract to the
runtime cache automatically. Logs default to `logs` beside the executable; license
and SQLite data keep using the OS credential vault and application data directory.
The default license server is `https://auth-supra.winmart.vn`. Set
`P2L_LICENSE_API_URL` only to override it. macOS may
require approval for a new executable to access an existing Keychain credential.
