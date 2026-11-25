# 서버-화면 계약 (CONTRACT) v0.3

변경 이력:
- v0.3 (2026-11-25) 관리자 헤더 `X-Admin-Key`, 주문 수량 서버 한도(400), 접속 직후 메시지 순서 추가 (REVIEW-5). 접속 직후 이력 재전송은 TASK-008에서 구현.
- v0.2 (2026-11-25) 거부 사유 목록, 실패 응답 형식, 없는 주문·실험의 404 추가 (REVIEW-2 P2-6).

서버(`src/Hoga.Server`, TASK-004)와 화면(`web/`, TASK-006)이 공유하는 메시지 형식이다. 화면은 서버가 생기기 전까지 이 계약대로 만든 목(mock) 데이터로 개발한다. 계약을 바꿔야 하면 구현에서 바꾸지 말고 메인 에이전트에게 제안한다.

## 공통
- JSON 필드는 camelCase.
- 가격·수량은 정수(원, 주). 화면에서만 천 단위 콤마로 표시한다.
- 시각은 `logicalTimeMs`(장 시작 기준 논리 ms). 화면 표시는 `09:00:00 + logicalTimeMs`로 계산한다(장 시작 09:00 가정).
- 종목은 1단계에 하나: `symbol = "HOGA"` (가상 종목, 임의 지정).

## 1. 실시간 (SignalR 허브 `/hubs/market`)
서버 → 화면 메시지.

### 접속 직후 (새 연결·재연결 모두)
1. 오늘의 1분봉 전부를 `candle`로, 최근 체결 최대 50건(임의 지정)을 오래된 것부터 `trade`로 다시 보낸다. (TASK-008에서 구현. 그 전까지는 생략됨)
2. 최신 `bookSnapshot` 1회.
- 화면은 새 연결이 열리면 기존 체결·봉 목록을 비우고 위 메시지로 다시 채운다. 연결 성공이 아니라 첫 `bookSnapshot` 수신을 "화면 복구 완료" 기준으로 삼는다.

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

### 거부 사유 (`reason`)
| 값 | 뜻 | ruleId |
|---|---|---|
| `InvalidQuantity` | 수량 0 이하 | null |
| `InvalidPrice` | 1원 미만 | R-LIMIT-05 |
| `InvalidOrderId` | 빈 주문 ID | null |
| `OffTick` | 호가단위 위반 | R-TICK-02 |
| `AboveUpperLimit` | 상한가 초과 | R-LIMIT-04 |
| `BelowLowerLimit` | 하한가 미만 | R-LIMIT-04 |
| `DuplicateOrderId` | 이미 쓴 주문 ID | null |
| `UnknownOrder` | 취소 대상 없음(이미 체결·취소 포함) | null |

### 실패 응답
- 요청 형식 오류: HTTP 400 `{ "error": "BadRequest", "message": "..." }`
  - 주문 수량이 서버 한도(`Market:MaxOrderQuantity`, 기본 1,000,000주, 임의 지정)를 넘어도 400이다. KRX 규칙이 아니라 서버 입력 한도이며 저널에 기록되지 않는다. 0 이하 수량은 엔진 거부(`InvalidQuantity`)로 200 + `accepted: false`.
- 관리자 API(`/api/admin/*`)는 요청 헤더 `X-Admin-Key`(이름 임의 지정)가 서버 설정 `Market:AdminKey`와 같아야 한다. 서버에 키가 설정되지 않았으면 항상 401이다.
- 없는 주문·실험: HTTP 404 `{ "error": "NotFound", "message": "..." }`
- 관리자 키 없음: HTTP 401 `{ "error": "Unauthorized", "message": "..." }`
- 알 수 없는 시나리오: HTTP 400 `{ "error": "UnknownScenario", "message": "..." }`
- 주문 거부는 에러가 아니라 HTTP 200 + `accepted: false`

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
