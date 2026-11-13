# PickSession polling API

WMS schedule creates and reserves PickSessions as before. Polling does not create sessions or change stock. It marks the returned light details On before responding. MQTT can continue delivering the same work; the native engine deduplicates by session and detail. The feature is disabled unless configured in authen.

## Setup

1. Deploy the gateway endpoint, WMS relight adjustment, authen backend/UI, and updated P2L binary.
2. Set gateway environment variable `P2L_PICK_SESSION_API_TOKEN` to a random token of at least 32 characters.
3. In authen license configuration, enable **Lấy phiên qua API**, enter the endpoint, same token, polling interval. Controller block numbers come from the existing licensed block list.
4. Restart P2L to apply remote configuration. Existing installed binaries do not contain this feature.
5. To operate independently of MQTT in both directions, also enable `confirmApi` with its existing token and endpoint. Otherwise confirmations remain in SQLite awaiting MQTT/WMS acknowledgement.

Example authen settings (values are placeholders):

```json
{
  "pickSessionApi": {
    "enabled": true,
    "url": "https://gateway.example/api/v1/p2l/pick-session/poll",
    "token": "<P2L_PICK_SESSION_API_TOKEN>",
    "pollIntervalMs": 2000
  }
}
```

## Protocol

`POST /api/v1/p2l/pick-session/poll`, JSON, `Authorization: Bearer <token>`.

```json
{"blockNos":["01","02"]}
```

Response:

```json
{
  "commands": [{
    "type": "batch_start",
    "blockNo": "01",
    "correlationId": "SESSION_CODE",
    "relight": false,
    "items": [{"address":"0008","quantity":2,"pickingDetailId":"DETAIL_ID","commandItemId":"detail:DETAIL_ID","color":"Green","ledMode":"On","buzMode":"Off","label":"BIN_CODE"}]
  }],
  "inactiveBlocks": []
}
```

Only active blocks and InProgress sessions belonging to the Controller blockNos are returned. No client/warehouse/site fields are required in config or requests. Completed/Confirmed details and exact detail IDs already stored as LightConfirm signals are excluded, even before inventory is processed. For a shared address only its first unconfirmed detail is returned. Ordinary polling returns only details whose LightStatus is not On. On details are returned only when IsRelightRequested is true; the first unconfirmed detail continues to hold its address until Confirm, preventing its successor from being returned early.

`IsRelightRequested` is atomically cleared while preparing the response and the returned command has `relight:true`. No separate ACK or request-version field is added. A lost response loses this manual request; the operator must request relight again. Before returning, polling updates each returned detail with LightStatus=On, payload LightColor/LightLedMode, LightDispatchStartedAt/LightDispatchAckAt/LightOnAt set to the current time, LightOffAt=null, UpdatedDate and UpdatedBy=P2L_API. This is the agreed delivery rule, without a separate device ACK; a lost response requires a manual relight request.

`inactiveBlocks` names explicitly disabled blocks. P2L terminates its active physical session on those blocks through the existing dispatcher. A failed API call or an empty work list does not terminate lights. HTTP and MQTT failure together prevent new instructions but preserve existing work and local Confirm receipts.

## Local validation

- `dotnet run --project tests/P2L.PickSessionApiTests/P2L.PickSessionApiTests.csproj`: local HTTP server and TCP Controller simulator; two independent blocks with MQTT offline, repeat snapshots, MQTT/API duplicates, relight/P5 fallback, local Confirm protection, Deactive and shutdown.
- Gateway: `node --test test/p2l/pickSessionPoll.test.js`.
- Authen: `node --test server/test/p2lLicense.test.js`.

These tests do not deploy services or operate real lights.
