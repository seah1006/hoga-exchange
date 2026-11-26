# 서버-화면 계약 (CONTRACT) v0.5

변경 이력:
- v0.5 통합 정정 (2026-11-26) 상태 해시에 `ledgerHash` 추가, 계좌 예시 및 주문 상태·관리자 인증 응답 명시.
- v0.5.1 (2026-11-26, dev/service) 로그인 실패 응답 통일(`LockedOut` 제거), 로그인·탈퇴 시도 제한 429 (REVIEW-8).
- v0.5 (2026-11-25, dev/service) 회원·세션·CSRF, 주문 로그인 필수와 소유권, 잔고 거부 사유, 계좌·내 주문·내 체결·랭킹 API, `accountUpdate` 메시지 (ADR 0005).
- v0.4 (2026-11-25) 실험 요약 `GET /api/experiments/summary`, run의 `minAtMs`·`maxAtMs`, 시장 초기화 `POST /api/admin/reset`과 `marketReset` 메시지, `GET /healthz` 추가 (TASK-009, TASK-007). 모두 추가만 하고 기존 필드는 바꾸지 않는다.
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
1. 오늘의 1분봉 전부를 `candle`로, 최근 체결 최대 50건(임의 지정)을 오래된 것부터 `trade`로 다시 보낸다. (TASK-008에서 구현)
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
| `GET /api/state/hash` | - | `{ "seq": 1024, "stateHash": "ab12...", "ledgerHash": "cd34...", "restoredFromJournal": true }` |
| `GET /api/experiments/summary` (v0.4) | - | 아래 실험 요약 객체 |
| `POST /api/admin/reset` (v0.4) | - | `{ "ok": true }` 저널을 보관하고 빈 시장으로 다시 시작. 관리자 세션(+CSRF) 또는 관리자 키 필요 |
| `GET /healthz` (v0.4) | - | `{ "ok": true, "seq": 1024 }` 처리 루프가 살아 있으면 200, 아니면 503 |

거부 응답 예: `{ "orderId": null, "accepted": false, "reason": "AboveUpperLimit", "ruleId": "R-LIMIT-04" }`

- orderId 형식은 불투명, 장(초기화) 간 유일.
- `ledgerHash`는 저널 재생으로 복원되는 가상 계좌 원장의 해시다. `stateHash`는 시장 엔진의 해시다.

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
- 관리자 API(`/api/admin/*`)는 `Admin` 역할 세션과 CSRF 토큰 또는 서버 설정 `Market:AdminKey`와 일치하는 `X-Admin-Key` 헤더를 요구한다.
- 없는 주문·실험: HTTP 404 `{ "error": "NotFound", "message": "..." }`
- 유효한 관리자 키가 없고 비로그인: HTTP 401 `{ "error": "Unauthorized" }`. 관리자 역할 없는 로그인 사용자: 같은 코드로 HTTP 403.
- 알 수 없는 시나리오: HTTP 400 `{ "error": "UnknownScenario", "message": "..." }`
- IP 요청 제한 초과: HTTP 429 `{ "error": "TooManyRequests" }` (주문·취소 합산 20건/초, 관리자 API 합산 10건/분, 기본값 임의 지정).
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
- (v0.4) 각 run에 `minAtMs`, `maxAtMs`(최저·최고 체결이 처음 나온 논리 시각) 추가. `series`는 1초마다의 마지막 체결가라 순간 최저가가 선에 안 보일 수 있다. 화면은 이 두 점을 별도 표식으로 그리고 "선: 1초 종가"를 표기한다.
- 실험 ID (v0.3): `exp-{scenario}-{seed}` (예: `exp-FatFinger-16`, 시드 1~1000, 임의 지정). 별칭 `demo`는 `docs/experiments/SUMMARY.md`의 시연 시드 선정 규칙으로 정한 FatFinger 실험이며 응답의 `id`는 실제 ID(`exp-FatFinger-16`)다. 형식이 틀리거나 범위 밖이면 404. 화면의 비교 화면은 `demo`를 조회한다.

### 실험 요약 객체 (v0.4, `GET /api/experiments/summary`)
`Hoga.Experiments --batch`가 `docs/experiments/summary.json`으로 미리 만들고 서버는 그 파일을 그대로 돌려준다(서버에서 600 run을 다시 계산하지 않는다).
```json
{
  "generatedBy": "dotnet run --project src/Hoga.Experiments -- --batch --out docs/experiments",
  "seeds": { "from": 1, "to": 100 },
  "durationMs": 240000,
  "demo": { "scenario": "FatFinger", "seed": 16, "rule": "FatFinger Δ가 중앙값에 가장 가까운 시드, 동률이면 작은 시드" },
  "scenarios": [
    {
      "scenario": "FatFinger",
      "deltaQuantiles": { "min": 1500, "p25": 2270, "median": 2465, "p75": 2650, "max": 2990 },
      "counts": { "viWorse": 0, "tie": 0, "viBetter": 100 },
      "runsEndingInVi": { "viOn": 6, "viOff": 0 },
      "seeds": [ { "seed": 1, "minOn": 9500, "minOff": 7020, "delta": 2480, "viTriggers": 1 } ]
    }
  ],
  "caveat": "임의 지정한 봇·호가 깊이·충격 크기·관측 구간에 한정된 결과다."
}
```
- `delta = minOn - minOff`(원). 양수면 VI 켬 쪽 최저가가 높았다(덜 떨어졌다). 중앙값은 0.5원 단위가 나올 수 있어 소수 한 자리까지 허용한다.

### `marketReset` (v0.4, SignalR)
```json
{ "symbol": "HOGA", "atUtc": "2026-11-26T20:00:00Z", "reason": "Daily" }
```
- `reason`: `"Daily"`(정기 초기화) | `"Admin"`(관리자). 화면은 체결·봉·내 주문 목록을 비우고 이어서 오는 `bookSnapshot`으로 다시 그린다.

## 3. 회원·계좌 (v0.5, ADR 0005)

### 공통
- 세션은 쿠키. 운영 관리자 키로 승인한 요청을 제외하고 상태를 바꾸는 요청(POST/DELETE, 주문 포함)은 `GET /api/auth/csrf`로 받은 토큰을 `X-CSRF-TOKEN` 헤더에 넣는다. 없거나 틀리면 400 `{ "error": "BadCsrfToken" }`.
- 로그인이 필요한 API를 로그인 없이 부르면 401 `{ "error": "Unauthorized" }`. 남의 주문은 404.
- 사용자 객체: `{ "id": "u-…", "nickname": "세아", "isAdmin": false }` (이메일은 `me`에서만 반환)

### 인증
| 메서드·경로 | 요청 | 응답 |
|---|---|---|
| `GET /api/auth/csrf` | - | `{ "token": "…" }` |
| `POST /api/auth/register` | `{ "email", "password", "nickname" }` | 201 사용자 객체(로그인 상태가 됨). 400 `InvalidEmail`·`WeakPassword`(10자 이상, 임의 지정)·`InvalidNickname`(2~16자), 409 `EmailTaken`·`NicknameTaken` |
| `POST /api/auth/login` | `{ "email", "password" }` | 200 사용자 객체. 401 `InvalidCredentials`(없는 이메일·틀린 비밀번호·잠긴 계정 모두 같은 응답과 비슷한 처리 시간, REVIEW-8), 429 `TooManyRequests`(IP·이메일별 시도 제한, 임의 지정) |
| `POST /api/auth/logout` | - | 204 |
| `GET /api/auth/me` | - | 200 `{ "id", "nickname", "email", "isAdmin" }` / 401 |
| `DELETE /api/auth/me` | `{ "password" }` | 204 탈퇴(비밀번호 확인 시도 제한, 초과 시 429)(현재 라운드 주문 취소 후 계정 삭제, 라운드 기록의 닉네임은 "탈퇴한 사용자") |

### 주문 (변경)
- `POST /api/orders`, `DELETE /api/orders/{id}`, `GET /api/orders/{id}/explanation`은 로그인 필수. 주문의 소유자만 취소·조회.
- 거부 사유 추가(HTTP 200 + `accepted:false`, `ruleId: null`, KRX 규칙 아님): `InsufficientCash`(매수 가능 현금 부족), `InsufficientShares`(매도 가능 수량 부족).

### 계좌
| 메서드·경로 | 응답 |
|---|---|
| `GET /api/account` | 아래 계좌 객체 |
| `GET /api/account/orders` | 이번 라운드 내 주문 `[{ "orderId", "side", "price", "quantity", "filledQuantity", "remainingQuantity", "status", "createdAtMs" }]` (최신순, 최대 200) |
| `GET /api/account/trades` | 이번 라운드 내 체결 `[{ "orderId", "side", "price", "quantity", "logicalTimeMs" }]` (최신순, 최대 200) |

- 내 주문의 `status`: `Resting`, `WaitingAuction`, `PartiallyFilled`, `Filled`, `Cancelled`, `Rejected`. 미체결 잔량이 있는 주문은 단일가매매 중 `WaitingAuction`으로 표시한다.

```json
{
  "round": "2026-11-26",
  "cash": 9000000, "cashOnHold": 500000,
  "shares": 1100, "sharesOnHold": 0,
  "initialValuation": 20000000,
  "lastPrice": 10050, "valuation": 20555000,
  "pnl": 555000, "returnBp": 277
}
```
- `valuation = cash + cashOnHold + (shares + sharesOnHold) × lastPrice`. `pnl = valuation − initialValuation`. `returnBp`는 `pnl × 10,000 ÷ initialValuation`의 소수부를 버린 정수(277 = 2.77%).

### 랭킹
| 메서드·경로 | 응답 |
|---|---|
| `GET /api/ranking` | 현재 라운드 `{ "round", "updatedAtUtc", "entries": [{ "rank", "nickname", "returnBp", "valuation" }], "me": { "rank", "returnBp", "valuation" } \| null }` 상위 50 |
| `GET /api/ranking/{round}` | 지난 라운드(`YYYY-MM-DD`) 같은 형식, 없으면 404 |
| `GET /api/ranking/rounds` | `["2026-11-26", …]` 최신순 최대 30 |
- 랭킹에는 그 라운드에 주문을 한 번 이상 한 사용자만 나온다. 동률은 먼저 첫 주문한 사람이 위.

### SignalR (추가)
- 로그인한 연결에는 본인 계좌가 바뀔 때(주문 접수·체결·취소·초기화) `accountUpdate`(계좌 객체)를 보낸다. 접속 직후에도 1회. 잔고 변동 없는 단일가 진입·종료에 이 알림을 보장하지 않으므로 `bookSnapshot.phase` 변경 시 내 주문을 다시 조회한다.
- 관리자 API(`/api/admin/*`)는 `Admin` 역할 세션(+CSRF) 또는 기존 `X-Admin-Key`로 호출할 수 있다.
