# REVIEW-1A: TASK-001 1단계(규칙 기반) 리뷰

- 대상: `C:\Users\USER\dev\hoga-001`, 브랜치 `task/TASK-001`
- 범위: `6f91afd3b338689324d9fc5f8315340fd0b6cf0b` (merge-base main) .. `bd096cca92eb3150dc0e56325aa33608fe60a23c` (HEAD)
  - 커밋: `2c023b9` 호가단위·가격제한폭, `c139d21` 접속매매·취소, `c3830a2` 조회·불변식, `bd096cc` 검증 기록
- 기준: REVIEW_GUIDE.md, AGENTS.md, docs/tasks/TASK-001.md, docs/RULES.md 1~3장·8장, ADR 0001/0002
- 리뷰어 직접 확인:
  - `dotnet build HogaExchange.slnx -warnaserror`: 경고 0 / 오류 0
  - `dotnet test HogaExchange.slnx`: 41개 통과, 실패 0 (FsCheck I1~I6 각 200회, 코드상 `MaxTest = 200`)
  - 엔진 소스 금지 API 검색(`DateTime|DateTimeOffset|Random|double|float|decimal|Task|Parallel|lock|Stopwatch|TickCount|GetHashCode|Thread|Environment`): 매치 없음
  - `git status` 깨끗함, 추적 파일에 `bin/`, `obj/`, `.env` 없음
  - 상하한가 산식을 기준가격 1~10,000,000원 전 구간에서 따로 계산해 봄: 상한·하한이 모두 자기 가격대 호가단위의 배수였고, 상한 ≥ 기준가 ≥ 하한도 지켜짐. 예외는 기준가격 1원일 때 하한가가 0원이 되는 경우 하나뿐(아래 P1-2)

## 요약

| 등급 | 개수 |
|---|---|
| P0 | 0 |
| P1 | 4 |
| P2 | 5 |

P0 점검 결과(문제 없음):
- 결정성: `_bids`/`_asks`는 `SortedDictionary` + `LinkedList`(FIFO)이고, `Dictionary _active`와 `HashSet _acceptedIds`는 조회(`TryGetValue`/`Contains`/`Add`/`Remove`)에만 씀. 열거는 SortedDictionary와 LinkedList에서만 함(`GetLevels`, `WriteSide`). 문자열 비교는 `StringComparer.Ordinal`, 해시 직렬화는 BinaryWriter(항상 리틀엔디언)라 플랫폼 영향이 없음. 시계·난수·스레드도 없음.
- 정수 가격: 가격·수량은 모두 `long`. `PriceLimits`는 `checked(basePrice * 3) / 10`.
- 규정: 체결가는 늘 대기 호가의 가격(`bestPrice`)이고, 대기 호가는 상하한가·호가단위 검사를 통과한 것만 들어감. 가격 우선(정렬된 First)과 시간 우선(LinkedList 머리)이 지켜짐. 부분 체결은 노드 안에서 `Remaining`만 줄여 같은 자리를 유지함. 잔량이 0이 되면 바로 제거하고, 빈 가격대도 함께 제거함.
- 거부 무해성: 거부 6종은 모두 `_acceptedIds.Add`와 호가창 변경보다 먼저 return함. 순번 오류나 잘못된 Side 예외는 `_lastSequence`를 갱신하기 전에 던짐.
- 불변식·테스트 약화: 이번이 첫 구현이라 지우거나 약하게 바꾼 테스트 없음.

## P1

```
[P1] src/Hoga.Engine/MatchingEngine.Queries.cs:36 - 상태 해시가 호가창만 담아서 ADR 0001의 "같은 상태" 확인과 복구 검증에는 부족함
  근거: 해시에 `_acceptedIds`(체결·취소된 ID 이력), `_lastSequence`, 기준가격·상하한가가 빠져 있다(주석에도 이렇게 적혀 있음). 그래서 해시가 같은 두 엔진이 다음 명령에 다르게 반응할 수 있다. 예: A는 "x"를 접수한 뒤 바로 체결했고 B는 "x"를 받은 적이 없으면, 두 엔진의 호가창은 같아서 해시도 같다. 그런데 다음 명령 Place("x")를 A는 DuplicateOrderId로 거부하고 B는 접수한다. 저널 재생 후 해시를 비교해 복구가 맞았다고 판단하면 이런 차이를 놓친다. 지시서 6절의 문구("호가창 전체")는 충족하므로 P0은 아니다. 같은 이유로 I6도 접수 ID 이력이 그대로인지는 검증하지 못한다(코드를 읽어 보면 거부 경로는 이력을 바꾸지 않는다).
  제안: 해시에 `_lastSequence`, BasePrice/Upper/Lower, 접수 ID 집합(정렬해서 직렬화)을 넣거나 "엔진 상태 해시"와 "호가창 해시"를 나눈다. 적어도 해시가 다루는 범위와 복구 판정에 쓸 수 있는 한계를 ADR 0001이나 주석에 적고, TASK-002(VI 상태 추가)에서 직렬화 형식을 함께 정한다. 메인 에이전트 결정이 필요하다.

[P1] src/Hoga.Engine/PriceLimits.cs:5,17 - 기준가격 1원이면 하한가가 0원이 되어 0원 주문이 접수되고 0원 체결이 가능함 (확인 필요)
  근거: base=1이면 raw=0이고 R-LIMIT-03에 따라 band=1이므로 lower=FloorToTick(0)=0이다. 이때 `IsOnTick(0)`이 true여서 0원 매수·매도 주문이 접수되고 서로 체결될 수 있다. 1~10,000,000원 전 구간을 계산해 보니 하한이 0 이하가 되는 경우는 base=1 하나뿐이었다. RULES.md에는 최저 호가가격 규칙이 없어 규정 위반인지는 확정할 수 없다. 구현 에이전트도 보고(PROGRESS "제안")에 이 점을 적었다.
  제안: `MinBasePrice`를 2로 올리거나, 하한을 1원 이상으로 묶는 규칙(예: R-LIMIT-05 "호가 가격은 1원 이상")을 RULES.md에 근거와 함께 추가한 뒤 구현하고 테스트한다. 규칙 근거 없이 코드만 바꾸지 않는다(AGENTS 규정 정직성).

[P1] tests/Hoga.Engine.Tests/InvariantTests.cs:9, PriceRuleTests.cs:33 - 가격대 경계에서 상하한가 2차 절사와 매칭을 검증하는 테스트가 얇음
  근거: 속성 테스트 I1~I6은 모두 기준가 9,940 하나만 쓴다. 이 경우 상하한가(6,960~12,920)가 한 가격대(10원) 안에 있어서, 호가 범위가 가격대 경계를 넘는 상황(예: 기준가 19,990처럼 상한이 50원 구간에 걸리는 경우, 4,000원 근처처럼 5,000원 경계를 넘는 경우)에서 OffTick 판정과 체결가 검증(I2)을 한 번도 돌리지 않는다. 가격제한폭 테스트도 RULES 예시 4개와 최대 기준가 1개뿐이다. 2,000/5,000/20,000/50,000/200,000/500,000 경계 바로 위·아래 기준가(예: 1,999, 2,000, 4,999, 5,000, 15,390처럼 상한이 20,000 경계에 닿는 값)는 고정 테스트가 없다. 리뷰어가 전 구간을 계산해 본 결과 현재 산식에는 문제가 없었으므로, 버그가 아니라 회귀 방지용 테스트가 빈 것이다.
  제안: (1) 경계 근처 기준가 여러 개의 기대 상하한가를 `Specified_limit_examples`나 별도 Theory로 고정한다(기대값은 손 계산 근거를 주석에 적는다). (2) 속성 테스트에서 기준가를 몇 개(9,940 / 19,990 / 4,000 / 45,000 등) 중에서 고르게 하고, 가격 후보를 엔진의 Lower/Upper와 경계 주변 값으로 만든다.

[P1] tests/Hoga.Engine.Tests/InvariantTests.cs:129-140 - 가격 우선(R-MATCH-01)을 속성 테스트로 검증하지 않음
  근거: I4는 체결이 그 가격대에서 가장 먼저 들어온 주문과 이루어졌는지, 체결가가 대기 호가 가격과 같은지만 본다. 더 좋은 가격의 대기 호가가 남아 있는데 나쁜 가격과 먼저 체결되는 경로는 잡지 못한다. 가격 우선은 예제 테스트 `Better_price_trades_first_at_resting_price`(호가 2개)로만 확인한다. 생성기 가격도 대부분 9,990/10,000/10,010 세 가격대에 몰려 있다.
  제안: I4나 별도 속성에서 체결마다 "반대편에 남아 있는 대기 주문(ExpectedRemaining>0, 순번<현재) 중 최우선 가격 = trade.Price"를 확인하고 `[Trait("Rule","R-MATCH-01")]`를 붙인다. 추적표 R-MATCH-01 행에도 이름을 추가한다.
```

## P2

```
[P2] src/Hoga.Engine/MatchingEngine.cs:74 - Trade.Sequence가 들어온 명령의 순번이라 한 명령에서 나온 체결이 모두 같은 값을 가짐
  근거: 지시서는 `Trade(..., Sequence)`의 뜻을 정하지 않았다. 지금은 체결 고유 번호로 쓸 수 없고, 같은 명령 안의 체결 순서는 리스트 위치로만 알 수 있다.
  제안: 필드 뜻(명령 순번)을 주석이나 문서에 적거나, 다음 작업에서 체결 번호(TradeId)를 따로 둘지 정한다.

[P2] src/Hoga.Engine/MatchingEngine.Queries.cs:47 - 수량 상한이 없어 같은 가격대 잔량 합이 long을 넘으면 GetBook과 GetQueuePosition이 OverflowException을 던짐 (확인 필요)
  근거: 접수 수량은 1~long.MaxValue를 모두 받는다. 체결 계산(`Math.Min`, 차감)은 넘치지 않지만 조회의 `Sum`과 `checked` 덧셈은 예외를 던진다. 구현 에이전트가 보고에서 이미 알렸다. 엔진 상태와 결정성에는 영향이 없다.
  제안: 운영 상한(주문 최대 수량)을 RULES나 설정으로 정할 때 함께 처리한다.

[P2] src/Hoga.Engine/OrderCommand.cs:5,8 - LogicalTimeMs와 AccountId를 받지만 쓰지도 검증하지도 않음, 빈 문자열 OrderId도 허용함
  근거: 지시서 범위에서는 문제가 아니다(시각 단조 증가 검사나 계좌 검증 요구가 없음). 다만 TASK-002의 단일가·VI는 시각을 쓰므로 그때 단조 증가 검사가 필요하다.
  제안: TASK-002 지시서에 "LogicalTimeMs 역행 시 처리"를 명시한다.

[P2] src/Hoga.Engine/MatchingEngine.cs:45 - 잘못된 Side와 null 명령의 예외 경로에 테스트가 없음
  근거: 순번 예외는 테스트하지만(`Sequence_starts_at_one...`), `(Side)99`가 예외를 던지고 순번이 늘지 않는지는 테스트하지 않는다. 저널에 기록된 명령이 예외로 끝났을 때 재생을 어떻게 처리할지도 정해지지 않았다.
  제안: 예외 테스트 하나를 추가하고, 저널 단계(TASK-00x)에서 "예외 명령은 저널에 남기지 않는다" 같은 정책을 정한다.

[P2] docs/RULES.md:103 - 추적표에 일부 관련 테스트가 빠짐
  근거: `BookTests.All_six_reject_reasons_leave_the_book_hash_unchanged`(Trait R-LIMIT-04, R-TICK-02), `MatchingTests.Cancel_removes_the_target_from_the_queue`(Trait R-MATCH-02), `BookTests.Book_and_queue_reflect_partial_fill_and_cancellation`(Trait R-MATCH-01, 02)가 Trait에는 있지만 추적표 해당 행에는 없다. 모든 규칙 ID에 테스트가 최소 하나씩은 있어서 P1 기준(테스트 없음)에는 해당하지 않는다.
  제안: 추적표 행에 이 테스트들을 추가하거나, "Trait 기준으로 대표 테스트만 적는다"는 원칙을 8장에 적는다.
```

## 지시서 완료 조건 대조

| 조건 | 결과 |
|---|---|
| 1. build -warnaserror | 충족 (리뷰어 재실행, 경고·오류 0) |
| 2. test 통과, 테스트 수·반복 횟수 | 충족 (41개, I1~I6 각 200회) |
| 3. 금지 API 검색 | 충족 (매치 없음) |
| 4. git status에 bin/obj/.env 없음 | 충족 |
| 5. 의미 단위 커밋, push 안 함 | 충족 (구현 커밋 3개 + 문서 커밋 1개) |
| 테스트 요구(경계 12개, 예시 4개, 거부 6종 + 해시 불변, 매칭 예제 5종, I1~I6) | 충족. 보강할 점은 P1-3, P1-4 |
| 추적표 R-TICK/R-LIMIT/R-MATCH 채움 | 충족 (P2 보완 사항 있음) |
