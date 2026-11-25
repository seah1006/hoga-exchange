# TASK-001: 엔진 뼈대 - 호가단위, 가격제한폭, 접속매매, 취소

- 브랜치: `task/TASK-001`
- 담당: 구현 에이전트 (Codex)
- 선행 문서: `AGENTS.md`(특히 "엔진 규칙"), `docs/RULES.md` 1~3장, `docs/adr/0001`, `docs/adr/0002`

## 목표
단일 종목의 **결정적 매칭 엔진 핵심**을 만든다. 서버, 화면, 단일가매매, VI는 아직 만들지 않는다. 이 작업이 끝나면 "같은 명령 순서를 넣으면 항상 같은 이벤트와 같은 상태 해시가 나오고, 체결은 항상 KRX 호가단위·상하한가를 지킨다"가 테스트로 증명돼 있어야 한다.

## 만들 것

### 1. 솔루션 구조
- 루트 `HogaExchange.slnx`
- `src/Hoga.Engine` (classlib, net10.0, 외부 NuGet 의존성 없음)
- `tests/Hoga.Engine.Tests` (xUnit, FsCheck.Xunit). 패키지 버전은 net10.0에서 동작하는 최신 안정판을 고르고 보고에 적는다.
- 루트 `Directory.Build.props`: `Nullable=enable`, `TreatWarningsAsErrors=true`, `LangVersion=latest`, `ImplicitUsings=enable`
- `README.md`는 건드리지 않는다(메인 에이전트가 갱신).

### 2. 호가가격단위 (R-TICK-01, R-TICK-02)
- `TickSizeTable`: 가격 → 호가단위. 2023-01-25 이후 표만 구현한다. 구간은 "이상~미만".
- 날짜별 규칙 교체를 나중에 넣을 수 있게, 표를 인터페이스 뒤에 둔다(예: `ITickSizeRule`). 개편 전 표는 만들지 않는다(R-TICK-03은 2단계).
- `IsOnTick(price)`: 가격이 그 가격대 호가단위의 배수인지.
- `FloorToTick(price)`: 그 가격대 호가단위로 내림.

### 3. 가격제한폭 (R-LIMIT-01~04)
- `PriceLimits.Compute(long basePrice, ITickSizeRule rule)` → `(long Upper, long Lower, long Band)`
  1. `raw = basePrice * 3 / 10` (정수 나눗셈)
  2. `band = raw`를 **기준가격의 호가단위**로 절사. 결과가 1 미만이면 1 (R-LIMIT-03)
  3. `upper = basePrice + band`를 **upper 자신의 호가단위**로 절사
  4. `lower = basePrice - band`를 **lower 자신의 호가단위**로 절사
- `basePrice`의 허용 범위(예: 1 이상 10,000,000원 이하, 임의 지정)를 정하고 범위 밖이면 예외. 이 상한에서 곱셈 오버플로가 없음을 테스트한다.

### 4. 엔진 명령과 이벤트
- 명령(입력): `PlaceLimitOrder(OrderId, Side, Price, Quantity, AccountId)`, `CancelOrder(OrderId)`
- 모든 명령은 엔진 밖에서 붙인 `Sequence`(long, 1부터 증가)와 `LogicalTimeMs`(long)를 가진다. 엔진은 순번이 1씩 증가하지 않으면 예외를 던진다.
- 이벤트(출력, 순서 있는 리스트): `OrderAccepted`, `OrderRejected(Reason, RuleId)`, `Trade(BuyOrderId, SellOrderId, Price, Quantity, Sequence)`, `OrderCancelled(RemainingQuantity)`, `OrderFilled`
- 거부 사유(`Reason`)와 규칙 ID:
  | Reason | RuleId |
  |---|---|
  | `InvalidQuantity` (0 이하) | - |
  | `OffTick` | R-TICK-02 |
  | `AboveUpperLimit` | R-LIMIT-04 |
  | `BelowLowerLimit` | R-LIMIT-04 |
  | `DuplicateOrderId` | - |
  | `UnknownOrder` (취소 대상 없음, 이미 체결 완료 포함) | - |
- 엔진 생성 시 `basePrice`와 `ITickSizeRule`을 받아 상하한가를 계산해 둔다.

### 5. 접속매매 (R-MATCH-01~04)
- 지정가만. 가격 우선, 같은 가격은 접수 순번 우선.
- 체결가격은 대기 중이던 호가의 가격 (R-MATCH-03).
- 부분 체결 후 남은 수량은 같은 가격·같은 순번 자리를 유지 (R-MATCH-04).
- 자료구조는 자유. 다만 열거 순서가 결정적이어야 한다(AGENTS.md 엔진 규칙). 선택한 자료구조와 이유를 보고에 적는다. 이번 작업에서는 성능 최적화보다 명확성을 우선한다.

### 6. 조회와 상태 해시
- `GetBook(depth)`: 가격대별 잔량 합계와 주문 수(매수 높은 순, 매도 낮은 순)
- `GetQueuePosition(OrderId)`: 같은 가격대에서 앞에 있는 주문 수와 앞 잔량 합계 ("왜 체결 안 됐나" 설명 패널의 기초)
- `ComputeStateHash()`: 마지막 처리 순번, 가격 설정, 호가단위 규칙 ID, 접수 ID 이력, 호가창 전체를 정해진 순서로 직렬화한 SHA-256 hex. 직렬화 형식을 코드 주석에 적는다.

### 7. 테스트
규칙 테스트에는 `[Trait("Rule", "R-...")]`를 붙인다.
- **호가단위**: 각 가격대의 경계(1,999 / 2,000 / 4,999 / 5,000 / 19,999 / 20,000 / 49,999 / 50,000 / 199,999 / 200,000 / 499,999 / 500,000)에서 호가단위와 `IsOnTick`.
- **가격제한폭**: `docs/RULES.md` 2장 검증 예시 4개(9,940 / 9,980 / 19,990 / 3)를 그대로 테스트로.
- **거부**: 표의 거부 사유마다 최소 1개. 거부는 처리 순번만 전진시킴.
- **매칭 예제**: 가격 우선, 같은 가격 시간 우선, 여러 호가를 한 번에 소진하는 큰 주문, 부분 체결 후 잔량 유지, 취소 후 대기열에서 빠짐.
- **속성 기반 테스트(FsCheck)**: 무작위 명령 수열(가격은 상하한가 안팎과 호가단위 안팎을 섞어 생성)에 대해
  - I1: 명령 처리 후 최고 매수가 < 최저 매도가 (호가창 비교차)
  - I2: 모든 체결가는 상하한가 안이고 `IsOnTick`
  - I3: 주문별로 원래 수량 = 체결 합계 + 잔량 + 취소 수량
  - I4: 같은 가격대에서 체결은 접수 순번 순서
  - I5: 같은 명령 수열을 새 엔진에 두 번 넣으면 이벤트 리스트와 상태 해시가 같다
  - I6: 거부된 명령은 처리 순번 외 상태를 바꾸지 않는다
- `docs/RULES.md` 8장 추적표의 R-TICK, R-LIMIT, R-MATCH 행에 코드 위치와 테스트 이름을 채운다(이 파일 수정은 허용).

## 하지 않을 것
- 단일가매매, VI, 시장가 호가, 저널·재생 파일 저장, 서버, 화면, 봇, 성능 벤치마크
- 외부 NuGet 패키지를 엔진 프로젝트에 추가하는 것

## 완료 조건 (직접 실행하고 결과를 보고)
1. `dotnet build HogaExchange.slnx -warnaserror` 성공
2. `dotnet test HogaExchange.slnx` 통과. 테스트 수와 속성 테스트 반복 횟수를 보고
3. 엔진 소스에 금지 API가 없음을 보여 주는 검색 결과: `DateTime.Now`, `DateTime.UtcNow`, `DateTimeOffset`, `Random`, `double`, `float`, `decimal`, `Task.Run`, `Parallel`, `lock`
4. `git status`에 `bin/`, `obj/`, `.env`가 없음
5. `task/TASK-001` 브랜치에 의미 단위로 나눈 커밋 (push 하지 않음)

## 보고 형식
- 만든 파일 목록(요약), 고른 패키지 버전과 이유
- 호가창 자료구조 선택과 이유
- 상태 해시 직렬화 형식
- 완료 조건 각각의 실제 결과
- 명세가 모호했던 부분과 내린 판단, 남은 문제, 제안
