# 서버-화면 계약 (CONTRACT) v0.1

서버(`src/Hoga.Server`, TASK-004)와 화면(`web/`, TASK-006)이 공유하는 메시지 형식이다. 화면은 서버가 생기기 전까지 이 계약대로 만든 목(mock) 데이터로 개발한다. 계약을 바꿔야 하면 구현에서 바꾸지 말고 메인 에이전트에게 제안한다.

## 공통
- JSON 필드는 camelCase.
- 가격·수량은 정수(원, 주). 화면에서만 천 단위 콤마로 표시한다.
- 시각은 `logicalTimeMs`(장 시작 기준 논리 ms). 화면 표시는 `09:00:00 + logicalTimeMs`로 계산한다(장 시작 09:00 가정).
- 종목은 1단계에 하나: `symbol = "HOGA"` (가상 종목, 임의 지정).

## 1. 실시간 (SignalR 허브 `/hubs/market`)
서버 → 화면 메시지.

### `bookSnapshot` (변경 시마다, 최대 초당 10회로 묶어서)
```json
{
  "symbol": "HOGA",
  "seq": 1024,
  "logicalTimeMs": 3600000,
  "phase": "Continuous",
  "basePrice": 9940,
  "upperLimit": 12920,
  "lowerLimit": 6960,
  "lastPrice": 10050,
  "asks": [ { "price": 10060, "quantity": 1200, "orderCount": 3 } ],
  "bids": [ { "price": 10050, "quantity": 800, "orderCount": 2 } ],
  "vi": null
}
```
- `phase`: `"Continuous"`(접속매매) | `"CallAuction"`(단일가매매)
- `asks`는 낮은 가격부터, `bids`는 높은 가격부터. 각 최대 10단계.
- 단일가매매 중에는 `expectedPrice`, `expectedQuantity`(예상 체결가·수량) 필드가 추가된다. 없으면 null.
- `vi`: VI 진행 중이면 아래 객체, 아니면 null
```json
{ "kind": "Dynamic", "referencePrice": 10000, "triggerPrice": 10600, "startMs": 3600000, "scheduledEndMs": 3720000, "ruleId": "R-VI-01" }
```
  - `kind`: `"Dynamic"` | `"Static"`
  - `scheduledEndMs`는 예정 마감(2분). 실제 종료는 30초 이내 임의 연장이 있어 화면은 "약 n초 남음(최대 30초 연장)"으로 표시한다.

### `trade`
```json
{ "symbol": "HOGA", "seq": 1025, "logicalTimeMs": 3600500, "price": 10050, "quantity": 300, "aggressorSide": "Buy" }
```
- 단일가 체결은 `aggressorSide: null`.

### `candle` (1분봉, 분이 바뀌거나 갱신될 때)
```json
{ "symbol": "HOGA", "startMs": 3600000, "open": 10000, "high": 10100, "low": 9990, "close": 10050, "volume": 15400 }
```

### `viEvent` (발동·해제 시)
```json
{ "type": "Triggered", "kind": "Dynamic", "referencePrice": 10000, "triggerPrice": 10600, "atMs": 3600000, "ruleId": "R-VI-01" }
```
- `type`: `"Triggered"` | `"Released"`. 해제 시 `auctionPrice`(단일가 체결가) 추가.

## 2. REST
| 메서드·경로 | 요청 | 응답 |
|---|---|---|
| `POST /api/orders` | `{ "side": "Buy", "price": 10050, "quantity": 100 }` | `{ "orderId": "o-123", "accepted": true, "reason": null, "ruleId": null }` |
| `DELETE /api/orders/{orderId}` | - | `{ "orderId": "o-123", "cancelled": true, "reason": null }` |
| `GET /api/orders/{orderId}/explanation` | - | 아래 설명 객체 |
| `GET /api/experiments/{id}` | - | 아래 비교 실험 객체 |
| `POST /api/admin/scenario` | `{ "name": "FatFinger" }` (`"FatFinger"` \| `"PanicSell"` \| `"Calm"`) | `{ "ok": true }` |
| `POST /api/admin/crash` | - | `{ "ok": true }` (서버가 재시작을 흉내 낸다) |
| `GET /api/state/hash` | - | `{ "seq": 1024, "stateHash": "ab12...", "restoredFromJournal": true }` |

거부 응답 예: `{ "orderId": null, "accepted": false, "reason": "AboveUpperLimit", "ruleId": "R-LIMIT-04" }`

### 설명 객체 (`explanation`)
```json
{
  "orderId": "o-123",
  "status": "Resting",
  "side": "Buy",
  "price": 10050,
  "remainingQuantity": 100,
  "queueAhead": { "orders": 7, "quantity": 3200 },
  "reason": null,
  "ruleId": null,
  "message": "같은 가격 대기열 8번째, 앞에 3,200주가 있습니다."
}
```
- `status`: `"Resting"` | `"WaitingAuction"` | `"Filled"` | `"PartiallyFilled"` | `"Rejected"` | `"Cancelled"`
- `message`는 서버가 만든 한국어 문장. 화면은 그대로 보여 주고, `ruleId`가 있으면 규칙 배지를 붙인다.

### 비교 실험 객체 (`experiment`)
```json
{
  "id": "exp-1",
  "seed": 42,
  "scenario": "FatFinger",
  "runs": [
    { "viEnabled": true,  "series": [ { "timeMs": 0, "price": 10000 } ], "viEvents": [ { "type": "Triggered", "kind": "Dynamic", "atMs": 61000, "referencePrice": 10000, "triggerPrice": 9400, "ruleId": "R-VI-01" } ], "minPrice": 9400, "maxPrice": 10100 },
    { "viEnabled": false, "series": [ { "timeMs": 0, "price": 10000 } ], "viEvents": [], "minPrice": 7200, "maxPrice": 10100 }
  ]
}
```
- 두 run은 같은 시드·같은 시나리오이고 VI 여부만 다르다.
